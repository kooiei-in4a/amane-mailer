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

    public static SetupGenerationLock Acquire(ISetupFileSystem fileSystem, string managedRootFullPath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRootFullPath);

        var lockPath = Path.GetFullPath(Path.Combine(managedRootFullPath, LockFileName));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                fileSystem,
                managedRootFullPath,
                lockPath,
                out var pathCode,
                out var pathMessage))
        {
            throw new SetupCoreException(pathCode, pathMessage);
        }

        var inspection = fileSystem.InspectSymlinkOrReparsePoint(lockPath);
        if (SetupPathGuard.IsUnsafeLink(inspection))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedPathUnsafe,
                "Generation lock path must not be a symlink or reparse point.");
        }

        try
        {
            var stream = fileSystem.OpenExclusiveGenerationLock(lockPath);
            return new SetupGenerationLock(stream);
        }
        catch (SetupCoreException)
        {
            throw;
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedConcurrentExecution,
                "Another Setup Core generation is already running against this managed root.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message.Contains("symlink", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedPathUnsafe,
                    "Generation lock path must not be a symlink or reparse point.");
            }

            throw new SetupCoreException(
                SetupResultCode.RejectedLockFailed,
                "Failed to acquire the Setup generation lock safely.");
        }
    }

    private static bool IsLockContention(IOException ex)
    {
        const int errorSharingViolation = unchecked((int)0x80070020);
        const int errorLockViolation = unchecked((int)0x80070021);
        const int errorFileExists = unchecked((int)0x80070050); // ERROR_FILE_EXISTS
        const int errorAlreadyExists = unchecked((int)0x800700B7); // ERROR_ALREADY_EXISTS
        if (ex.HResult is errorSharingViolation or errorLockViolation
            or errorFileExists or errorAlreadyExists)
        {
            return true;
        }

        // Exclusive open contention surfaces as sharing/in-use errors across platforms.
        return ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _lockStream.Dispose();
}
