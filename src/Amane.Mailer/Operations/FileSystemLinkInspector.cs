using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amane.Mailer.Operations;

/// <summary>
/// Three-state symlink / reparse inspection and approved-root containment for shared secret writers.
/// </summary>
internal static class FileSystemLinkInspector
{
    public static FileSystemLinkInspectionResult Inspect(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    return FileSystemLinkInspectionResult.IsLinkOrReparse;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? FileSystemLinkInspectionResult.IsLinkOrReparse
                    : FileSystemLinkInspectionResult.NotALink;
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    return FileSystemLinkInspectionResult.IsLinkOrReparse;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? FileSystemLinkInspectionResult.IsLinkOrReparse
                    : FileSystemLinkInspectionResult.NotALink;
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsPathHasReparsePoint(path);
            }

            var dangling = new FileInfo(path);
            if (dangling.LinkTarget is not null)
            {
                return FileSystemLinkInspectionResult.IsLinkOrReparse;
            }

            try
            {
                if (dangling.Exists && dangling.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return FileSystemLinkInspectionResult.IsLinkOrReparse;
                }
            }
            catch
            {
                return FileSystemLinkInspectionResult.InspectionFailed;
            }

            return FileSystemLinkInspectionResult.NotALink;
        }
        catch
        {
            return FileSystemLinkInspectionResult.InspectionFailed;
        }
    }

    public static bool IsUnsafe(FileSystemLinkInspectionResult inspection) =>
        inspection is FileSystemLinkInspectionResult.IsLinkOrReparse
            or FileSystemLinkInspectionResult.InspectionFailed;

    public static bool IsUnderApprovedRoot(string approvedRootFullPath, string candidateFullPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootTrimmed = approvedRootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidateFullPath.Equals(rootTrimmed, comparison))
        {
            return true;
        }

        var rootPrefix = rootTrimmed + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(rootPrefix, comparison);
    }

    public static bool HasUnsafeLinkInAncestry(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (IsUnsafe(Inspect(current)))
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

    [SupportedOSPlatform("windows")]
    private static FileSystemLinkInspectionResult WindowsPathHasReparsePoint(string path)
    {
        var attrs = GetFileAttributesW(path);
        if (attrs == unchecked((uint)(-1)))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND → not a link.
            if (error is 2 or 3)
            {
                return FileSystemLinkInspectionResult.NotALink;
            }

            return FileSystemLinkInspectionResult.InspectionFailed;
        }

        const uint fileAttributeReparsePoint = 0x400;
        return (attrs & fileAttributeReparsePoint) != 0
            ? FileSystemLinkInspectionResult.IsLinkOrReparse
            : FileSystemLinkInspectionResult.NotALink;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}

internal enum FileSystemLinkInspectionResult
{
    NotALink,
    IsLinkOrReparse,
    InspectionFailed,
}
