using System.Globalization;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbStatsCommand(
    SqliteConnectionFactory connections,
    Func<DateTimeOffset>? nowProvider = null)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    private readonly Func<DateTimeOffset> _nowProvider = nowProvider ?? (() => SqliteTime.UtcNow);

    public static bool IsDbStatsCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "stats", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string? parseError = null;
        if (!IsDbStatsCommand(args) || !TryParseOptions(args, out var options, out parseError))
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                await error.WriteLineAsync(parseError);
            }

            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db stats [--tenant-id <uuid>] [--queued-stale-minutes <minutes>] [--failure-window-minutes <minutes>] [--stale-processing-minutes <minutes>]");
            return UsageErrorExitCode;
        }

        if (!await CanReadMigratedSchemaAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated.");
            return UnavailableExitCode;
        }

        var now = _nowProvider().ToUniversalTime();
        var stats = await LoadStatsAsync(options, now, cancellationToken);
        await WriteStatsAsync(stats, output);
        return SuccessExitCode;
    }

    private async Task<MailRequestStats> LoadStatsAsync(
        DbStatsOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH filtered AS (
                SELECT status, next_attempt_at, lock_expires_at, updated_at
                FROM mail_requests
                WHERE @TenantId IS NULL OR tenant_id = @TenantId
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
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", options.TenantId is null ? DBNull.Value : options.TenantId.Value.ToString("D"));
        command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue(
            "@QueuedStaleBefore",
            SqliteTime.ToStorageUtc(now.AddMinutes(-options.QueuedStaleMinutes)));
        command.Parameters.AddWithValue(
            "@StaleProcessingBefore",
            SqliteTime.ToStorageUtc(now.AddMinutes(-options.StaleProcessingMinutes)));
        command.Parameters.AddWithValue(
            "@FailureWindowStart",
            SqliteTime.ToStorageUtc(now.AddMinutes(-options.FailureWindowMinutes)));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Mailer stats query returned no rows.");
        }

        DateTimeOffset? oldestQueuedAt = reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6));
        var oldestQueuedAgeSeconds = oldestQueuedAt is null
            ? 0
            : Math.Max(0, (long)Math.Floor((now - oldestQueuedAt.Value).TotalSeconds));

        var mailStats = new MailRequestStats(
            now,
            options.TenantId,
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

    private async Task<bool> CanReadMigratedSchemaAsync(CancellationToken cancellationToken)
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

    private static async Task WriteStatsAsync(MailRequestStats stats, TextWriter output)
    {
        await output.WriteLineAsync($"as_of_utc={SqliteTime.ToStorageUtc(stats.AsOfUtc)}");
        await output.WriteLineAsync($"tenant_id={stats.TenantId?.ToString("D") ?? "all"}");
        await output.WriteLineAsync($"status_queued={stats.QueuedCount}");
        await output.WriteLineAsync($"status_processing={stats.ProcessingCount}");
        await output.WriteLineAsync($"status_delivered={stats.DeliveredCount}");
        await output.WriteLineAsync($"status_failed={stats.FailedCount}");
        await output.WriteLineAsync($"status_dead_lettered={stats.DeadLetteredCount}");
        await output.WriteLineAsync($"ready_backlog_count={stats.ReadyBacklogCount}");
        await output.WriteLineAsync($"oldest_queued_age_seconds={stats.OldestQueuedAgeSeconds}");
        await output.WriteLineAsync($"queued_stale_count={stats.QueuedStaleCount}");
        await output.WriteLineAsync($"stale_processing_count={stats.StaleProcessingCount}");
        await output.WriteLineAsync($"expired_processing_count={stats.ExpiredProcessingCount}");
        await output.WriteLineAsync($"recent_failed_count={stats.RecentFailedCount}");
        await output.WriteLineAsync($"recent_dead_lettered_count={stats.RecentDeadLetteredCount}");
        await output.WriteLineAsync($"failed_total={stats.FailedCount}");
        await output.WriteLineAsync($"dead_lettered_total={stats.DeadLetteredCount}");
        await output.WriteLineAsync($"terminal_total={stats.FailedCount + stats.DeadLetteredCount}");
        await output.WriteLineAsync($"worker_heartbeat_age_seconds={stats.WorkerHeartbeatAgeSeconds}");
        await output.WriteLineAsync($"sweep_heartbeat_age_seconds={stats.SweepHeartbeatAgeSeconds}");
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out DbStatsOptions options,
        out string? error)
    {
        Guid? tenantId = null;
        var queuedStaleMinutes = 30;
        var failureWindowMinutes = 60;
        var staleProcessingMinutes = 30;

        for (var index = 2; index < args.Count; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Count)
            {
                options = default;
                error = $"Missing value for {option}.";
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--tenant-id":
                    if (!Guid.TryParse(value, out var parsedTenantId))
                    {
                        options = default;
                        error = "--tenant-id must be a UUID.";
                        return false;
                    }

                    tenantId = parsedTenantId;
                    break;

                case "--queued-stale-minutes":
                    if (!TryParseNonNegativeMinutes(value, out queuedStaleMinutes))
                    {
                        options = default;
                        error = "--queued-stale-minutes must be a non-negative integer.";
                        return false;
                    }

                    break;

                case "--failure-window-minutes":
                    if (!TryParseNonNegativeMinutes(value, out failureWindowMinutes))
                    {
                        options = default;
                        error = "--failure-window-minutes must be a non-negative integer.";
                        return false;
                    }

                    break;

                case "--stale-processing-minutes":
                    if (!TryParseNonNegativeMinutes(value, out staleProcessingMinutes))
                    {
                        options = default;
                        error = "--stale-processing-minutes must be a non-negative integer.";
                        return false;
                    }

                    break;

                default:
                    options = default;
                    error = $"Unknown option: {option}.";
                    return false;
            }
        }

        options = new DbStatsOptions(
            tenantId,
            queuedStaleMinutes,
            failureWindowMinutes,
            staleProcessingMinutes);
        error = null;
        return true;
    }

    private static bool TryParseNonNegativeMinutes(string value, out int minutes) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
        && minutes >= 0;

    private readonly record struct DbStatsOptions(
        Guid? TenantId,
        int QueuedStaleMinutes,
        int FailureWindowMinutes,
        int StaleProcessingMinutes);

    private sealed record MailRequestStats(
        DateTimeOffset AsOfUtc,
        Guid? TenantId,
        long QueuedCount,
        long ProcessingCount,
        long DeliveredCount,
        long FailedCount,
        long DeadLetteredCount,
        long ReadyBacklogCount,
        long OldestQueuedAgeSeconds,
        long QueuedStaleCount,
        long StaleProcessingCount,
        long ExpiredProcessingCount,
        long RecentFailedCount,
        long RecentDeadLetteredCount,
        long WorkerHeartbeatAgeSeconds,
        long SweepHeartbeatAgeSeconds);
}
