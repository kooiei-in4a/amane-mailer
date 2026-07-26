using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestAcceptStore(
    SqliteConnectionFactory connections,
    MailerRuntimeMetrics? runtimeMetrics = null)
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
                status, attempt_count, max_attempts, scheduled_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                @PayloadJson, @PayloadHash, @Subject, @HtmlBody, @TextBody, @ReplyTo,
                @RecipientEmail, @RecipientDisplayName, @MetadataJson,
                @Status, 0, @MaxAttempts, @ScheduledAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;

        var nowStorage = SqliteTime.ToStorageUtc(insert.AcceptedAt);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
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
            command.Parameters.AddWithValue("@RecipientEmail", insert.RecipientEmail);
            command.Parameters.AddWithValue("@RecipientDisplayName", (object?)insert.RecipientDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@MetadataJson", (object?)insert.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
            command.Parameters.AddWithValue("@MaxAttempts", insert.MaxAttempts);
            command.Parameters.AddWithValue(
                "@ScheduledAt",
                insert.ScheduledAt is null
                    ? DBNull.Value
                    : SqliteTime.ToStorageUtc(insert.ScheduledAt.Value));
            command.Parameters.AddWithValue("@AcceptedAt", nowStorage);
            command.Parameters.AddWithValue("@CreatedAt", nowStorage);
            command.Parameters.AddWithValue("@UpdatedAt", nowStorage);

            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _runtimeMetrics?.RecordRequestAccepted();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
