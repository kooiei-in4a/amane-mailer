namespace Amane.Mailer.Setup;

/// <summary>
/// Rejects symlinks, reparse points, and paths outside the managed root.
/// </summary>
public static class SetupPathGuard
{
    public static bool TryEnsureManagedRootSafe(
        ISetupFileSystem fileSystem,
        string managedRootFull,
        out string failureCode,
        out string message)
    {
        failureCode = SetupResultCode.RejectedPathUnsafe;
        message = "Managed root path rejected.";

        if (string.IsNullOrWhiteSpace(managedRootFull))
        {
            message = "Managed root path is required.";
            return false;
        }

        if (HasSymlinkOrReparseInAncestry(fileSystem, managedRootFull))
        {
            message = "Managed root must not be a symlink/reparse point or descend through one.";
            return false;
        }

        failureCode = string.Empty;
        message = string.Empty;
        return true;
    }

    public static bool TryEnsurePathSafeUnderRoot(
        ISetupFileSystem fileSystem,
        string managedRootFull,
        string candidateFull,
        out string failureCode,
        out string message)
    {
        failureCode = SetupResultCode.RejectedPathUnsafe;
        message = "Path rejected.";

        if (!IsUnderRoot(managedRootFull, candidateFull))
        {
            message = "Path is outside the managed root.";
            return false;
        }

        if (HasSymlinkOrReparseInAncestry(fileSystem, candidateFull))
        {
            message = "Symlink or reparse point paths are rejected.";
            return false;
        }

        failureCode = string.Empty;
        message = string.Empty;
        return true;
    }

    public static bool IsUnderRoot(string rootFullPath, string candidateFullPath)
    {
        var root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = candidateFullPath;
        if (candidate.Equals(
                rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        return candidate.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool HasSymlinkOrReparseInAncestry(ISetupFileSystem fileSystem, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (fileSystem.IsSymlinkOrReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, comparison))
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
