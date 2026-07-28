namespace Amane.Mailer.Setup;

/// <summary>
/// Host-unit exclusive lock for Setup Core bundle generation (sealing key preflight through
/// FINALIZED / cleanup). Uses an OS advisory lock so a crash releases it automatically.
/// </summary>
public sealed class SetupGenerationLock : IDisposable
{
    public const string LockFileName = ".setup-generation.lock";

    private readonly FileStream _lockStream;

    private SetupGenerationLock(FileStream lockStream) => _lockStream = lockStream;

    public static SetupGenerationLock Acquire(string managedRootFullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRootFullPath);
        var lockPath = Path.Combine(managedRootFullPath, LockFileName);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SetupGenerationLock(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedConcurrentExecution,
                "Another Setup Core generation is already running against this managed root.");
        }
    }

    public void Dispose() => _lockStream.Dispose();
}
