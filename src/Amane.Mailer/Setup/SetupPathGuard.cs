namespace Amane.Mailer.Setup;

/// <summary>
/// Rejects symlinks, reparse points, and paths outside the managed root.
/// </summary>
public static class SetupPathGuard
{
    public static bool TryResolveUnderRoot(
        ISetupFileSystem fileSystem,
        string managedRoot,
        string candidatePath,
        out string fullPath,
        out string failureCode,
        out string message)
    {
        fullPath = string.Empty;
        failureCode = SetupResultCode.RejectedPathUnsafe;
        message = "Path rejected.";

        if (string.IsNullOrWhiteSpace(managedRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            message = "Managed root and candidate path are required.";
            return false;
        }

        string rootFull;
        string candidateFull;
        try
        {
            rootFull = Path.GetFullPath(managedRoot);
            candidateFull = Path.GetFullPath(candidatePath);
        }
        catch
        {
            message = "Path could not be resolved.";
            return false;
        }

        if (!IsUnderRoot(rootFull, candidateFull))
        {
            message = "Path is outside the managed root.";
            return false;
        }

        if (fileSystem.IsSymlinkOrReparsePoint(candidateFull)
            || HasSymlinkAncestor(fileSystem, rootFull, candidateFull))
        {
            message = "Symlink or reparse point paths are rejected.";
            return false;
        }

        fullPath = candidateFull;
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

    private static bool HasSymlinkAncestor(ISetupFileSystem fileSystem, string rootFull, string candidateFull)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = candidateFull;
        while (!string.IsNullOrEmpty(current)
               && !current.Equals(
                   rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   comparison))
        {
            if (fileSystem.IsSymlinkOrReparsePoint(current))
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return fileSystem.IsSymlinkOrReparsePoint(rootFull);
    }
}
