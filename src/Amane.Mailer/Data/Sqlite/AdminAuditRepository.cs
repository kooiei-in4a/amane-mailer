using System.Text;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

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
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await WriteAsync(auditEvent, connection, cancellationToken);
    }

    public async Task WriteAsync(
        AdminAuditEvent auditEvent,
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        var tenantId = await ResolveTenantIdAsync(auditEvent, connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_audit_events (
                event_type, actor, occurred_at,
                source_ip, user_agent_summary,
                target_type, target_id, tenant_id, field_name,
                result, error_code)
            VALUES (
                @EventType, @Actor, @OccurredAt,
                @SourceIp, @UserAgentSummary,
                @TargetType, @TargetId, @TenantId, @FieldName,
                @Result, @ErrorCode);
            """;
        command.Parameters.AddWithValue("@EventType", auditEvent.EventType);
        command.Parameters.AddWithValue("@Actor", auditEvent.Actor);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(auditEvent.OccurredAt));
        command.Parameters.AddWithValue("@SourceIp", (object?)auditEvent.SourceIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserAgentSummary", (object?)auditEvent.UserAgentSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@TargetType", (object?)auditEvent.TargetType ?? DBNull.Value);
        command.Parameters.AddWithValue("@TargetId", (object?)auditEvent.TargetId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@TenantId",
            tenantId is null ? DBNull.Value : tenantId.Value.ToString("D"));
        command.Parameters.AddWithValue("@FieldName", (object?)auditEvent.FieldName ?? DBNull.Value);
        command.Parameters.AddWithValue("@Result", auditEvent.Result);
        command.Parameters.AddWithValue("@ErrorCode", (object?)auditEvent.ErrorCode ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdminAuditEventRow>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var page = await ListForAdminAsync(
            new AdminAuditListQuery { PageSize = Math.Clamp(limit, 1, 500) },
            cancellationToken);
        return page.Items;
    }

    public async Task<AdminAuditListPage> ListForAdminAsync(
        AdminAuditListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new StringBuilder("WHERE 1 = 1");
        AppendListFilters(where, command, query);
        AppendAuditTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (query.CursorOccurredAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (ae.occurred_at < @CursorOccurredAt OR (ae.occurred_at = @CursorOccurredAt AND ae.id < @CursorId))");
            command.Parameters.AddWithValue("@CursorOccurredAt", query.CursorOccurredAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value);
        }

        command.CommandText = $"""
            SELECT
                ae.id, ae.event_type, ae.actor, ae.occurred_at,
                ae.source_ip, ae.user_agent_summary,
                ae.target_type, ae.target_id, ae.field_name,
                ae.result, ae.error_code
            FROM admin_audit_events ae
            {where}
            ORDER BY ae.occurred_at DESC, ae.id DESC
            LIMIT @Limit;
            """;
        command.Parameters.AddWithValue("@Limit", limit);

        var rows = new List<AdminAuditEventRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadAuditEventRow(reader));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminAuditEventCursor.Encode(last.OccurredAt, last.Id);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminAuditListPage(rows, nextCursor);
    }

    public async Task<AdminAuditEventRow?> GetForAdminAsync(
        long id,
        IReadOnlySet<Guid>? allowedTenantIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new StringBuilder("WHERE ae.id = @Id");
        command.Parameters.AddWithValue("@Id", id);
        AppendAuditTenantScopeFilter(where, command, allowedTenantIds);

        command.CommandText = $"""
            SELECT
                ae.id, ae.event_type, ae.actor, ae.occurred_at,
                ae.source_ip, ae.user_agent_summary,
                ae.target_type, ae.target_id, ae.field_name,
                ae.result, ae.error_code
            FROM admin_audit_events ae
            {where}
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadAuditEventRow(reader);
    }

    private static void AppendListFilters(
        StringBuilder where,
        SqliteCommand command,
        AdminAuditListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            where.AppendLine();
            where.Append("  AND ae.event_type = @EventType");
            command.Parameters.AddWithValue("@EventType", query.EventType);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            where.AppendLine();
            where.Append("  AND ae.actor = @Actor");
            command.Parameters.AddWithValue("@Actor", query.Actor);
        }

        if (query.OccurredFrom is not null)
        {
            where.AppendLine();
            where.Append("  AND ae.occurred_at >= @OccurredFrom");
            command.Parameters.AddWithValue("@OccurredFrom", SqliteTime.ToStorageUtc(query.OccurredFrom.Value));
        }

        if (query.OccurredToExclusive is not null)
        {
            where.AppendLine();
            where.Append("  AND ae.occurred_at < @OccurredToExclusive");
            command.Parameters.AddWithValue(
                "@OccurredToExclusive",
                SqliteTime.ToStorageUtc(query.OccurredToExclusive.Value));
        }
    }

    /// <summary>
    /// Scoped admins see auth/session/db_ops events service-wide (no mail tenant PII) and
    /// tenant-scoped targets (<see cref="AdminAuditLog.TargetTypes.TenantScoped"/>) only when
    /// the persisted tenant_id is in an allowed tenant. Null tenant_id on those targets is
    /// hidden from scoped admins (break-glass only). Break-glass passes
    /// <paramref name="allowedTenantIds"/> as null (no filter).
    /// </summary>
    private static void AppendAuditTenantScopeFilter(
        StringBuilder where,
        SqliteCommand command,
        IReadOnlySet<Guid>? allowedTenantIds)
    {
        if (allowedTenantIds is null)
            return;

        where.AppendLine();
        if (allowedTenantIds.Count == 0)
        {
            where.Append("  AND 1 = 0");
            return;
        }

        var parameterNames = new List<string>(allowedTenantIds.Count);
        var index = 0;
        foreach (var tenantId in allowedTenantIds.OrderBy(id => id))
        {
            var parameterName = "@AllowedTenantId" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, tenantId.ToString("D"));
            index++;
        }

        var scopedTargetNames = new List<string>(AdminAuditLog.TargetTypes.TenantScoped.Count);
        for (var i = 0; i < AdminAuditLog.TargetTypes.TenantScoped.Count; i++)
        {
            var parameterName = "@TenantScopedTarget" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            scopedTargetNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, AdminAuditLog.TargetTypes.TenantScoped[i]);
        }

        where.Append("  AND (ae.target_type IS NULL OR ae.target_type NOT IN (");
        where.Append(string.Join(", ", scopedTargetNames));
        where.Append(") OR ae.tenant_id IN (");
        where.Append(string.Join(", ", parameterNames));
        where.Append("))");
    }

    /// <summary>
    /// Prefer the caller-supplied tenant id. When missing for a mail_request target,
    /// resolve from the live mail_requests row so writes during Admin operations stay
    /// scoped after later request retention deletes the join target.
    /// </summary>
    private static async Task<Guid?> ResolveTenantIdAsync(
        AdminAuditEvent auditEvent,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (auditEvent.TenantId is not null)
            return auditEvent.TenantId;

        if (!string.Equals(auditEvent.TargetType, AdminAuditLog.TargetTypes.MailRequest, StringComparison.Ordinal))
            return null;

        if (!Guid.TryParse(auditEvent.TargetId, out var mailRequestId))
            return null;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tenant_id
            FROM mail_requests
            WHERE id = @Id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", mailRequestId.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is string tenantIdText && Guid.TryParse(tenantIdText, out var tenantId))
            return tenantId;

        return null;
    }

    private static AdminAuditEventRow ReadAuditEventRow(SqliteDataReader reader) =>
        new(
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
            reader.IsDBNull(10) ? null : reader.GetString(10));

    public async Task<int> DeleteOlderThanAsync(
        DateTimeOffset occurredBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM admin_audit_events
            WHERE id IN (
                SELECT id
                FROM admin_audit_events
                WHERE occurred_at < @OccurredBefore
                ORDER BY occurred_at ASC
                LIMIT @BatchSize
            );
            """;

        var occurredBeforeStorage = SqliteTime.ToStorageUtc(occurredBefore);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@OccurredBefore", occurredBeforeStorage);
            command.Parameters.AddWithValue("@BatchSize", Math.Max(1, batchSize));

            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admin_audit_events;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }
}
