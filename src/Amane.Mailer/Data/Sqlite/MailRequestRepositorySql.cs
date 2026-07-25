using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

internal static class MailRequestRepositorySql
{
    internal static void AppendTenantScopeFilter(
        StringBuilder where,
        SqliteCommand command,
        IReadOnlySet<Guid>? allowedTenantIds,
        string tenantColumn = "tenant_id")
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

        where.Append("  AND ");
        where.Append(tenantColumn);
        where.Append(" IN (");
        where.Append(string.Join(", ", parameterNames));
        where.Append(')');
    }

    internal static async Task<bool> ExistsByIdempotencyKeyAsync(
        SqliteConnection connection,
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM mail_requests
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    internal static async Task<(Guid InternalRequestId, MailRequestStatusRow Status)?>
        ReadStatusWithInternalIdByIdempotencyKeyAsync(
            SqliteConnection connection,
            Guid tenantId,
            string sourceService,
            Guid mailRequestId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                mr.id,
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

        var internalId = Guid.Parse(reader.GetString(0));
        var lastErrorCode = reader.GetString(9);
        var status = new MailRequestStatusRow(
            MailRequestId: Guid.Parse(reader.GetString(1)),
            Status: (MailRequestState)reader.GetInt32(2),
            AttemptCount: reader.GetInt32(3),
            MaxAttempts: reader.GetInt32(4),
            NextAttemptAt: reader.IsDBNull(5) ? null : SqliteTime.FromStorage(reader.GetString(5)),
            ScheduledAt: reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6)),
            AcceptedAt: SqliteTime.FromStorage(reader.GetString(7)),
            DeliveredAt: reader.IsDBNull(8) ? null : SqliteTime.FromStorage(reader.GetString(8)),
            LastErrorCode: string.IsNullOrEmpty(lastErrorCode) ? null : lastErrorCode);
        return (internalId, status);
    }

    internal static async Task<MailRequestStatusRow?> ReadStatusByIdempotencyKeyAsync(
        SqliteConnection connection,
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        var row = await ReadStatusWithInternalIdByIdempotencyKeyAsync(
            connection,
            tenantId,
            sourceService,
            mailRequestId,
            cancellationToken);
        return row?.Status;
    }

    internal static async Task WriteFailureAuditAsync(
        AdminAuditRepository auditRepository,
        SqliteConnection connection,
        AdminAuditEvent auditTemplate,
        string errorCode,
        CancellationToken cancellationToken) =>
        await auditRepository.WriteAsync(
            auditTemplate with
            {
                Result = AdminAuditLog.Results.Failure,
                ErrorCode = errorCode,
            },
            connection,
            cancellationToken);

    internal static async Task<(MailRequestState Status, DateTimeOffset? LockExpiresAt)?> ReadScopedStatusAsync(
        SqliteConnection connection,
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE id = @Id");
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        AppendTenantScopeFilter(where, command, allowedTenantIds);
        command.CommandText = $"""
            SELECT status, lock_expires_at
            FROM mail_requests
            {where}
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var status = (MailRequestState)reader.GetInt32(0);
        DateTimeOffset? lockExpiresAt = reader.IsDBNull(1)
            ? null
            : SqliteTime.FromStorage(reader.GetString(1));
        return (status, lockExpiresAt);
    }
}
