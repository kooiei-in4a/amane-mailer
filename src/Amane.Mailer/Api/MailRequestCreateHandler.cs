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
        var validationError = ValidateRequest(request, tenant!, now, runtimeMetrics);
        if (validationError is not null)
        {
            return validationError;
        }

        var scheduledAtUtc = request.ScheduledAt?.ToUniversalTime();
        var requestId = Guid.CreateVersion7(now);

        // ADR 0022 D-04 steps 3-7: attachment count, bounded decode, per-file/total size,
        // digest/length, filename, and file-type validation. On failure the request-scoped
        // staging directory is already deleted by the validator; nothing else to clean up.
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
                return MailRequestHttpErrorMapper.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    attachmentResult.FailureCode!);
            }
        }

        var attachmentHashInputs = ToHashInputs(attachmentResult.Attachments);

        // D-04 step 8: canonical metadata + payload_hash recompute. Attachment-bearing requests
        // hash the *verified* attachment values, never the raw declared content_type/content_base64.
        string computedHash;
        try
        {
            computedHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(requestBody, attachmentHashInputs);
        }
        catch (JsonException)
        {
            attachmentSpool.TryDeleteStaging(requestId);
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid JSON.",
                StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(computedHash, request.PayloadHash, StringComparison.Ordinal))
        {
            attachmentSpool.TryDeleteStaging(requestId);
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.InvalidPayloadHash);
        }

        // D-04 step 10: provider envelope pre-check (best-effort estimate; the authoritative
        // gate is re-checked at Worker dispatch time with exact pre-serialization).
        if (attachmentResult.Attachments is { Count: > 0 }
            && !IsWithinProviderEnvelopeEstimate(request, tenant!, attachmentResult.Attachments))
        {
            attachmentSpool.TryDeleteStaging(requestId);
            runtimeMetrics?.RecordAttachmentValidationRejected(MailerErrorCodes.MailPayloadTooLarge);
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.MailPayloadTooLarge);
        }

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
            attachmentSpool.TryDeleteStaging(requestId);
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            attachmentSpool.TryDeleteStaging(requestId);
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (existing is not null)
        {
            // Idempotent repost (ADR 0022 D-08): the new staging is discarded; if identity
            // matches, the already-committed request stands. Decode/digest/type validation
            // above was never skipped just because an existing row was found (D-04).
            attachmentSpool.TryDeleteStaging(requestId);

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

        // Guaranteed by the legacy-shape gate in ValidateRequest (IsLegacySingleTo): exactly one
        // To recipient, no Cc/Bcc.
        var recipient = request.To![0];
        var insert = new AcceptedMailRequestInsert
        {
            Id = requestId,
            TenantId = request.TenantId,
            SourceService = request.SourceService,
            MailRequestId = request.MailRequestId,
            Purpose = request.Purpose,
            // ADR 0022 D-04: the raw request body is stored for audit/debugging, but it must
            // never carry attachment content_base64 into SQLite (and its backups) -- attachment
            // binaries live only in the short-lived spool. payload_hash (compared for
            // idempotency) is computed from requestBody above, before this redaction.
            PayloadJson = attachmentResult.Attachments is { Count: > 0 }
                ? RedactAttachmentContentBase64(requestBody)
                : requestBody,
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
            Attachments = attachmentResult.Attachments,
        };

        try
        {
            await repository.InsertAcceptedAsync(insert, cancellationToken);
        }
        catch (AttachmentStorageUnavailableException)
        {
            // ADR 0022 D-09: a backup maintenance lease is held. The committed spool for this
            // attempt was already removed inside InsertAcceptedAsync's own catch block.
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status503ServiceUnavailable,
                MailerErrorCodes.AttachmentStorageUnavailable);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The spool commit (staging -> committed) already ran before this transaction
            // opened; our internal id lost the (tenant, source_service, mail_request_id) race,
            // so it will never be referenced by a DB row. Clean it up promptly rather than
            // waiting for reconciliation.
            attachmentSpool.TryDeleteCommitted(requestId);

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

    private static bool IsWithinProviderEnvelopeEstimate(
        MailRequestCreateRequest request,
        MailerTenant tenant,
        IReadOnlyList<CanonicalAttachmentMetadata> attachments)
    {
        // Guaranteed by the legacy-shape gate in ValidateRequest (IsLegacySingleTo).
        var recipient = request.To![0];
        var estimate = AttachmentEnvelopeEstimator.EstimateUpperBound(new AttachmentEnvelopeInput(
            tenant.DefaultFrom.Email,
            recipient.Email,
            recipient.DisplayName,
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

    private static IResult? ValidateRequest(
        MailRequestCreateRequest request,
        MailerTenant tenant,
        DateTimeOffset now,
        MailerRuntimeMetrics? runtimeMetrics)
    {
        if (!MailRecipientValidator.TryValidate(
                request.To,
                request.Cc,
                request.Bcc,
                out var canonicalRecipients,
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

        // Temporary gate (ADR 0023 / issue #540): Contracts/OpenAPI/validation/hash accept
        // multiple To and Cc/Bcc, but recipient persistence and delivery (a separate, not-yet-
        // implemented follow-up) still only handle a single To recipient. Reject any other shape
        // here -- before attachment staging, hash verification, or any DB write -- so a
        // multi-recipient request is never silently reduced to one recipient. No recipient values
        // are included in the response.
        if (!canonicalRecipients!.IsLegacySingleTo)
        {
            return MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Only a single To recipient is currently accepted.",
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
