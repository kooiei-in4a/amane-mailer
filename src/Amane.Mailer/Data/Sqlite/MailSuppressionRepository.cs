using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Tenant-scoped suppression list (ADR 0020 D-06 / D-07).
/// Rows are physically deleted on removal; audit belongs in admin_audit_events (#400).
/// </summary>
public sealed class MailSuppressionRepository(SqliteConnectionFactory connections)
{
    public async Task<bool> TryInsertAsync(
        MailSuppressionInsert row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Reason is not (MailSuppressionReasons.HardBounce or MailSuppressionReasons.Manual))
        {
            throw new ArgumentOutOfRangeException(nameof(row), row.Reason, "Unsupported suppression reason.");
        }

        var normalized = RecipientEmailNormalizer.Normalize(row.RecipientEmail);

        const string sql = """
            INSERT INTO mail_suppressions (
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
            VALUES (
                @Id, @TenantId, @RecipientEmail, @Reason, @SourceBounceEventId, @CreatedAt)
            ON CONFLICT (tenant_id, recipient_email) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", row.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", normalized);
        command.Parameters.AddWithValue("@Reason", row.Reason);
        command.Parameters.AddWithValue(
            "@SourceBounceEventId",
            row.SourceBounceEventId is null ? DBNull.Value : row.SourceBounceEventId.Value.ToString("D"));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(row.CreatedAt));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ExistsAsync(
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        var normalized = RecipientEmailNormalizer.Normalize(recipientEmail);

        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM mail_suppressions
                WHERE tenant_id = @TenantId
                  AND recipient_email = @RecipientEmail
            );
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", normalized);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<bool> TryDeleteAsync(
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await TryDeleteReturningIdAsync(tenantId, recipientEmail, connection, cancellationToken) is not null;
    }

    /// <summary>
    /// Deletes a matching row on an open connection and returns its id, or null when absent.
    /// Callers that need an audit trail should wrap this with the audit insert in one transaction.
    /// </summary>
    public async Task<Guid?> TryDeleteReturningIdAsync(
        Guid tenantId,
        string recipientEmail,
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var normalized = RecipientEmailNormalizer.Normalize(recipientEmail);

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id
                FROM mail_suppressions
                WHERE tenant_id = @TenantId
                  AND recipient_email = @RecipientEmail
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            select.Parameters.AddWithValue("@RecipientEmail", normalized);

            var idValue = await select.ExecuteScalarAsync(cancellationToken);
            if (idValue is not string idText || !Guid.TryParse(idText, out var suppressionId))
            {
                return null;
            }

            await using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM mail_suppressions
                WHERE id = @Id
                  AND tenant_id = @TenantId;
                """;
            delete.Parameters.AddWithValue("@Id", suppressionId.ToString("D"));
            delete.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));

            var affected = await delete.ExecuteNonQueryAsync(cancellationToken);
            return affected > 0 ? suppressionId : null;
        }
    }

    public async Task<long> CountAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (tenantId is null)
        {
            command.CommandText = "SELECT COUNT(*) FROM mail_suppressions;";
        }
        else
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM mail_suppressions
                WHERE tenant_id = @TenantId;
                """;
            command.Parameters.AddWithValue("@TenantId", tenantId.Value.ToString("D"));
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : Convert.ToInt64(result);
    }

    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset createdBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM mail_suppressions
            WHERE id IN (
                SELECT id
                FROM mail_suppressions
                WHERE created_at < @CreatedBefore
                ORDER BY created_at ASC, id ASC
                LIMIT @BatchSize
            );
            """;

        var effectiveBatchSize = Math.Clamp(batchSize, 1, MailerRetentionOptions.MaxBatchSize);
        var createdBeforeStorage = SqliteTime.ToStorageUtc(createdBefore);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@CreatedBefore", createdBeforeStorage);
        command.Parameters.AddWithValue("@BatchSize", effectiveBatchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AdminSuppressionListPage> ListForAdminAsync(
        AdminSuppressionListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE 1 = 1");

        if (query.TenantId is not null)
        {
            where.AppendLine();
            where.Append("  AND tenant_id = @TenantId");
            command.Parameters.AddWithValue("@TenantId", query.TenantId.Value.ToString("D"));
        }

        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (!string.IsNullOrWhiteSpace(query.CursorCreatedAt) && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("""
                  AND (
                    created_at < @CursorCreatedAt
                    OR (created_at = @CursorCreatedAt AND id < @CursorId)
                  )
                """);
            command.Parameters.AddWithValue("@CursorCreatedAt", query.CursorCreatedAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $"""
            SELECT
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at
            FROM mail_suppressions
            {where}
            ORDER BY created_at DESC, id DESC
            LIMIT @Limit;
            """;
        command.Parameters.AddWithValue("@Limit", limit);

        var rows = new List<AdminSuppressionListRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminSuppressionListRow(
                Id: Guid.Parse(reader.GetString(0)),
                TenantId: Guid.Parse(reader.GetString(1)),
                RecipientEmail: reader.GetString(2),
                Reason: reader.GetString(3),
                SourceBounceEventId: reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                CreatedAt: SqliteTime.FromStorage(reader.GetString(5))));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = AdminSuppressionCursor.Encode(last.CreatedAt, last.Id);
        }

        return new AdminSuppressionListPage(rows, nextCursor);
    }
}
