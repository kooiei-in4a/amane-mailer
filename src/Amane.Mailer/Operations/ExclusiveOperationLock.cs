namespace Amane.Mailer.Operations;

/// <summary>
/// Cross-process exclusive lock for the window between preflight passing and commit completing
/// in <see cref="AdminProviderRegisterAcsCommand"/>. Backed by an OS-level advisory lock
/// (<see cref="FileShare.None"/>, emulated via <c>flock</c> on Linux) tied to the open file
/// description, not to the lock file's mere existence on disk.
/// <para>
/// This matters for crash recovery: if the process is killed (container OOM-killed, SIGKILL,
/// host crash) between acquiring the lock and releasing it, the OS releases the advisory lock
/// automatically when the process's file descriptors close. A subsequent invocation can acquire
/// the lock again immediately — no stale-lock timeout or manual cleanup is needed. The lock file
/// left behind on disk is inert; only a live open handle blocks acquisition.
/// </para>
/// </summary>
public sealed class ExclusiveOperationLock : IDisposable
{
    public const string LockFileName = ".register-acs.lock";

    private readonly FileStream _lockStream;

    private ExclusiveOperationLock(FileStream lockStream) => _lockStream = lockStream;

    public static ExclusiveOperationLock Acquire(string directoryPath)
    {
        FileSystemSafetyGuard.EnsureDirectoryIsSafe(directoryPath);
        var lockPath = Path.Combine(directoryPath, LockFileName);
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(lockPath);

        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedConcurrentExecution,
                "Another register-acs operation is already running against this directory.",
                ex);
        }

        return new ExclusiveOperationLock(stream);
    }

    public void Dispose()
    {
        // Intentionally does not delete the lock file: FileShare.None acquisition, not file
        // existence, is what grants exclusivity. Deleting it here would race a concurrently
        // waiting Acquire() that has already recreated/opened it after this Dispose releases the
        // OS-level lock but before a delete would land. Leaving an unlocked, empty file behind is
        // inert and reused (not recreated) by the next Acquire().
        _lockStream.Dispose();
    }
}
