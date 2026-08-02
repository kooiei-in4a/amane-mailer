using System.Globalization;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Operations;

public sealed class DbStatsCommand(
    SqliteConnectionFactory connections,
    Func<DateTimeOffset>? nowProvider = null,
    DeliveryEventRepository? deliveryEventRepository = null,
    ProviderEventInboxRepository? providerEventInboxRepository = null,
    ProviderQueueDeadLetterRepository? providerQueueDeadLetterRepository = null)
{
    public const int SuccessExitCode = 0;
    public const int UnavailableExitCode = 1;
    public const int UsageErrorExitCode = 2;

    private readonly MailerDbStatsReader _statsReader = new(connections);
    private readonly DeliveryEventRepository _deliveryEventRepository =
        deliveryEventRepository ?? new DeliveryEventRepository(connections);
    private readonly ProviderEventInboxRepository _providerEventInboxRepository =
        providerEventInboxRepository ?? new ProviderEventInboxRepository(connections);
    private readonly ProviderQueueDeadLetterRepository _providerQueueDeadLetterRepository =
        providerQueueDeadLetterRepository ?? new ProviderQueueDeadLetterRepository(connections);
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

        if (!await _statsReader.CanReadMigratedSchemaAsync(cancellationToken))
        {
            await error.WriteLineAsync("Mailer database schema is not migrated.");
            return UnavailableExitCode;
        }

        var now = _nowProvider().ToUniversalTime();
        var query = options.TenantId is null
            ? MailerDbStatsQuery.ForAllTenants(
                options.QueuedStaleMinutes,
                options.FailureWindowMinutes,
                options.StaleProcessingMinutes)
            : MailerDbStatsQuery.ForSingleTenant(
                options.TenantId.Value,
                options.QueuedStaleMinutes,
                options.FailureWindowMinutes,
                options.StaleProcessingMinutes);

        var stats = await _statsReader.LoadStatsAsync(query, now, cancellationToken)
            ?? throw new InvalidOperationException("Mailer stats query returned no result.");

        var webhookCounts = await _deliveryEventRepository.CountOperationalAsync(cancellationToken);
        var providerEventCounts = await _providerEventInboxRepository.CountOperationalAsync(cancellationToken);
        var providerQueueDeadLettersCount = await _providerQueueDeadLetterRepository.CountAsync(cancellationToken);
        var suppressions = new MailSuppressionRepository(connections);
        var suppressionsCount = await suppressions.CountAsync(options.TenantId, cancellationToken);

        await WriteStatsAsync(
            stats,
            webhookCounts,
            providerEventCounts,
            providerQueueDeadLettersCount,
            suppressionsCount,
            options.TenantId,
            output);
        return SuccessExitCode;
    }

    private static async Task WriteStatsAsync(
        MailerDbStatsResult stats,
        (long PendingCount, long DeadLetteredCount) webhookCounts,
        (long PendingCount, long DeadLetteredCount) providerEventCounts,
        long providerQueueDeadLettersCount,
        long suppressionsCount,
        Guid? tenantId,
        TextWriter output)
    {
        await output.WriteLineAsync($"as_of_utc={SqliteTime.ToStorageUtc(stats.AsOfUtc)}");
        await output.WriteLineAsync($"tenant_id={tenantId?.ToString("D") ?? "all"}");
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
        await output.WriteLineAsync($"webhook_events_pending={webhookCounts.PendingCount}");
        await output.WriteLineAsync($"webhook_events_dead_lettered={webhookCounts.DeadLetteredCount}");
        await output.WriteLineAsync($"provider_events_pending={providerEventCounts.PendingCount}");
        await output.WriteLineAsync($"provider_events_dead_lettered={providerEventCounts.DeadLetteredCount}");
        await output.WriteLineAsync($"provider_queue_dead_letters_count={providerQueueDeadLettersCount}");
        await output.WriteLineAsync($"mail_suppressions_count={suppressionsCount}");
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
}
