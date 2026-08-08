using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

public sealed class AdminDbOpsService(
    SqliteConnectionFactory connections,
    MailerAdminDbOpsOptions options,
    MailerMaintenanceLeaseStore maintenanceLeaseStore,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan BackupLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackupLeaseRenewInterval = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _disposed;

    public bool IsEnabled => options.Enabled;

    public string BackupDirectory => options.BackupDirectory;

    public async Task<AdminDbOpsResult> RunCheckpointAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return AdminDbOpsResult.Disabled();

        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return AdminDbOpsResult.LockHeld();

        try
        {
            await connections.RunWalCheckpointTruncateAsync(cancellationToken);
            return AdminDbOpsResult.Succeeded();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AdminDbOpsResult.Failed(ex.GetType().Name);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<AdminDbOpsBackupResult> RunBackupAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
            return AdminDbOpsBackupResult.Disabled();

        if (!await _operationLock.WaitAsync(0, cancellationToken))
            return AdminDbOpsBackupResult.LockHeld();

        // ADR 0022 D-09 backup sequence: acquire the durable cross-process lease first (this
        // also blocks new attachment acceptance for its duration), then verify no non-terminal
        // attachment row exists before snapshotting. A successful routine backup must never
        // capture a non-terminal attachment row without its spool.
        var ownerToken = Guid.NewGuid();
        var acquired = await maintenanceLeaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerToken, BackupLeaseDuration, cancellationToken);
        if (!acquired.Acquired)
        {
            _operationLock.Release();
            return AdminDbOpsBackupResult.LockHeld();
        }

        var fencingToken = acquired.FencingToken;
        try
        {
            if (await maintenanceLeaseStore.HasActiveAttachmentRequestsAsync(cancellationToken))
            {
                return AdminDbOpsBackupResult.Failed("ActiveAttachmentRequests");
            }

            var fileName = BuildBackupFileName(timeProvider.GetUtcNow());
            var destinationPath = ResolveBackupDestinationPath(fileName);

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
                // ADR 0022 D-09 publish gate: the heartbeat only proves the last renewal it
                // attempted succeeded -- re-check DB-side ownership/fencing/expiry immediately
                // before the artifact is treated as a successful backup (post-merge review of
                // #533/PR #537).
                verifyBeforePublish: ct => IsLeaseStillValidForPublishAsync(ownerToken, fencingToken, heartbeat, ct));
            return AdminDbOpsBackupResult.Succeeded(fileName);
        }
        catch (BackupMaintenanceLeaseLostException)
        {
            return AdminDbOpsBackupResult.Failed("BackupMaintenanceLeaseLost");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AdminDbOpsBackupResult.Failed(ex.GetType().Name);
        }
        finally
        {
            await maintenanceLeaseStore.ReleaseAsync(
                MailerMaintenanceLeaseStore.BackupLeaseName,
                ownerToken,
                fencingToken,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            _operationLock.Release();
        }
    }

    private async Task<bool> IsLeaseStillValidForPublishAsync(
        Guid ownerToken, long fencingToken, MaintenanceLeaseHeartbeat heartbeat, CancellationToken cancellationToken) =>
        heartbeat.IsHealthy
        && await maintenanceLeaseStore.IsLeaseCurrentlyValidAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            ownerToken,
            fencingToken,
            cancellationToken);

    internal static string BuildBackupFileName(DateTimeOffset utcNow) =>
        "mailer-" + utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".db";

    internal string ResolveBackupDestinationPath(string fileName)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Backup file name is invalid.");
        }

        var destinationPath = Path.GetFullPath(Path.Combine(options.BackupDirectory, fileName));
        var normalizedBackupDirectory = Path.GetFullPath(options.BackupDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var directoryPrefix = normalizedBackupDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(directoryPrefix, comparison)
            && !PathsEqual(destinationPath, normalizedBackupDirectory))
        {
            throw new InvalidOperationException("Backup destination is outside the configured backup directory.");
        }

        return destinationPath;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _operationLock.Dispose();
    }
}

public enum AdminDbOpsStatus
{
    Succeeded,
    Failed,
    Disabled,
    LockHeld,
}

public sealed record AdminDbOpsResult(AdminDbOpsStatus Status, string? ErrorDetail = null)
{
    public static AdminDbOpsResult Succeeded() => new(AdminDbOpsStatus.Succeeded);

    public static AdminDbOpsResult Failed(string errorDetail) => new(AdminDbOpsStatus.Failed, errorDetail);

    public static AdminDbOpsResult Disabled() => new(AdminDbOpsStatus.Disabled);

    public static AdminDbOpsResult LockHeld() => new(AdminDbOpsStatus.LockHeld);
}

public sealed record AdminDbOpsBackupResult(
    AdminDbOpsStatus Status,
    string? BackupFileName = null,
    string? ErrorDetail = null)
{
    public static AdminDbOpsBackupResult Succeeded(string backupFileName) =>
        new(AdminDbOpsStatus.Succeeded, backupFileName);

    public static AdminDbOpsBackupResult Failed(string errorDetail) =>
        new(AdminDbOpsStatus.Failed, ErrorDetail: errorDetail);

    public static AdminDbOpsBackupResult Disabled() => new(AdminDbOpsStatus.Disabled);

    public static AdminDbOpsBackupResult LockHeld() => new(AdminDbOpsStatus.LockHeld);
}
