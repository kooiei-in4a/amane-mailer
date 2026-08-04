using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Worker;

/// <summary>
/// Startup and periodic spool/SQLite reconciliation (ADR 0022 D-08 spool lifecycle):
/// staging left over from a crashed accept is deleted, and committed spool for a request that
/// has durably reached a terminal state is deleted. A committed directory with no owning DB
/// row at all is deleted only after a grace period, so a request whose SQLite commit is still
/// in flight is never touched. Failures never roll back or revisit an already-terminal request;
/// they just retry on the next pass.
/// </summary>
public sealed class AttachmentSpoolReconciliationService(
    AttachmentSpool spool,
    SqliteConnectionFactory connections,
    TimeProvider timeProvider,
    MailerRuntimeMetrics runtimeMetrics,
    ILogger<AttachmentSpoolReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StagingGracePeriod = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // At startup, every staging directory is provably orphaned: no HTTP request that could
        // still be writing to one has had a chance to run yet.
        CleanupAllStaging();

        using var timer = new PeriodicTimer(ReconciliationInterval);
        try
        {
            do
            {
                try
                {
                    await ReconcileCommittedSpoolAsync(stoppingToken);
                    CleanupStaleStaging();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Attachment spool reconciliation pass failed; retrying next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void CleanupAllStaging()
    {
        // EnumerateStagingDirectories only yields directories that currently exist, so each
        // iteration here is a genuine cleanup, not a no-op probe.
        foreach (var directory in spool.EnumerateStagingDirectories())
        {
            TryDeleteDirectory(directory);
            runtimeMetrics.RecordAttachmentSpoolCleanup("staging_startup");
        }
    }

    private void CleanupStaleStaging()
    {
        var cutoffUtc = timeProvider.GetUtcNow().UtcDateTime - StagingGracePeriod;
        foreach (var directory in spool.EnumerateStagingDirectories())
        {
            if (SafeLastWriteTimeUtc(directory) < cutoffUtc)
            {
                TryDeleteDirectory(directory);
                runtimeMetrics.RecordAttachmentSpoolCleanup("staging_stale");
            }
        }
    }

    private async Task ReconcileCommittedSpoolAsync(CancellationToken cancellationToken)
    {
        var requestIds = spool.EnumerateCommittedRequestIds().ToList();
        if (requestIds.Count == 0)
        {
            return;
        }

        var statuses = await LoadStatusesAsync(requestIds, cancellationToken);
        var orphanCutoffUtc = timeProvider.GetUtcNow().UtcDateTime - OrphanGracePeriod;

        foreach (var requestId in requestIds)
        {
            if (statuses.TryGetValue(requestId, out var status))
            {
                if (IsTerminal(status))
                {
                    spool.TryDeleteCommitted(requestId);
                    runtimeMetrics.RecordAttachmentSpoolCleanup("committed_terminal");
                }

                continue;
            }

            // No DB row at all for this committed directory (accept crashed between the spool
            // rename and the SQLite commit). Only remove once clearly stale.
            var directory = spool.GetCommittedDirectory(requestId);
            if (SafeLastWriteTimeUtc(directory) < orphanCutoffUtc)
            {
                spool.TryDeleteCommitted(requestId);
                runtimeMetrics.RecordAttachmentSpoolCleanup("committed_orphan");
            }
        }
    }

    private async Task<Dictionary<Guid, MailRequestState>> LoadStatusesAsync(
        IReadOnlyList<Guid> requestIds,
        CancellationToken cancellationToken)
    {
        var statuses = new Dictionary<Guid, MailRequestState>(requestIds.Count);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var parameterNames = new List<string>(requestIds.Count);
        for (var i = 0; i < requestIds.Count; i++)
        {
            var parameterName = $"@Id{i}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, requestIds[i].ToString("D"));
        }

        command.CommandText = $"SELECT id, status FROM mail_requests WHERE id IN ({string.Join(",", parameterNames)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses[Guid.Parse(reader.GetString(0))] = (MailRequestState)reader.GetInt32(1);
        }

        return statuses;
    }

    private static bool IsTerminal(MailRequestState status) =>
        status is MailRequestState.Delivered
            or MailRequestState.Failed
            or MailRequestState.DeadLettered
            or MailRequestState.Cancelled
            or MailRequestState.DeliveryUnknown;

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MaxValue; // Treat as "not yet stale" if we can't stat it right now.
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MaxValue;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
