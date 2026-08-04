using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbBackupCommand(
    SqliteConnectionFactory connections,
    MailerMaintenanceLeaseStore maintenanceLeaseStore,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan BackupLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackupLeaseRenewInterval = TimeSpan.FromMinutes(3);

    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;
    public const int LeaseHeldExitCode = 3;
    public const int ActiveAttachmentRequestsExitCode = 4;
    public const int BackupMaintenanceLeaseLostExitCode = 5;

    public static bool IsDbBackupCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "db", StringComparison.Ordinal)
        && string.Equals(args[1], "backup", StringComparison.Ordinal);

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Count != 3 || !IsDbBackupCommand(args))
        {
            await error.WriteLineAsync("Usage: dotnet Amane.Mailer.dll db backup <absolute-path>");
            return UsageErrorExitCode;
        }

        var destinationPath = args[2];
        if (!Path.IsPathRooted(destinationPath))
        {
            await error.WriteLineAsync("Backup destination must be an absolute path.");
            return UsageErrorExitCode;
        }

        if (connections.IsConfiguredDatabasePath(destinationPath))
        {
            await error.WriteLineAsync("Backup destination must not be the active mailer database.");
            return UsageErrorExitCode;
        }

        // Same durable maintenance lease as the Admin backup endpoint (ADR 0022 D-09): Admin and
        // CLI backup share one gate against concurrent backups and against new attachment
        // acceptance, and both verify no non-terminal attachment row exists before snapshotting.
        var ownerToken = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var acquired = await maintenanceLeaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerToken, BackupLeaseDuration, now, cancellationToken);
        if (!acquired.Acquired)
        {
            await error.WriteLineAsync("Backup maintenance lease is held by another backup in progress.");
            return LeaseHeldExitCode;
        }

        var fencingToken = acquired.FencingToken;
        try
        {
            if (await maintenanceLeaseStore.HasActiveAttachmentRequestsAsync(cancellationToken))
            {
                await error.WriteLineAsync(
                    "Backup aborted: one or more attachment requests are not yet in a terminal state.");
                return ActiveAttachmentRequestsExitCode;
            }

            // ADR 0022 D-09: renew the lease periodically for the snapshot's full duration so a
            // backup slower than BackupLeaseDuration can never let expires_at lapse mid-flight
            // and reopen the acceptance race the lease exists to close.
            await using var heartbeat = new MaintenanceLeaseHeartbeat(
                maintenanceLeaseStore,
                MailerMaintenanceLeaseStore.BackupLeaseName,
                ownerToken,
                fencingToken,
                BackupLeaseDuration,
                BackupLeaseRenewInterval,
                timeProvider);

            await connections.BackupToAsync(
                destinationPath,
                cancellationToken,
                // ADR 0022 D-09 publish gate: re-check DB-side ownership/fencing/expiry
                // immediately before the artifact is treated as a successful backup, not just
                // the heartbeat's last-known renewal outcome (post-merge review of #533/PR #537).
                verifyBeforePublish: async ct =>
                    heartbeat.IsHealthy
                    && await maintenanceLeaseStore.IsLeaseCurrentlyValidAsync(
                        MailerMaintenanceLeaseStore.BackupLeaseName,
                        ownerToken,
                        fencingToken,
                        timeProvider.GetUtcNow(),
                        ct));
            await output.WriteLineAsync($"Database backup written to {destinationPath}");
            return SuccessExitCode;
        }
        catch (BackupMaintenanceLeaseLostException)
        {
            await error.WriteLineAsync(
                "Backup aborted: the maintenance lease was lost before the snapshot could be published.");
            return BackupMaintenanceLeaseLostExitCode;
        }
        finally
        {
            await maintenanceLeaseStore.ReleaseAsync(
                MailerMaintenanceLeaseStore.BackupLeaseName,
                ownerToken,
                fencingToken,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
    }
}
