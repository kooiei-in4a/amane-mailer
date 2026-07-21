namespace Amane.Mailer.Operations;

/// <summary>
/// Shared filesystem safety checks for <see cref="SecretFileWriter"/> and
/// <see cref="ExclusiveOperationLock"/>. Rejects symlinks/reparse points and overly permissive
/// directory modes before any secret touches disk. Directory mode enforcement only runs on
/// Linux (the real deploy target); Windows dev/test exercises the calling logic only, matching
/// this repository's existing stance that Windows cannot substitute for Linux owner/mode
/// verification.
/// </summary>
internal static class FileSystemSafetyGuard
{
    public static void EnsureDirectoryIsSafe(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe,
                "Target directory does not exist.");
        }

        var info = new DirectoryInfo(directoryPath);
        if (info.LinkTarget is not null)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe,
                "Target directory must not be a symlink or reparse point.");
        }

        EnsureNotGroupOrOtherAccessible(directoryPath);
    }

    public static void EnsureTargetFileIsSafeIfExists(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var info = new FileInfo(filePath);
        if (info.LinkTarget is not null)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe,
                "Target file must not be a symlink or reparse point.");
        }
    }

    /// <summary>
    /// Proves the running (non-root) container user can actually create and remove a file in the
    /// directory, rather than inferring it from ownership metadata .NET cannot read portably.
    /// </summary>
    public static void EnsureDirectoryIsWritable(string directoryPath)
    {
        var probePath = Path.Combine(directoryPath, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryNotWritable,
                "Target directory is not writable by the running container user.",
                ex);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch
            {
                // Best effort; a stray probe file is not a secret and not a correctness issue.
            }
        }
    }

    private static void EnsureNotGroupOrOtherAccessible(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(directoryPath);
        const UnixFileMode groupOrOtherBits =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        if ((mode & groupOrOtherBits) != 0)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe,
                "Target directory must not grant group or other permissions (expected owner-only, e.g. 0700).");
        }
    }
}
