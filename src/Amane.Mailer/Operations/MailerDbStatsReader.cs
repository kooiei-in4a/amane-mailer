using System.Globalization;
using System.Text;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class MailerDbStatsReader(SqliteConnectionFactory connections)
{
    public async Task<bool> CanReadMigratedSchemaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connections.CanConnectToMigratedSchemaAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task<MailerDbStatsResult?> LoadStatsAsync(
        MailerDbStatsQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!await CanReadMigratedSchemaAsync(cancellationToken))
            return null;

        if (query.AllowedTenantIds is { Count: 0 })
        {
            return new MailerDbStatsResult(
                now.ToUniversalTime(),
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1);
        }

        const string sql = """
            WITH filtered AS (
                SELECT status, next_attempt_at, lock_expires_at, updated_at
                FROM mail_requests
                WHERE 1 = 1
            )
            SELECT
                COUNT(CASE WHEN status = @QueuedStatus THEN 1 END),
                COUNT(CASE WHEN status = @ProcessingStatus THEN 1 END),
                COUNT(CASE WHEN status = @DeliveredStatus THEN 1 END),
                COUNT(CASE WHEN status = @FailedStatus THEN 1 END),
                COUNT(CASE WHEN status = @DeadLetteredStatus THEN 1 END),
                COUNT(CASE
                    WHEN status = @QueuedStatus
                     AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
                    THEN 1 END),
                MIN(CASE
                    WHEN status = @QueuedStatus
                     AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
                    THEN updated_at END),
                COUNT(CASE
                    WHEN status = @QueuedStatus
                     AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
                     AND updated_at < @QueuedStaleBefore
                    THEN 1 END),
                COUNT(CASE
                    WHEN status = @ProcessingStatus
                     AND updated_at < @StaleProcessingBefore
                    THEN 1 END),
                COUNT(CASE
                    WHEN status = @ProcessingStatus
                     AND lock_expires_at IS NOT NULL
                     AND lock_expires_at <= @Now
                    THEN 1 END),
                COUNT(CASE
                    WHEN status = @FailedStatus
                     AND updated_at > @FailureWindowStart
                    THEN 1 END),
                COUNT(CASE
                    WHEN status = @DeadLetteredStatus
                     AND updated_at > @FailureWindowStart
                    THEN 1 END)
            FROM filtered;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ApplyTenantFilter(sql, query.AllowedTenantIds, command);
        command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue(
            "@QueuedStaleBefore",
            SqliteTime.ToStorageUtc(now.AddMinutes(-query.QueuedStaleMinutes)));
        command.Parameters.AddWithValue(
            "@StaleProcessingBefore",
            SqliteTime.ToStorageUtc(now.AddMinutes(-query.StaleProcessingMinutes)));
        command.Parameters.AddWithValue(
            "@FailureWindowStart",
            SqliteTime.ToStorageUtc(now.AddMinutes(-query.FailureWindowMinutes)));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Mailer stats query returned no rows.");

        DateTimeOffset? oldestQueuedAt = reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6));
        var oldestQueuedAgeSeconds = oldestQueuedAt is null
            ? 0
            : Math.Max(0, (long)Math.Floor((now - oldestQueuedAt.Value).TotalSeconds));

        var mailStats = new MailerDbStatsResult(
            now.ToUniversalTime(),
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            oldestQueuedAgeSeconds,
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            WorkerHeartbeatAgeSeconds: -1,
            SweepHeartbeatAgeSeconds: -1);

        const string heartbeatSql = "SELECT name, last_heartbeat_at FROM worker_heartbeats WHERE name IN ('worker', 'sweep');";

        await using var heartbeatCommand = connection.CreateCommand();
        heartbeatCommand.CommandText = heartbeatSql;

        await using var heartbeatReader = await heartbeatCommand.ExecuteReaderAsync(cancellationToken);
        var workerAge = -1L;
        var sweepAge = -1L;
        while (await heartbeatReader.ReadAsync(cancellationToken))
        {
            var name = heartbeatReader.GetString(0);
            var lastHeartbeatAt = SqliteTime.FromStorage(heartbeatReader.GetString(1));
            var age = Math.Max(0, (long)Math.Floor((now - lastHeartbeatAt).TotalSeconds));
            if (string.Equals(name, "worker", StringComparison.Ordinal))
                workerAge = age;
            else if (string.Equals(name, "sweep", StringComparison.Ordinal))
                sweepAge = age;
        }

        return mailStats with
        {
            WorkerHeartbeatAgeSeconds = workerAge,
            SweepHeartbeatAgeSeconds = sweepAge,
        };
    }

    public async Task<IReadOnlyList<MailerProviderAttemptStat>> LoadProviderAttemptStatsAsync(
        IReadOnlySet<Guid>? allowedTenantIds,
        CancellationToken cancellationToken = default)
    {
        if (!await CanReadMigratedSchemaAsync(cancellationToken))
            return [];

        if (allowedTenantIds is { Count: 0 })
            return [];

        const string sql = """
            SELECT ma.provider, ma.status, COUNT(*)
            FROM mail_attempts ma
            INNER JOIN mail_requests mr ON mr.id = ma.request_id
            WHERE 1 = 1
            GROUP BY ma.provider, ma.status
            ORDER BY ma.provider, ma.status;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ApplyTenantFilter(sql, allowedTenantIds, command, tenantColumn: "mr.tenant_id");

        var stats = new List<MailerProviderAttemptStat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stats.Add(new MailerProviderAttemptStat(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }

        return stats;
    }

    internal static string ApplyTenantFilter(
        string sql,
        IReadOnlySet<Guid>? allowedTenantIds,
        SqliteCommand command,
        string tenantColumn = "tenant_id")
    {
        if (allowedTenantIds is null)
            return sql;

        var filter = new StringBuilder();
        filter.Append(" AND ");
        filter.Append(tenantColumn);
        filter.Append(" IN (");

        var parameterNames = new List<string>(allowedTenantIds.Count);
        var index = 0;
        foreach (var tenantId in allowedTenantIds.OrderBy(id => id))
        {
            var parameterName = "@StatsTenantId" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, tenantId.ToString("D"));
            index++;
        }

        filter.Append(string.Join(", ", parameterNames));
        filter.Append(')');

        return sql.Replace("WHERE 1 = 1", "WHERE 1 = 1" + filter, StringComparison.Ordinal);
    }
}
