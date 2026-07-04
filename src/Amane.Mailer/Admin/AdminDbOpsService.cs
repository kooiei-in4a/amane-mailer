using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

public sealed class AdminDbOpsService(
    SqliteConnectionFactory connections,
    MailerAdminDbOpsOptions options,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

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

        try
        {
            var fileName = BuildBackupFileName(timeProvider.GetUtcNow());
            var destinationPath = ResolveBackupDestinationPath(fileName);
            await connections.BackupToAsync(destinationPath, cancellationToken);
            return AdminDbOpsBackupResult.Succeeded(fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AdminDbOpsBackupResult.Failed(ex.GetType().Name);
        }
        finally
        {
            _operationLock.Release();
        }
    }

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
