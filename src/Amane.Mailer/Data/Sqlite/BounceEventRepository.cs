using System.Text;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Existing Admin-compatible query surface over recipient-aware delivery-event history.
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
            INSERT INTO recipient_delivery_events (
                id, tenant_id, source_service, mail_request_id,
                recipient_role, recipient_ordinal,
                provider, provider_event_id, provider_message_id,
                provider_status, applied_delivery_state, status_message, occurred_at, created_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId,
                @RecipientRole, @RecipientOrdinal,
                @Provider, @ProviderEventId, @ProviderMessageId,
                @DeliveryStatus, @AppliedDeliveryState, @StatusMessage, @OccurredAt, @CreatedAt)
            ON CONFLICT (provider, provider_event_id) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", row.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", row.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", row.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientRole", row.RecipientRole);
        command.Parameters.AddWithValue("@RecipientOrdinal", row.RecipientOrdinal);
        command.Parameters.AddWithValue("@Provider", row.Provider);
        command.Parameters.AddWithValue("@ProviderEventId", row.ProviderEventId);
        command.Parameters.AddWithValue("@ProviderMessageId", row.ProviderMessageId);
        command.Parameters.AddWithValue("@DeliveryStatus", row.DeliveryStatus);
        command.Parameters.AddWithValue(
            "@AppliedDeliveryState",
            row.AppliedDeliveryState is null ? DBNull.Value : (int)row.AppliedDeliveryState.Value);
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
                FROM recipient_delivery_events
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

    /// <summary>
    /// Lists bounce facts for a mail request (consumer mail_request_id).
    /// No FK to mail_requests — empty result is valid when rows were purged or never correlated.
    /// </summary>
    public async Task<IReadOnlyList<AdminBounceEventRow>> ListForMailRequestAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        IReadOnlySet<Guid>? allowedTenantIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceService);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("""
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
              AND provider_status <> 'Delivered'
            """);
        MailRequestRepositorySql.AppendTenantScopeFilter(where, command, allowedTenantIds);

        command.CommandText = $"""
            SELECT
                id, provider, provider_event_id, provider_message_id,
                provider_status, status_message, occurred_at, created_at
            FROM recipient_delivery_events
            {where}
            ORDER BY occurred_at ASC, id ASC;
            """;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));

        var rows = new List<AdminBounceEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminBounceEventRow(
                Id: Guid.Parse(reader.GetString(0)),
                Provider: reader.GetString(1),
                ProviderEventId: reader.GetString(2),
                ProviderMessageId: reader.GetString(3),
                DeliveryStatus: reader.GetString(4),
                StatusMessage: reader.IsDBNull(5) ? null : reader.GetString(5),
                OccurredAt: SqliteTime.FromStorage(reader.GetString(6)),
                CreatedAt: SqliteTime.FromStorage(reader.GetString(7))));
        }

        return rows;
    }
}
