using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
using Amane.Mailer.Queue;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Api;

/// <summary>
/// Create mail-request use-case: validation, idempotency, insert, and queue signal.
/// </summary>
public static class MailRequestCreateHandler
{
    public static async Task<IResult> HandleAsync(
        HttpRequest httpRequest,
        MailRequestCreateRequest request,
        string requestBody,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bearerToken = TenantRequestAuthorizer.ReadBearerToken(httpRequest);
        if (!TenantRequestAuthorizer.TryAuthorizeCreate(
                tenantRegistry,
                request.TenantId,
                request.SourceService,
                bearerToken,
                out var tenant,
                out var authError))
        {
            return authError!;
        }

        var now = timeProvider.GetUtcNow();
        var validationError = ValidateRequest(request, requestBody, tenant!, now);
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
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
            {
                return MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status409Conflict,
                    MailerErrorCodes.IdempotencyConflict);
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
            MaxAttempts = tenant!.Retry.MaxAttempts,
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
            catch (Exception duplicateReadException) when (
                MailRequestHttpErrorMapper.IsStorageFullDatabaseException(duplicateReadException))
            {
                return MailRequestHttpErrorMapper.StorageFull();
            }
            catch (Exception duplicateReadException) when (
                MailRequestHttpErrorMapper.IsTransientDatabaseException(duplicateReadException))
            {
                return MailRequestHttpErrorMapper.ServiceUnavailable();
            }

            if (duplicate is null)
            {
                if (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
                {
                    return MailRequestHttpErrorMapper.StorageFull();
                }

                if (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
                {
                    return MailRequestHttpErrorMapper.ServiceUnavailable();
                }

                throw;
            }

            if (!string.Equals(duplicate.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
            {
                return MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status409Conflict,
                    MailerErrorCodes.IdempotencyConflict);
            }

            SignalIfDispatchable(queue, duplicate, now, logger);

            return MailerJsonResults.Accepted(new MailRequestCreateResponse
            {
                MailRequestId = request.MailRequestId,
                Status = MailRequestAcceptanceStatus.AlreadyAccepted,
            });
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (IsImmediatelyDispatchable(scheduledAtUtc, now)
            && !queue.TrySignalWorkAvailable())
        {
            logger.LogWarning(
                "WorkAvailable channel is full; request {MailRequestId} will be picked up by sweep.",
                request.MailRequestId);
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
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.TooManyRecipients);
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

        if (!IsValidMetadata(request, tenant.EffectiveMetadataMaxBytes))
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.InvalidMetadata);
        }

        var scheduleError = MailRequestScheduleValidator.ValidateScheduledAt(request.ScheduledAt, now);
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
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.InvalidPayloadHash);
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
}
