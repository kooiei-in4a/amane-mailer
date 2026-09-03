using System.Buffers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Attachments.Provider;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Api;

/// <summary>
/// Create mail-request use-case: validation, attachment acceptance, idempotency, insert, and
/// queue signal.
/// </summary>
public static class MailRequestCreateHandler
{
    private const string RedactedRecipientValue = "[REDACTED]";

    private sealed record PreparedAcceptance(
        Guid RequestId,
        DateTimeOffset Now,
        DateTimeOffset? ScheduledAtUtc,
        MailerTenant Tenant,
        CanonicalMailRecipientSet CanonicalRecipients,
        AttachmentAcceptanceResult AttachmentResult);

    private sealed record PreparationOutcome(
        PreparedAcceptance? Acceptance,
        IResult? Error);

    public static async Task<IResult> HandleAsync(
        HttpRequest httpRequest,
        MailRequestCreateRequest request,
        string requestBody,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        AttachmentSpool attachmentSpool,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken,
        MailerRuntimeMetrics? runtimeMetrics = null)
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
        var validationError = ValidateRequest(request, tenant!, now, runtimeMetrics, out var canonicalRecipients);
        if (validationError is not null)
        {
            return validationError;
        }

        var preparation = await PrepareAcceptanceAsync(
            request,
            requestBody,
            tenant!,
            canonicalRecipients!,
            now,
            attachmentSpool,
            runtimeMetrics,
            cancellationToken);
        if (preparation.Error is not null)
        {
            return preparation.Error;
        }

        var prepared = preparation.Acceptance!;
        var existingResult = await ResolveExistingRequestAsync(
            request,
            prepared,
            repository,
            attachmentSpool,
            queue,
            logger,
            cancellationToken);
        if (existingResult is not null)
        {
            return existingResult;
        }

        var commitResult = await CommitAcceptedRequestAsync(
            request,
            requestBody,
            prepared,
            repository,
            attachmentSpool,
            queue,
            logger,
            cancellationToken);
        if (commitResult is not null)
        {
            return commitResult;
        }

        if (IsImmediatelyDispatchable(prepared.ScheduledAtUtc, prepared.Now)
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

    private static async Task<PreparationOutcome> PrepareAcceptanceAsync(
        MailRequestCreateRequest request,
        string requestBody,
        MailerTenant tenant,
        CanonicalMailRecipientSet canonicalRecipients,
        DateTimeOffset now,
        AttachmentSpool attachmentSpool,
        MailerRuntimeMetrics? runtimeMetrics,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.CreateVersion7(now);

        // ADR 0022 D-04 steps 3-7: attachment count, bounded decode, per-file/total size,
        // digest/length, filename, and file-type validation. On failure the request-scoped
        // staging directory is already deleted by the validator.
        var attachmentResult = AttachmentAcceptanceResult.NoAttachments();
        if (request.Attachments is { Count: > 0 })
        {
            attachmentResult = await AttachmentAcceptanceValidator.ValidateAndStageAsync(
                request.Attachments,
                requestId,
                attachmentSpool,
                cancellationToken);

            if (attachmentResult.Status == AttachmentAcceptanceStatus.Failure)
            {
                runtimeMetrics?.RecordAttachmentValidationRejected(attachmentResult.FailureCode!);
                return new(
                    null,
                    MailRequestHttpErrorMapper.Error(
                        StatusCodes.Status422UnprocessableEntity,
                        attachmentResult.FailureCode!));
            }
        }

        // D-04 step 8: payload_hash is computed from the original request body and verified
        // attachment metadata, never from the redacted persisted snapshot.
        string computedHash;
        try
        {
            computedHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(
                requestBody,
                ToHashInputs(attachmentResult.Attachments));
        }
        catch (JsonException)
        {
            attachmentSpool.TryDeleteStaging(requestId);
            return new(
                null,
                MailerJsonResults.ValidationError(
                    MailerErrorCodes.InvalidRequest,
                    "Request body is not valid JSON.",
                    StatusCodes.Status400BadRequest));
        }

        if (!string.Equals(computedHash, request.PayloadHash, StringComparison.Ordinal))
        {
            attachmentSpool.TryDeleteStaging(requestId);
            return new(
                null,
                MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    MailerErrorCodes.InvalidPayloadHash));
        }

        // D-04 step 10: best-effort provider envelope estimate. Worker dispatch performs the
        // authoritative exact pre-serialization check.
        if (attachmentResult.Attachments is { Count: > 0 }
            && !IsWithinProviderEnvelopeEstimate(
                request,
                canonicalRecipients.All,
                tenant,
                attachmentResult.Attachments))
        {
            attachmentSpool.TryDeleteStaging(requestId);
            runtimeMetrics?.RecordAttachmentValidationRejected(MailerErrorCodes.MailPayloadTooLarge);
            return new(
                null,
                MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    MailerErrorCodes.MailPayloadTooLarge));
        }

