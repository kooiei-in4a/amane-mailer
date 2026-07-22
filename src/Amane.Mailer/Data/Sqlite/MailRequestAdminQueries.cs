using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestAdminQueries(SqliteConnectionFactory connections)
{
    private static readonly int[] KnownAdminListStatuses =
    [
        (int)MailRequestState.Queued,
        (int)MailRequestState.Processing,
        (int)MailRequestState.Delivered,
        (int)MailRequestState.Failed,
        (int)MailRequestState.DeadLettered,
        (int)MailRequestState.Cancelled,
    ];

    public async Task<AdminMailRequestListPage> ListForAdminAsync(
        AdminMailRequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);

        List<AdminMailRequestListRow> rows;
        if (query.Status is not null)
        {
            rows = await ListForAdminStatusAsync(connection, query, query.Status.Value, limit, cancellationToken);
        }
        else
        {
            rows = new List<AdminMailRequestListRow>(limit * 5);
            foreach (var status in KnownAdminListStatuses)
            {
                rows.AddRange(await ListForAdminStatusAsync(connection, query, status, limit, cancellationToken));
            }

            rows = rows
                .OrderByDescending(row => row.UpdatedAt)
                .ThenByDescending(row => row.Id)
                .Take(limit)
                .ToList();
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminMailRequestCursor.Encode(last.UpdatedAt, last.Id);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminMailRequestListPage(rows, nextCursor);
    }

    public async Task<AdminDeadLetterListPage> ListDeadLettersForAdminAsync(
        AdminDeadLetterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Limit", limit);

        var where = new StringBuilder("""
            WHERE status = @Status
              AND completed_at IS NOT NULL
            """);

        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (query.CursorCompletedAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (completed_at < @CursorCompletedAt OR (completed_at = @CursorCompletedAt AND id < @CursorId))");
            command.Parameters.AddWithValue("@CursorCompletedAt", query.CursorCompletedAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id,
                recipient_email, subject, last_error_message,
                attempt_count, max_attempts, completed_at
            FROM mail_requests
            {{where}}
            ORDER BY completed_at DESC, id DESC
            LIMIT @Limit;
            """;

        var rows = new List<AdminDeadLetterListRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminDeadLetterListRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                SqliteTime.FromStorage(reader.GetString(9))));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminDeadLetterCursor.Encode(last.CompletedAt, last.Id);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminDeadLetterListPage(rows, nextCursor);
    }

    public async Task<int> CountDeadLettersForAdminAsync(
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE status = @Status");
        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, allowedTenantIds);
        command.CommandText = $$"""
            SELECT COUNT(*)
            FROM mail_requests
            {{where}};
            """;
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<AdminMailRequestDetail?> GetDetailForAdminAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE id = @Id");
        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, allowedTenantIds);

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts,
                next_attempt_at, lock_token, lock_expires_at,
                delivered_at, failed_at, last_error_message,
                accepted_at, created_at, updated_at, completed_at
            FROM mail_requests
            {{where}}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AdminMailRequestDetail(
            Id: Guid.Parse(reader.GetString(0)),
            TenantId: Guid.Parse(reader.GetString(1)),
            SourceService: reader.GetString(2),
            MailRequestId: Guid.Parse(reader.GetString(3)),
            Purpose: reader.GetString(4),
            PayloadJson: reader.GetString(5),
            PayloadHash: reader.GetString(6),
            Subject: reader.GetString(7),
            HtmlBody: reader.IsDBNull(8) ? null : reader.GetString(8),
            TextBody: reader.IsDBNull(9) ? null : reader.GetString(9),
            ReplyTo: reader.IsDBNull(10) ? null : reader.GetString(10),
            RecipientEmail: reader.GetString(11),
            RecipientDisplayName: reader.IsDBNull(12) ? null : reader.GetString(12),
            MetadataJson: reader.IsDBNull(13) ? null : reader.GetString(13),
            Status: (MailRequestState)reader.GetInt32(14),
            AttemptCount: reader.GetInt32(15),
            MaxAttempts: reader.GetInt32(16),
            NextAttemptAt: reader.IsDBNull(17) ? null : SqliteTime.FromStorage(reader.GetString(17)),
            LockToken: reader.IsDBNull(18) ? null : reader.GetString(18),
            LockExpiresAt: reader.IsDBNull(19) ? null : SqliteTime.FromStorage(reader.GetString(19)),
            DeliveredAt: reader.IsDBNull(20) ? null : SqliteTime.FromStorage(reader.GetString(20)),
            FailedAt: reader.IsDBNull(21) ? null : SqliteTime.FromStorage(reader.GetString(21)),
            LastErrorMessage: reader.IsDBNull(22) ? null : reader.GetString(22),
            AcceptedAt: SqliteTime.FromStorage(reader.GetString(23)),
            CreatedAt: SqliteTime.FromStorage(reader.GetString(24)),
            UpdatedAt: SqliteTime.FromStorage(reader.GetString(25)),
            CompletedAt: reader.IsDBNull(26) ? null : SqliteTime.FromStorage(reader.GetString(26)));
    }

    public async Task<IReadOnlyList<AdminMailAttemptRow>> ListAttemptsForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                attempt_number, provider, status,
                provider_message_id, error_code, error_message,
                started_at, completed_at
            FROM mail_attempts
            WHERE request_id = @RequestId
            ORDER BY started_at ASC, id ASC;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var rows = new List<AdminMailAttemptRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminMailAttemptRow(
                AttemptNumber: reader.GetInt32(0),
                Provider: reader.GetString(1),
                Status: reader.GetInt32(2),
                ProviderMessageId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ErrorCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                ErrorMessage: reader.IsDBNull(5) ? null : reader.GetString(5),
                StartedAt: SqliteTime.FromStorage(reader.GetString(6)),
                CompletedAt: SqliteTime.FromStorage(reader.GetString(7))));
        }

        return rows;
    }

    private static async Task<List<AdminMailRequestListRow>> ListForAdminStatusAsync(
        SqliteConnection connection,
        AdminMailRequestListQuery query,
        int status,
        int limit,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder("WHERE status = @Status");
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Status", status);

        if (query.TenantId is not null)
        {
            where.AppendLine();
            where.Append("  AND tenant_id = @TenantId");
            command.Parameters.AddWithValue("@TenantId", query.TenantId.Value.ToString("D"));
        }

        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (!string.IsNullOrWhiteSpace(query.SourceService))
        {
            where.AppendLine();
            where.Append("  AND source_service = @SourceService");
            command.Parameters.AddWithValue("@SourceService", query.SourceService);
        }

        if (query.CursorUpdatedAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (updated_at < @CursorUpdatedAt OR (updated_at = @CursorUpdatedAt AND id < @CursorId))");
            command.Parameters.AddWithValue("@CursorUpdatedAt", query.CursorUpdatedAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id,
                recipient_email, subject, status, attempt_count, max_attempts,
                updated_at
            FROM mail_requests
            {{where}}
            ORDER BY updated_at DESC, id DESC
            LIMIT @Limit;
            """;
        command.Parameters.AddWithValue("@Limit", limit);

        var rows = new List<AdminMailRequestListRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminMailRequestListRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                (MailRequestState)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                SqliteTime.FromStorage(reader.GetString(9))));
        }

        return rows;
    }
}
