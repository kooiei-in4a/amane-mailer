using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed class DbBackupCommand(
    SqliteConnectionFactory connections,
    MailerMaintenanceLeaseStore maintenanceLeaseStore,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan BackupLeaseDuration = TimeSpan.FromMinutes(10);

    public const int SuccessExitCode = 0;
    public const int UsageErrorExitCode = 2;
    public const int LeaseHeldExitCode = 3;
    public const int ActiveAttachmentRequestsExitCode = 4;

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
        if (!await maintenanceLeaseStore.TryAcquireAsync(
                MailerMaintenanceLeaseStore.BackupLeaseName, ownerToken, BackupLeaseDuration, now, cancellationToken))
        {
            await error.WriteLineAsync("Backup maintenance lease is held by another backup in progress.");
            return LeaseHeldExitCode;
        }

        try
        {
            if (await maintenanceLeaseStore.HasActiveAttachmentRequestsAsync(cancellationToken))
            {
                await error.WriteLineAsync(
                    "Backup aborted: one or more attachment requests are not yet in a terminal state.");
                return ActiveAttachmentRequestsExitCode;
            }

            await connections.BackupToAsync(destinationPath, cancellationToken);
            await output.WriteLineAsync($"Database backup written to {destinationPath}");
            return SuccessExitCode;
        }
        finally
        {
            await maintenanceLeaseStore.ReleaseAsync(
                MailerMaintenanceLeaseStore.BackupLeaseName, ownerToken, timeProvider.GetUtcNow(), CancellationToken.None);
        }
    }
}