        return new(
            new PreparedAcceptance(
                requestId,
                now,
                request.ScheduledAt?.ToUniversalTime(),
                tenant,
                canonicalRecipients,
                attachmentResult),
            null);
    }

    private static async Task<IResult?> ResolveExistingRequestAsync(
        MailRequestCreateRequest request,
        PreparedAcceptance prepared,
        MailRequestRepository repository,
        AttachmentSpool attachmentSpool,
        IMailRequestQueue queue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
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
            attachmentSpool.TryDeleteStaging(prepared.RequestId);
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            attachmentSpool.TryDeleteStaging(prepared.RequestId);
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (existing is null)
        {
            return null;
        }

        // The new staging belongs to this request flow until an existing row is found. It is
        // never attached to an already-accepted row, including a conflicting repost.
        attachmentSpool.TryDeleteStaging(prepared.RequestId);

        if (!string.Equals(existing.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status409Conflict,
                MailerErrorCodes.IdempotencyConflict);
        }

        SignalIfDispatchable(queue, existing, prepared.Now, logger);
        return AlreadyAccepted(request.MailRequestId);
    }

    private static async Task<IResult?> CommitAcceptedRequestAsync(
        MailRequestCreateRequest request,
        string requestBody,
        PreparedAcceptance prepared,
        MailRequestRepository repository,
        AttachmentSpool attachmentSpool,
        IMailRequestQueue queue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var insert = CreateAcceptedInsert(request, requestBody, prepared);

        try
        {
            // InsertAcceptedAsync owns the staging -> committed transfer and the transaction
            // rollback cleanup. Once it succeeds, the accepted request lifecycle owns the
            // committed spool; this handler must not delete it.
            await repository.InsertAcceptedAsync(insert, cancellationToken);
        }
        catch (AttachmentStorageUnavailableException)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status503ServiceUnavailable,
                MailerErrorCodes.AttachmentStorageUnavailable);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return await RecoverUniqueRaceAsync(
                request,
                prepared,
                ex,
                repository,
                attachmentSpool,
                queue,
                logger,
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

        return null;
    }

    private static AcceptedMailRequestInsert CreateAcceptedInsert(
        MailRequestCreateRequest request,
        string requestBody,
        PreparedAcceptance prepared)
    {
        // Select the compatibility-only legacy shadow representative from the canonical
        // aggregate. The canonical recipient rows remain the sole delivery source of truth.
        var legacyRecipient = GetLegacyShadowRepresentative(prepared.CanonicalRecipients);
        var attachments = prepared.AttachmentResult.Attachments;

        return new AcceptedMailRequestInsert
        {
            Id = prepared.RequestId,
            TenantId = request.TenantId,
            SourceService = request.SourceService,
            MailRequestId = request.MailRequestId,
            Purpose = request.Purpose,
            // SQLite stores a safe request snapshot, never attachment bytes or recipient PII.
            // payload_hash above remains the hash of the original request body.
            PayloadJson = RedactRecipientPii(
                attachments is { Count: > 0 }
                    ? RedactAttachmentContentBase64(requestBody)
                    : requestBody),
            PayloadHash = request.PayloadHash,
            Subject = request.Subject,
            HtmlBody = request.HtmlBody,
            TextBody = request.TextBody,
            ReplyTo = request.ReplyTo,
            RecipientEmail = legacyRecipient.Address,
            RecipientDisplayName = legacyRecipient.DisplayName,
            Recipients = prepared.CanonicalRecipients.All,
            MetadataJson = request.Metadata is null
                ? null
                : JsonSerializer.Serialize(request.Metadata, MailerJsonContext.Default.DictionaryStringString),
            MaxAttempts = prepared.Tenant.Retry.MaxAttempts,
            AcceptedAt = prepared.Now,
            ScheduledAt = prepared.ScheduledAtUtc,
            Attachments = attachments,
        };
    }

    private static async Task<IResult> RecoverUniqueRaceAsync(
        MailRequestCreateRequest request,
        PreparedAcceptance prepared,
        SqliteException originalException,
        MailRequestRepository repository,
        AttachmentSpool attachmentSpool,
        IMailRequestQueue queue,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The losing insert's committed spool is not referenced by any DB row.
        attachmentSpool.TryDeleteCommitted(prepared.RequestId);

        MailRequestIdempotencyRow? duplicate;
        try
        {
            duplicate = await repository.FindByIdempotencyKeyAsync(
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

        if (duplicate is null)
        {
            if (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(originalException))
            {
                return MailRequestHttpErrorMapper.StorageFull();
            }

            if (MailRequestHttpErrorMapper.IsTransientDatabaseException(originalException))
            {
                return MailRequestHttpErrorMapper.ServiceUnavailable();
            }

            throw originalException;
        }

        if (!string.Equals(duplicate.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status409Conflict,
                MailerErrorCodes.IdempotencyConflict);
        }

        SignalIfDispatchable(queue, duplicate, prepared.Now, logger);
        return AlreadyAccepted(request.MailRequestId);
    }

    private static IResult AlreadyAccepted(Guid mailRequestId) =>
        MailerJsonResults.Accepted(new MailRequestCreateResponse
        {
            MailRequestId = mailRequestId,
            Status = MailRequestAcceptanceStatus.AlreadyAccepted,
        });

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

    private static IReadOnlyList<MailAttachmentHashInput>? ToHashInputs(
        IReadOnlyList<CanonicalAttachmentMetadata>? attachments) =>
        attachments is { Count: > 0 }
            ? attachments
                .Select(attachment => new MailAttachmentHashInput(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.ByteLength,
                    attachment.Sha256Hex))
                .ToArray()
            : null;

    /// <summary>
    /// Rewrites the top-level <c>attachments</c> array (if any) to drop each element's
    /// <c>content_base64</c> before the request body is persisted (ADR 0022 D-04/D-14: raw
    /// attachment content must never land in SQLite or its backups). Every other field --
    /// including the rest of each attachment's declared metadata -- passes through unchanged.
    /// </summary>
    internal static string RedactAttachmentContentBase64(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var bufferWriter = new ArrayBufferWriter<byte>(requestBody.Length);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("attachments") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    foreach (var attachment in property.Value.EnumerateArray())
                    {
                        WriteAttachmentWithoutContentBase64(attachment, writer);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
    }

    /// <summary>
    /// Rewrites top-level To/Cc/Bcc recipient arrays so the persisted payload snapshot contains
    /// no recipient address or display-name PII. The original request JSON remains the input to
    /// payload hash verification; this method only protects the durable operational snapshot.
    /// </summary>
    internal static string RedactRecipientPii(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var bufferWriter = new ArrayBufferWriter<byte>(requestBody.Length);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (IsRecipientRole(property.Name)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    foreach (var recipient in property.Value.EnumerateArray())
                    {
                        WriteRecipientWithoutPii(recipient, writer);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
    }

    private static bool IsRecipientRole(string propertyName) =>
        propertyName is "to" or "cc" or "bcc";

    private static void WriteRecipientWithoutPii(JsonElement recipient, Utf8JsonWriter writer)
    {
        if (recipient.ValueKind != JsonValueKind.Object)
        {
            recipient.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in recipient.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name is "email" or "address" or "display_name")
            {
                writer.WriteStringValue(RedactedRecipientValue);
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteAttachmentWithoutContentBase64(JsonElement attachment, Utf8JsonWriter writer)
    {
        if (attachment.ValueKind != JsonValueKind.Object)
        {
            attachment.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in attachment.EnumerateObject())
        {
            if (property.NameEquals("content_base64"))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    internal static bool IsWithinProviderEnvelopeEstimate(
        MailRequestCreateRequest request,
        IReadOnlyList<CanonicalMailRecipient> recipients,
        MailerTenant tenant,
        IReadOnlyList<CanonicalAttachmentMetadata> attachments)
    {
        var estimate = AttachmentEnvelopeEstimator.EstimateUpperBound(new AttachmentEnvelopeInput(
            tenant.DefaultFrom.Email,
            recipients
                .Select(recipient => new AttachmentEnvelopeRecipient(recipient.Address, recipient.DisplayName))
                .ToArray(),
            request.Subject,
            request.TextBody,
            request.HtmlBody,
            request.ReplyTo,
            attachments
                .Select(attachment => new AttachmentEnvelopeAttachment(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.ByteLength))
                .ToArray()));

        return estimate <= MailAttachmentLimits.MaxProviderEnvelopeBytes;
    }

    internal static CanonicalMailRecipient GetLegacyShadowRepresentative(
        CanonicalMailRecipientSet canonicalRecipients) =>
        canonicalRecipients.All[0];

    private static IResult? ValidateRequest(
        MailRequestCreateRequest request,
        MailerTenant tenant,
        DateTimeOffset now,
        MailerRuntimeMetrics? runtimeMetrics,
        out CanonicalMailRecipientSet? canonicalRecipients)
    {
        if (!MailRecipientValidator.TryValidate(
                request.To,
                request.Cc,
                request.Bcc,
                out canonicalRecipients,
                out var recipientFailure))
        {
            return recipientFailure == MailRecipientValidationFailure.TooManyRecipients
                ? MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    MailerErrorCodes.TooManyRecipients)
                : MailerJsonResults.ValidationError(
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

        if (request.Attachments is { Count: > 0 } attachments
            && attachments.Count > MailAttachmentLimits.MaxAttachmentCount)
        {
            runtimeMetrics?.RecordAttachmentValidationRejected(MailerErrorCodes.TooManyAttachments);
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.TooManyAttachments);
        }

        var scheduleError = MailRequestScheduleValidator.ValidateScheduledAt(request.ScheduledAt, now);
        if (scheduleError is not null)
        {
            return scheduleError;
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
