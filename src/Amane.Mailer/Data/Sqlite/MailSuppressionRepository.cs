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
        var normalized = RecipientEmailNormalizer.Normalize(recipientEmail);

        const string sql = """
            DELETE FROM mail_suppressions
            WHERE tenant_id = @TenantId
              AND recipient_email = @RecipientEmail;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", normalized);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
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
}
