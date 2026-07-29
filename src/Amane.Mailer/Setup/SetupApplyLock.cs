namespace Amane.Mailer.Setup;

/// <summary>
/// ADR 0021 <c>state/APPLY.lock</c> primitive. Session lifetime is owned by callers (#450).
/// Individual Docker operations must not re-acquire this lock.
/// </summary>
public sealed class SetupApplyLock : IDisposable
{
    public const string LockFileName = "APPLY.lock";

    private readonly FileStream _lockStream;

    private SetupApplyLock(FileStream lockStream) => _lockStream = lockStream;

    public static SetupApplyLock Acquire(ISetupFileSystem fileSystem, TrustedSetupHostLayout layout)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        if (!fileSystem.DirectoryExists(layout.StatePath))
        {
            fileSystem.CreateOwnerOnlyDirectory(layout.StatePath);
        }

        var lockPath = layout.ApplyLockPath;
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                fileSystem,
                layout.ManagedRoot,
                lockPath,
                out _,
                out _))
        {
            throw new SetupDockerException(
                SetupDockerResultCode.UnsafePath,
                "Apply lock path rejected.");
        }

        var inspection = fileSystem.InspectSymlinkOrReparsePoint(lockPath);
        if (SetupPathGuard.IsUnsafeLink(inspection))
        {
            throw new SetupDockerException(
                SetupDockerResultCode.UnsafePath,
                "Apply lock path must not be a symlink or reparse point.");
        }

        try
        {
            var stream = fileSystem.OpenExclusiveGenerationLock(lockPath);
            return new SetupApplyLock(stream);
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            throw new SetupDockerException(
                SetupDockerResultCode.ConcurrentSetupRejected,
                "Another setup apply session already holds APPLY.lock.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message.Contains("symlink", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupDockerException(
                    SetupDockerResultCode.UnsafePath,
                    "Apply lock path must not be a symlink or reparse point.");
            }

            throw new SetupDockerException(
                SetupDockerResultCode.FailedUnexpected,
                "Failed to acquire APPLY.lock safely.");
        }
    }

    private static bool IsLockContention(IOException ex)
    {
        const int errorSharingViolation = unchecked((int)0x80070020);
        const int errorLockViolation = unchecked((int)0x80070021);
        const int errorFileExists = unchecked((int)0x80070050);
        const int errorAlreadyExists = unchecked((int)0x800700B7);
        if (ex.HResult is errorSharingViolation or errorLockViolation
            or errorFileExists or errorAlreadyExists)
        {
            return true;
        }

        return ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _lockStream.Dispose();
}

public sealed class SetupDockerException : Exception
{
    public SetupDockerException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public SetupDockerResult ToResult() => SetupDockerResult.Fail(Code, Message);
}
