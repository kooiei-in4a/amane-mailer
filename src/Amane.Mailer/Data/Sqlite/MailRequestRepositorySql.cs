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

        where.Append("  AND tenant_id IN (");
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
