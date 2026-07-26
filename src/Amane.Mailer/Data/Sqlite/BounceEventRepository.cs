using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists correlated bounce domain facts (ADR 0020 D-05). No FK to mail_requests.
/// </summary>
public sealed class BounceEventRepository(SqliteConnectionFactory connections)
{
    public async Task<bool> TryInsertAsync(
        BounceEventInsert row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // status_message must already be sanitized by the caller (#26 / ADR 0020 D-08).
        const string sql = """
            INSERT INTO bounce_events (
                id, tenant_id, source_service, mail_request_id,
                provider, provider_event_id, provider_message_id,
                delivery_status, status_message, occurred_at, created_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId,
                @Provider, @ProviderEventId, @ProviderMessageId,
                @DeliveryStatus, @StatusMessage, @OccurredAt, @CreatedAt)
            ON CONFLICT (provider, provider_event_id) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", row.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", row.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", row.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@Provider", row.Provider);
        command.Parameters.AddWithValue("@ProviderEventId", row.ProviderEventId);
        command.Parameters.AddWithValue("@ProviderMessageId", row.ProviderMessageId);
        command.Parameters.AddWithValue("@DeliveryStatus", row.DeliveryStatus);
        command.Parameters.AddWithValue("@StatusMessage", (object?)row.StatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(row.OccurredAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(row.CreatedAt));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ExistsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM bounce_events
                WHERE id = @Id
            );
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }
}
