using Amane.Mailer.Admin;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestAcceptStore(
    SqliteConnectionFactory connections,
    AttachmentSpool? attachmentSpool = null,
    MailerRuntimeMetrics? runtimeMetrics = null,
    MailerMaintenanceLeaseStore? maintenanceLeaseStore = null)
{
    private readonly MailerRuntimeMetrics? _runtimeMetrics = runtimeMetrics;

    public async Task<MailRequestIdempotencyRow?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, payload_hash, status, next_attempt_at, scheduled_at
            FROM mail_requests
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MailRequestIdempotencyRow
        {
            Id = Guid.Parse(reader.GetString(0)),
            PayloadHash = reader.GetString(1),
            Status = (MailRequestState)reader.GetInt32(2),
            NextAttemptAt = reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)),
            ScheduledAt = reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
        };
    }

    public async Task<MailRequestStatusRow?> GetStatusByIdempotencyKeyAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                mr.mail_request_id,
                mr.status,
                mr.attempt_count,
                mr.max_attempts,
                mr.next_attempt_at,
                mr.scheduled_at,
                mr.accepted_at,
                mr.delivered_at,
                COALESCE((
                    SELECT ma.error_code
                    FROM mail_attempts ma
                    WHERE ma.request_id = mr.id
                      AND IFNULL(ma.error_code, '') <> @SupersededErrorCode
                    ORDER BY ma.id DESC
                    LIMIT 1
                ), '') AS last_error_code
            FROM mail_requests mr
            WHERE mr.tenant_id = @TenantId
              AND mr.source_service = @SourceService
              AND mr.mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue(
            "@SupersededErrorCode",
            MailRequestConsumerMutations.SupersededByManualRetryErrorCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lastErrorCode = reader.GetString(8);
        return new MailRequestStatusRow(
            MailRequestId: Guid.Parse(reader.GetString(0)),
            Status: (MailRequestState)reader.GetInt32(1),
            AttemptCount: reader.GetInt32(2),
            MaxAttempts: reader.GetInt32(3),
            NextAttemptAt: reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
            ScheduledAt: reader.IsDBNull(5) ? null : SqliteTime.FromStorage(reader.GetString(5)),
            AcceptedAt: SqliteTime.FromStorage(reader.GetString(6)),
            DeliveredAt: reader.IsDBNull(7) ? null : SqliteTime.FromStorage(reader.GetString(7)),
            LastErrorCode: string.IsNullOrEmpty(lastErrorCode) ? null : lastErrorCode);
    }

    public async Task InsertAcceptedAsync(
        AcceptedMailRequestInsert insert,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts, scheduled_at, attachment_count,
                accepted_api_key_id, accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                @PayloadJson, @PayloadHash, @Subject, @HtmlBody, @TextBody, @ReplyTo,
                @RecipientEmail, @RecipientDisplayName, @MetadataJson,
                @Status, 0, @MaxAttempts, @ScheduledAt, @AttachmentCount,
                @AcceptedApiKeyId, @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;

        const string insertAttachmentSql = """
            INSERT INTO mail_request_attachments (
                id, request_id, attachment_order, file_name, content_type,
                byte_length, content_sha256, spool_key, created_at)
            VALUES (
                @Id, @RequestId, @Order, @FileName, @ContentType,
                @ByteLength, @ContentSha256, @SpoolKey, @CreatedAt);
            """;

        const string insertRecipientSql = """
            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key, display_name,
                delivery_state, provider_message_id, provider_status_detail, created_at, updated_at)
            VALUES (
                @RequestId, @RecipientRole, @Ordinal, @Address, @AddressKey, @DisplayName,
                @DeliveryState, NULL, NULL, @CreatedAt, @UpdatedAt);
            """;

        var nowStorage = SqliteTime.ToStorageUtc(insert.AcceptedAt);
        var attachments = insert.Attachments;
        var recipients = insert.Recipients ??
        [
            new CanonicalMailRecipient
            {
                Role = MailRecipientRole.To,
                Ordinal = 0,
                Address = insert.RecipientEmail,
                AddressKey = RecipientEmailNormalizer.Normalize(insert.RecipientEmail),
                DisplayName = insert.RecipientDisplayName,
            },
        ];
        var isBccOnly = recipients.Count > 0
            && recipients.All(recipient => recipient.Role == MailRecipientRole.Bcc);
        var legacyRecipientEmail = isBccOnly
            ? MailRequestLegacyShadow.BccOnlyRecipientEmail
            : insert.RecipientEmail;
        var legacyRecipientDisplayName = isBccOnly
            ? MailRequestLegacyShadow.BccOnlyRecipientDisplayName
            : insert.RecipientDisplayName;

        // Spool commit (atomic staging -> committed rename) happens before the SQLite
        // transaction opens (ADR 0022 D-08 steps 4-5). Until BEGIN IMMEDIATE succeeds,
        // this method owns immediate cleanup of the committed spool on failure.
        var cleanupCommittedBeforeTransaction = false;
        if (attachments is { Count: > 0 } && attachmentSpool is not null)
        {
            attachmentSpool.CommitStagingToCommitted(insert.Id);
            cleanupCommittedBeforeTransaction = true;
        }

        SqliteConnection connection;
        SqliteImmediateTransaction transaction;
        try
        {
            connection = await connections.OpenConnectionAsync(cancellationToken);
            try
            {
                transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch
        {
            if (cleanupCommittedBeforeTransaction)
            {
                attachmentSpool?.TryDeleteCommitted(insert.Id);
            }

            throw;
        }

        // From this point, the existing transaction catch owns rollback and committed-spool
        // cleanup. Disable the pre-transaction guard so the same spool is never deleted twice.
        cleanupCommittedBeforeTransaction = false;
        await using var connectionScope = connection;
        await using var transactionScope = transaction;
        try
        {
            // ADR 0022 D-09: the acceptance transaction checks the backup maintenance lease
            // inside the same SQLite transaction as the insert. A held lease means a backup
            // snapshot may be in flight; spool/DB commit does not proceed, and the caller must
            // surface 503 ATTACHMENT_STORAGE_UNAVAILABLE without leaving a committed spool
            // orphan (the exception path below removes it).
            if (attachments is { Count: > 0 } && maintenanceLeaseStore is not null
                && await MailerMaintenanceLeaseStore.IsHeldWithinTransactionAsync(
                    connection, MailerMaintenanceLeaseStore.BackupLeaseName, insert.AcceptedAt, cancellationToken))
            {
                throw new AttachmentStorageUnavailableException(
                    "Attachment acceptance is temporarily unavailable while a backup is in progress.");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.Parameters.AddWithValue("@Id", insert.Id.ToString("D"));
                command.Parameters.AddWithValue("@TenantId", insert.TenantId.ToString("D"));
                command.Parameters.AddWithValue("@SourceService", insert.SourceService);
                command.Parameters.AddWithValue("@MailRequestId", insert.MailRequestId.ToString("D"));
                command.Parameters.AddWithValue("@Purpose", insert.Purpose);
                command.Parameters.AddWithValue("@PayloadJson", insert.PayloadJson);
                command.Parameters.AddWithValue("@PayloadHash", insert.PayloadHash);
                command.Parameters.AddWithValue("@Subject", insert.Subject);
                command.Parameters.AddWithValue("@HtmlBody", (object?)insert.HtmlBody ?? DBNull.Value);
                command.Parameters.AddWithValue("@TextBody", (object?)insert.TextBody ?? DBNull.Value);
                command.Parameters.AddWithValue("@ReplyTo", (object?)insert.ReplyTo ?? DBNull.Value);
                command.Parameters.AddWithValue("@RecipientEmail", legacyRecipientEmail);
                command.Parameters.AddWithValue("@RecipientDisplayName", (object?)legacyRecipientDisplayName ?? DBNull.Value);
                command.Parameters.AddWithValue("@MetadataJson", (object?)insert.MetadataJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
                command.Parameters.AddWithValue("@MaxAttempts", insert.MaxAttempts);
                command.Parameters.AddWithValue(
                    "@ScheduledAt",
                    insert.ScheduledAt is null
                        ? DBNull.Value
                        : SqliteTime.ToStorageUtc(insert.ScheduledAt.Value));
                command.Parameters.AddWithValue("@AttachmentCount", attachments?.Count ?? 0);
                command.Parameters.AddWithValue(
                    "@AcceptedApiKeyId",
                    insert.AcceptedApiKeyId is null
                        ? DBNull.Value
                        : insert.AcceptedApiKeyId.Value.ToString("D"));
                command.Parameters.AddWithValue("@AcceptedAt", nowStorage);
                command.Parameters.AddWithValue("@CreatedAt", nowStorage);
                command.Parameters.AddWithValue("@UpdatedAt", nowStorage);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (attachments is { Count: > 0 })
            {
                foreach (var attachment in attachments)
                {
                    await using var attachmentCommand = connection.CreateCommand();
                    attachmentCommand.CommandText = insertAttachmentSql;
                    attachmentCommand.Parameters.AddWithValue("@Id", Guid.CreateVersion7(insert.AcceptedAt).ToString("D"));
                    attachmentCommand.Parameters.AddWithValue("@RequestId", insert.Id.ToString("D"));
                    attachmentCommand.Parameters.AddWithValue("@Order", attachment.Order);
                    attachmentCommand.Parameters.AddWithValue("@FileName", attachment.FileName);
                    attachmentCommand.Parameters.AddWithValue("@ContentType", attachment.ContentType);
                    attachmentCommand.Parameters.AddWithValue("@ByteLength", attachment.ByteLength);
                    attachmentCommand.Parameters.AddWithValue("@ContentSha256", attachment.Sha256Hex);
                    attachmentCommand.Parameters.AddWithValue("@SpoolKey", attachment.SpoolKey.ToString("D"));
                    attachmentCommand.Parameters.AddWithValue("@CreatedAt", nowStorage);
                    await attachmentCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            foreach (var recipient in recipients)
            {
                await using var recipientCommand = connection.CreateCommand();
                recipientCommand.CommandText = insertRecipientSql;
                recipientCommand.Parameters.AddWithValue("@RequestId", insert.Id.ToString("D"));
                recipientCommand.Parameters.AddWithValue("@RecipientRole", (int)recipient.Role);
                recipientCommand.Parameters.AddWithValue("@Ordinal", recipient.Ordinal);
                recipientCommand.Parameters.AddWithValue("@Address", recipient.Address);
                recipientCommand.Parameters.AddWithValue(
                    "@AddressKey",
                    RecipientEmailNormalizer.Normalize(recipient.Address));
                recipientCommand.Parameters.AddWithValue(
                    "@DisplayName",
                    (object?)recipient.DisplayName ?? DBNull.Value);
                recipientCommand.Parameters.AddWithValue(
                    "@DeliveryState",
                    (int)MailRecipientDeliveryState.NotSent);
                recipientCommand.Parameters.AddWithValue("@CreatedAt", nowStorage);
                recipientCommand.Parameters.AddWithValue("@UpdatedAt", nowStorage);
                await recipientCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _runtimeMetrics?.RecordRequestAccepted();
            if (attachments is { Count: > 0 })
            {
                _runtimeMetrics?.RecordAttachmentRequestAccepted();
            }
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);

            // The spool commit above ran before this transaction; on any failure here (lease
            // held, or any other exception) the committed directory is now an orphan with no DB
            // row, so remove it immediately rather than waiting for reconciliation.
            if (attachments is { Count: > 0 })
            {
                attachmentSpool?.TryDeleteCommitted(insert.Id);
            }

            throw;
        }
    }
}
