using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists and reads admin-operation audit events (ADR 0013 D-08).
/// The SQLite table is the source of truth so the trail survives restart and
/// deployment; the structured stdout log is a secondary channel.
/// </summary>
public sealed class AdminAuditRepository(SqliteConnectionFactory connections)
{
    public async Task WriteAsync(AdminAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO admin_audit_events (
                event_type, actor, occurred_at,
                source_ip, user_agent_summary,
                target_type, target_id, field_name,
                result, error_code)
            VALUES (
                @EventType, @Actor, @OccurredAt,
                @SourceIp, @UserAgentSummary,
                @TargetType, @TargetId, @FieldName,
                @Result, @ErrorCode);
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@EventType", auditEvent.EventType);
        command.Parameters.AddWithValue("@Actor", auditEvent.Actor);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(auditEvent.OccurredAt));
        command.Parameters.AddWithValue("@SourceIp", (object?)auditEvent.SourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserAgentSummary", (object?)auditEvent.UserAgentSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@TargetType", (object?)auditEvent.TargetType ?? DBNull.Value);
        command.Parameters.AddWithValue("@TargetId", (object?)auditEvent.TargetId ?? DBNull.Value);
        command.Parameters.AddWithValue("@FieldName", (object?)auditEvent.FieldName ?? DBNull.Value);
        command.Parameters.AddWithValue("@Result", auditEvent.Result);
        command.Parameters.AddWithValue("@ErrorCode", (object?)auditEvent.ErrorCode ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdminAuditEventRow>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);

        const string sql = """
            SELECT
                id, event_type, actor, occurred_at,
                source_ip, user_agent_summary,
                target_type, target_id, field_name,
                result, error_code
            FROM admin_audit_events
            ORDER BY occurred_at DESC, id DESC
            LIMIT @Limit;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Limit", boundedLimit);

        var rows = new List<AdminAuditEventRow>(boundedLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminAuditEventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                SqliteTime.FromStorage(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }
}
