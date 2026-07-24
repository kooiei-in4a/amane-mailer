using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
using Amane.Mailer.Queue;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Webhooks;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Api;

public static class MailRequestEndpoints
{
    private const int MaxRequestBodyBytes = 256_000;

    // Reject invalid UTF-8 instead of substituting U+FFFD (#343).
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IEndpointRouteBuilder MapMailRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/mail-requests", CreateMailRequestAsync);
        endpoints.MapGet("/internal/mail-requests/{mailRequestId}", GetMailRequestStatusAsync);
        endpoints.MapPost("/internal/mail-requests/{mailRequestId}/cancel", CancelMailRequestAsync);
        endpoints.MapPost("/internal/mail-requests/{mailRequestId}/reschedule", RescheduleMailRequestAsync);
        return endpoints;
    }

    private static async Task<IResult> GetMailRequestStatusAsync(
        string mailRequestId,
        HttpRequest httpRequest,
        MailRequestRepository repository,
        MailerTenantRegistry tenantRegistry,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeScopedMailRequest(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        MailRequestStatusRow? statusRow;
        try
        {
            statusRow = await repository.GetStatusByIdempotencyKeyAsync(
                tenantId,
                sourceService,
                parsedMailRequestId,
                cancellationToken);
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (statusRow is null)
        {
            return Error(StatusCodes.Status404NotFound, MailerErrorCodes.NotFound);
        }

        return MailerJsonResults.Ok(new MailRequestStatusResponse
        {
            MailRequestId = statusRow.MailRequestId,
            Status = ToDeliveryStatus(statusRow.Status),
            AttemptCount = statusRow.AttemptCount,
            MaxAttempts = statusRow.MaxAttempts,
            NextAttemptAt = statusRow.NextAttemptAt,
            ScheduledAt = statusRow.ScheduledAt,
            AcceptedAt = statusRow.AcceptedAt,
            DeliveredAt = statusRow.DeliveredAt,
            LastErrorCode = statusRow.LastErrorCode,
        });
    }

    private static async Task<IResult> CancelMailRequestAsync(
        string mailRequestId,
        HttpRequest httpRequest,
        MailRequestRepository repository,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeScopedMailRequest(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        var now = timeProvider.GetUtcNow();
        ConsumerMailRequestMutationResult result;
        try
        {
            result = await repository.TryConsumerCancelAsync(
                tenantId,
                sourceService,
                parsedMailRequestId,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (result.Status == ManualMailRequestMutationStatus.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, MailerErrorCodes.NotFound);
        }

        if (result.Status != ManualMailRequestMutationStatus.Succeeded)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.InvalidState);
        }

        // Cancel is already committed. Webhook enqueue is best-effort; reconcile covers gaps (#269).
        if (result.InternalRequestId is Guid internalId)
        {
            try
            {
                await deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(internalId, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
                logger.LogWarning(
                    ex,
                    "Failed to enqueue cancel delivery event for mail request {MailRequestId}.",
                    parsedMailRequestId);
            }
        }

        // Prefer the committed snapshot over a post-commit re-read so non-transient re-read
        // faults cannot turn a successful cancel into HTTP 5xx (#269).
        return StatusOk(result.StatusSnapshot!);
    }

    private static async Task<IResult> RescheduleMailRequestAsync(
        string mailRequestId,
        HttpRequest httpRequest,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorizeScopedMailRequest(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        string requestBody;
        try
        {
            requestBody = await ReadRequestBodyAsync(httpRequest, cancellationToken);
        }
        catch (RequestBodyTooLargeException)
        {
            return MailerJsonResults.Error(
                MailerErrorCodes.RequestTooLarge,
                StatusCodes.Status413PayloadTooLarge);
        }
        catch (RequestBodyInvalidUtf8Exception)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid UTF-8.",
                StatusCodes.Status400BadRequest);
        }

        MailRequestRescheduleRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                requestBody,
                MailerJsonContext.Default.MailRequestRescheduleRequest);
        }
        catch (JsonException)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid JSON.",
                StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is required.",
                StatusCodes.Status400BadRequest);
        }

        if (JsonDuplicatePropertyDetector.HasDuplicateProperty(requestBody))
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body contains a duplicate JSON property.",
                StatusCodes.Status400BadRequest);
        }

        var now = timeProvider.GetUtcNow();
        var scheduleError = ValidateScheduledAt(request.ScheduledAt, now);
        if (scheduleError is not null)
        {
            return scheduleError;
        }

        var scheduledAtUtc = request.ScheduledAt?.ToUniversalTime();
        ConsumerMailRequestMutationResult result;
        try
        {
            result = await repository.TryRescheduleAsync(
                tenantId,
                sourceService,
                parsedMailRequestId,
                scheduledAtUtc,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (result.Status == ManualMailRequestMutationStatus.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, MailerErrorCodes.NotFound);
        }

        if (result.Status != ManualMailRequestMutationStatus.Succeeded)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.InvalidState);
        }

        if (IsImmediatelyDispatchable(scheduledAtUtc, now))
        {
            var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
            if (!queue.TrySignalWorkAvailable())
            {
                logger.LogWarning(
                    "WorkAvailable channel is full after reschedule for request {MailRequestId}.",
                    parsedMailRequestId);
            }
        }

        // Prefer the committed snapshot over a post-commit re-read so non-transient re-read
        // faults cannot turn a successful reschedule into HTTP 5xx (#327).
        return StatusOk(result.StatusSnapshot!);
    }

    private static async Task<IResult> CreateMailRequestAsync(
        HttpRequest httpRequest,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
        if (httpRequest.ContentLength > MaxRequestBodyBytes)
        {
            return MailerJsonResults.Error(
                MailerErrorCodes.RequestTooLarge,
                StatusCodes.Status413PayloadTooLarge);
        }

        string requestBody;
        try
        {
            requestBody = await ReadRequestBodyAsync(httpRequest, cancellationToken);
        }
        catch (RequestBodyTooLargeException)
        {
            return MailerJsonResults.Error(
                MailerErrorCodes.RequestTooLarge,
                StatusCodes.Status413PayloadTooLarge);
        }
        catch (RequestBodyInvalidUtf8Exception)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid UTF-8.",
                StatusCodes.Status400BadRequest);
        }

        MailRequestCreateRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(requestBody, MailerJsonContext.Default.MailRequestCreateRequest);
        }
        catch (JsonException)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid JSON.",
                StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is required.",
                StatusCodes.Status400BadRequest);
        }

        if (JsonDuplicatePropertyDetector.HasDuplicateProperty(requestBody))
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body contains a duplicate JSON property.",
                StatusCodes.Status400BadRequest);
        }

        var bearerToken = ReadBearerToken(httpRequest);
        var tenant = tenantRegistry.Authorize(request.TenantId, bearerToken);
        if (tenant is null)
        {
            return Error(StatusCodes.Status401Unauthorized, MailerErrorCodes.UnauthorizedTenant);
        }

        if (!tenant.IsSourceServiceAllowed(request.SourceService))
        {
            return Error(StatusCodes.Status403Forbidden, MailerErrorCodes.SourceServiceNotAllowed);
        }

        var now = timeProvider.GetUtcNow();
        var validationError = ValidateRequest(request, requestBody, tenant, now);
        if (validationError is not null)
        {
            return validationError;
        }

        var scheduledAtUtc = request.ScheduledAt?.ToUniversalTime();

        MailRequestIdempotencyRow? existing;
        try
        {
            existing = await repository.FindByIdempotencyKeyAsync(
                request.TenantId,
                request.SourceService,
                request.MailRequestId,
                cancellationToken);
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status409Conflict, MailerErrorCodes.IdempotencyConflict);
            }

            SignalIfDispatchable(queue, existing, now, logger);

            return MailerJsonResults.Accepted(new MailRequestCreateResponse
            {
                MailRequestId = request.MailRequestId,
                Status = MailRequestAcceptanceStatus.AlreadyAccepted,
            });
        }

        var recipient = request.To[0];
        var insert = new AcceptedMailRequestInsert
        {
            Id = Guid.CreateVersion7(now),
            TenantId = request.TenantId,
            SourceService = request.SourceService,
            MailRequestId = request.MailRequestId,
            Purpose = request.Purpose,
            PayloadJson = requestBody,
            PayloadHash = request.PayloadHash,
            Subject = request.Subject,
            HtmlBody = request.HtmlBody,
            TextBody = request.TextBody,
            ReplyTo = request.ReplyTo,
            RecipientEmail = recipient.Email,
            RecipientDisplayName = recipient.DisplayName,
            MetadataJson = request.Metadata is null
                ? null
                : JsonSerializer.Serialize(request.Metadata, MailerJsonContext.Default.DictionaryStringString),
            MaxAttempts = tenant.Retry.MaxAttempts,
            AcceptedAt = now,
            ScheduledAt = scheduledAtUtc,
        };

        try
        {
            await repository.InsertAcceptedAsync(insert, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            MailRequestIdempotencyRow? duplicate;
            try
            {
                duplicate = await repository.FindByIdempotencyKeyAsync(
                    request.TenantId,
                    request.SourceService,
                    request.MailRequestId,
                    cancellationToken);
            }
            catch (Exception duplicateReadException) when (IsStorageFullDatabaseException(duplicateReadException))
            {
                return StorageFull();
            }
            catch (Exception duplicateReadException) when (IsTransientDatabaseException(duplicateReadException))
            {
                return ServiceUnavailable();
            }

            if (duplicate is null)
            {
                if (IsStorageFullDatabaseException(ex))
                {
                    return StorageFull();
                }

                if (IsTransientDatabaseException(ex))
                {
                    return ServiceUnavailable();
                }

                throw;
            }

            if (!string.Equals(duplicate.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
            {
                return Error(StatusCodes.Status409Conflict, MailerErrorCodes.IdempotencyConflict);
            }

            SignalIfDispatchable(queue, duplicate, now, logger);

            return MailerJsonResults.Accepted(new MailRequestCreateResponse
            {
                MailRequestId = request.MailRequestId,
                Status = MailRequestAcceptanceStatus.AlreadyAccepted,
            });
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (IsImmediatelyDispatchable(scheduledAtUtc, now)
            && !queue.TrySignalWorkAvailable())
        {
            logger.LogWarning("WorkAvailable channel is full; request {MailRequestId} will be picked up by sweep.", request.MailRequestId);
        }

        return MailerJsonResults.Accepted(new MailRequestCreateResponse
        {
            MailRequestId = request.MailRequestId,
            Status = MailRequestAcceptanceStatus.Accepted,
        });
    }

    internal static bool IsDispatchableQueued(MailRequestIdempotencyRow row, DateTimeOffset now) =>
        row.Status == MailRequestState.Queued
        && (row.NextAttemptAt is null || row.NextAttemptAt <= now)
        && (row.ScheduledAt is null || row.ScheduledAt <= now);

    internal static bool IsImmediatelyDispatchable(DateTimeOffset? scheduledAtUtc, DateTimeOffset now) =>
        scheduledAtUtc is null || scheduledAtUtc <= now;

    private static void SignalIfDispatchable(
        IMailRequestQueue queue,
        MailRequestIdempotencyRow row,
        DateTimeOffset now,
        ILogger logger)
    {
        if (!IsDispatchableQueued(row, now))
        {
            return;
        }

        if (!queue.TrySignalWorkAvailable())
        {
            logger.LogWarning(
                "WorkAvailable channel is full on already_accepted for request id {RequestId}.",
                row.Id);
        }
    }

    private static IResult? ValidateRequest(
        MailRequestCreateRequest request,
        string requestBody,
        MailerTenant tenant,
        DateTimeOffset now)
    {
        if (request.To is null)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "A valid recipient is required.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (request.To.Count > 1)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.TooManyRecipients);
        }

        if (request.To.Count == 0
            || request.To.Any(recipient => recipient is null || !MailAddress.TryCreate(recipient.Email, out _)))
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "A valid recipient is required.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (!string.IsNullOrWhiteSpace(request.ReplyTo)
            && !MailAddress.TryCreate(request.ReplyTo, out _))
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "ReplyTo must be a valid email address.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (string.IsNullOrWhiteSpace(request.Subject)
            || (string.IsNullOrWhiteSpace(request.HtmlBody) && string.IsNullOrWhiteSpace(request.TextBody)))
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Subject and at least one body are required.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (!IsValidMetadata(request, tenant.MetadataMaxBytes))
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.InvalidMetadata);
        }

        var scheduleError = ValidateScheduledAt(request.ScheduledAt, now);
        if (scheduleError is not null)
        {
            return scheduleError;
        }

        string computedHash;
        try
        {
            computedHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(requestBody);
        }
        catch (JsonException)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid JSON.",
                StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(computedHash, request.PayloadHash, StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.InvalidPayloadHash);
        }

        return null;
    }

    internal static IResult? ValidateScheduledAt(DateTimeOffset? scheduledAt, DateTimeOffset now)
    {
        if (scheduledAt is null)
        {
            return null;
        }

        var scheduledAtUtc = scheduledAt.Value.ToUniversalTime();
        if (scheduledAtUtc < now)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.ScheduledAtInPast);
        }

        if (scheduledAtUtc > now.Add(MailRequestScheduleLimits.MaxScheduledAhead))
        {
            return Error(StatusCodes.Status422UnprocessableEntity, MailerErrorCodes.ScheduledAtTooFar);
        }

        return null;
    }

    private static bool IsValidMetadata(MailRequestCreateRequest request, int metadataMaxBytes)
    {
        if (request.Metadata is null)
        {
            return true;
        }

        var serialized = JsonSerializer.Serialize(request.Metadata, MailerJsonContext.Default.DictionaryStringString);
        if (Encoding.UTF8.GetByteCount(serialized) > metadataMaxBytes)
        {
            return false;
        }

        return request.Metadata.Keys.All(key =>
            !key.Contains("token", StringComparison.OrdinalIgnoreCase)
            && !key.Contains("password", StringComparison.OrdinalIgnoreCase)
            && !key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            && !key.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadRequestBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int totalBytes = 0;

        while (true)
        {
            var bytesRead = await request.Body.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxRequestBodyBytes)
            {
                throw new RequestBodyTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        try
        {
            return StrictUtf8.GetString(buffer.ToArray());
        }
        catch (DecoderFallbackException)
        {
            throw new RequestBodyInvalidUtf8Exception();
        }
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static bool TryAuthorizeScopedMailRequest(
        HttpRequest httpRequest,
        MailerTenantRegistry tenantRegistry,
        string mailRequestId,
        out Guid tenantId,
        out string sourceService,
        out Guid parsedMailRequestId,
        out IResult? error)
    {
        tenantId = default;
        sourceService = string.Empty;
        parsedMailRequestId = default;
        error = null;

        if (!Guid.TryParse(mailRequestId, out parsedMailRequestId))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "mail_request_id must be a UUID.",
                StatusCodes.Status400BadRequest);
            return false;
        }

        if (!TryParseRequiredGuidQuery(httpRequest, "tenant_id", out tenantId, out var queryParseError))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                queryParseError,
                StatusCodes.Status400BadRequest);
            return false;
        }

        if (!TryParseRequiredSourceServiceQuery(httpRequest, out sourceService, out queryParseError))
        {
            error = MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                queryParseError,
                StatusCodes.Status400BadRequest);
            return false;
        }

        var bearerToken = ReadBearerToken(httpRequest);
        var tenant = tenantRegistry.Authorize(tenantId, bearerToken);
        if (tenant is null)
        {
            error = Error(StatusCodes.Status401Unauthorized, MailerErrorCodes.UnauthorizedTenant);
            return false;
        }

        if (!tenant.IsSourceServiceAllowed(sourceService))
        {
            error = Error(StatusCodes.Status403Forbidden, MailerErrorCodes.SourceServiceNotAllowed);
            return false;
        }

        return true;
    }

    private static async Task<IResult> GetStatusResultAsync(
        MailRequestRepository repository,
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        MailRequestStatusRow? statusRow;
        try
        {
            statusRow = await repository.GetStatusByIdempotencyKeyAsync(
                tenantId,
                sourceService,
                mailRequestId,
                cancellationToken);
        }
        catch (Exception ex) when (IsStorageFullDatabaseException(ex))
        {
            return StorageFull();
        }
        catch (Exception ex) when (IsTransientDatabaseException(ex))
        {
            return ServiceUnavailable();
        }

        if (statusRow is null)
        {
            return Error(StatusCodes.Status404NotFound, MailerErrorCodes.NotFound);
        }

        return StatusOk(statusRow);
    }

    private static IResult StatusOk(MailRequestStatusRow statusRow) =>
        MailerJsonResults.Ok(new MailRequestStatusResponse
        {
            MailRequestId = statusRow.MailRequestId,
            Status = ToDeliveryStatus(statusRow.Status),
            AttemptCount = statusRow.AttemptCount,
            MaxAttempts = statusRow.MaxAttempts,
            NextAttemptAt = statusRow.NextAttemptAt,
            ScheduledAt = statusRow.ScheduledAt,
            AcceptedAt = statusRow.AcceptedAt,
            DeliveredAt = statusRow.DeliveredAt,
            LastErrorCode = statusRow.LastErrorCode,
        });

    private static IResult Error(int statusCode, string code) =>
        MailerJsonResults.Error(code, statusCode);

    private static IResult ServiceUnavailable() =>
        MailerJsonResults.ServiceUnavailable();

    private static IResult StorageFull() =>
        MailerJsonResults.StorageFull();

    private static bool IsStorageFullDatabaseException(Exception exception) =>
        SqliteDatabaseExceptionClassifier.IsStorageFull(exception);

    private static bool IsTransientDatabaseException(Exception exception) =>
        SqliteDatabaseExceptionClassifier.IsTransient(exception);

    private static bool TryParseRequiredGuidQuery(
        HttpRequest request,
        string name,
        out Guid value,
        out string error)
    {
        if (!request.Query.TryGetValue(name, out var rawValues) || rawValues.Count == 0)
        {
            value = default;
            error = $"{name} is required.";
            return false;
        }

        if (!Guid.TryParse(rawValues[0], out value))
        {
            error = $"{name} must be a UUID.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseRequiredSourceServiceQuery(
        HttpRequest request,
        out string value,
        out string error)
    {
        if (!request.Query.TryGetValue("source_service", out var rawValues) || rawValues.Count == 0)
        {
            value = string.Empty;
            error = "source_service is required.";
            return false;
        }

        value = rawValues[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "source_service must not be empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string ToDeliveryStatus(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => MailRequestStatus.Queued,
            MailRequestState.Processing => MailRequestStatus.Processing,
            MailRequestState.Delivered => MailRequestStatus.Delivered,
            MailRequestState.Failed => MailRequestStatus.Failed,
            MailRequestState.DeadLettered => MailRequestStatus.DeadLettered,
            MailRequestState.Cancelled => MailRequestStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private sealed class RequestBodyTooLargeException : Exception;

    private sealed class RequestBodyInvalidUtf8Exception : Exception;
}
