using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amane.Mailer.Operations;
using Microsoft.Win32.SafeHandles;

namespace Amane.Mailer.Setup;

public sealed class HostSetupFileSystem : ISetupFileSystem
{
    private const int ORdOnly = 0;
    private const int ODirectory = 65536; // 0x10000 on Linux
    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;
    private const uint SddlRevision1 = 1;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    return SetupLinkInspectionResult.IsLinkOrReparse;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? SetupLinkInspectionResult.IsLinkOrReparse
                    : SetupLinkInspectionResult.NotALink;
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    return SetupLinkInspectionResult.IsLinkOrReparse;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? SetupLinkInspectionResult.IsLinkOrReparse
                    : SetupLinkInspectionResult.NotALink;
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsPathHasReparsePoint(path);
            }

            var dangling = new FileInfo(path);
            if (dangling.LinkTarget is not null)
            {
                return SetupLinkInspectionResult.IsLinkOrReparse;
            }

            try
            {
                if (dangling.Exists && dangling.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return SetupLinkInspectionResult.IsLinkOrReparse;
                }
            }
            catch
            {
                return SetupLinkInspectionResult.InspectionFailed;
            }

            return SetupLinkInspectionResult.NotALink;
        }
        catch
        {
            return SetupLinkInspectionResult.InspectionFailed;
        }
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public void CreateOwnerOnlyDirectory(string path) => SecureFileCreate.EnsureOwnerOnlyDirectory(path);

    public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
        SecureFileCreate.WriteAllBytesCreateNew(path, content);

    public void WriteProtectedFileCreateNew(string path, string content) =>
        SecureFileCreate.WriteAllTextCreateNew(path, content);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectoryRecursive(string path) => Directory.Delete(path, recursive: true);

    public void MoveReplace(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: true);

    public void FlushDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            FlushDirectoryUnix(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            FlushDirectoryWindows(path);
            return;
        }

        throw new IOException("Directory durability flush is not supported on this platform.");
    }

    public void SetUnixOwnership(string path, uint userId, uint groupId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        if (Chown(path, userId, groupId) != 0)
        {
            throw new IOException("Failed to set Unix ownership on a generated path.");
        }
    }

    public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var mode = executableDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
    }

    public bool TryGetUnixFileMode(string path, out UnixFileMode mode)
    {
        mode = default;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            mode = File.GetUnixFileMode(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsOwnerOnlyFile(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (!TryGetUnixFileMode(path, out var mode))
                {
                    return false;
                }

                const UnixFileMode groupOrOther =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                return (mode & groupOrOther) == 0
                    && (mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                        == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (OperatingSystem.IsWindows())
            {
                return IsOwnerOnlyFileWindows(path);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public uint? GetEffectiveUnixUserId()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        return Geteuid();
    }

    [SupportedOSPlatform("windows")]
    private static SetupLinkInspectionResult WindowsPathHasReparsePoint(string path)
    {
        var attrs = GetFileAttributesW(path);
        if (attrs == unchecked((uint)(-1)))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND → not a link.
            if (error is 2 or 3)
            {
                return SetupLinkInspectionResult.NotALink;
            }

            return SetupLinkInspectionResult.InspectionFailed;
        }

        const uint fileAttributeReparsePoint = 0x400;
        return (attrs & fileAttributeReparsePoint) != 0
            ? SetupLinkInspectionResult.IsLinkOrReparse
            : SetupLinkInspectionResult.NotALink;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void FlushDirectoryUnix(string path)
    {
        var fd = Open(path, ORdOnly | ODirectory);
        if (fd < 0)
        {
            // Fallback without O_DIRECTORY for platforms that reject the flag.
            fd = Open(path, ORdOnly);
        }

        if (fd < 0)
        {
            throw new IOException("Failed to open directory for durability flush.");
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                throw new IOException("Failed to fsync directory.");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void FlushDirectoryWindows(string path)
    {
        // ADR D-03: Windows volume/directory flush is part of the fixed durability order.
        // FlushFileBuffers on a directory requires GENERIC_WRITE (ACCESS_DENIED with read-only).
        using var handle = CreateFileW(
            path,
            dwDesiredAccess: 0x40000000, // GENERIC_WRITE
            dwShareMode: 0x7,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: 3, // OPEN_EXISTING
            dwFlagsAndAttributes: 0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
            hTemplateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Failed to open directory for durability flush.");
        }

        if (!FlushFileBuffers(handle))
        {
            throw new IOException("Failed to flush directory buffers.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyFileWindows(string path)
    {
        if (!TryGetFileSddl(path, out var sddl, out var ownerSid))
        {
            return false;
        }

        var daclIndex = sddl.IndexOf("D:", StringComparison.Ordinal);
        if (daclIndex < 0)
        {
            return false;
        }

        var afterD = daclIndex + 2;
        var firstAce = sddl.IndexOf('(', afterD);
        if (firstAce < 0)
        {
            return false;
        }

        var flags = sddl[afterD..firstAce];
        if (!flags.Contains('P'))
        {
            return false;
        }

        var saclStart = sddl.IndexOf("S:", daclIndex, StringComparison.Ordinal);
        var daclEnd = saclStart >= 0 ? saclStart : sddl.Length;
        var cursor = firstAce;
        while (cursor < daclEnd)
        {
            if (sddl[cursor] != '(')
            {
                cursor++;
                continue;
            }

            var close = sddl.IndexOf(')', cursor + 1);
            if (close < 0 || close > daclEnd)
            {
                return false;
            }

            var ace = sddl[(cursor + 1)..close];
            var parts = ace.Split(';');
            if (parts.Length < 6)
            {
                return false;
            }

            if (parts[0] is "A" or "OA" or "AA")
            {
                var sid = parts[5];
                if (IsBroadPrincipalSid(sid)
                    || !sid.Equals(ownerSid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            cursor = close + 1;
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetFileSddl(string path, out string sddl, out string ownerSid)
    {
        sddl = string.Empty;
        ownerSid = string.Empty;
        var needed = 0u;
        _ = GetFileSecurityW(path, OwnerSecurityInformation | DaclSecurityInformation, IntPtr.Zero, 0, ref needed);
        if (needed == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetFileSecurityW(
                    path,
                    OwnerSecurityInformation | DaclSecurityInformation,
                    buffer,
                    needed,
                    ref needed))
            {
                return false;
            }

            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    buffer,
                    SddlRevision1,
                    OwnerSecurityInformation | DaclSecurityInformation,
                    out var sddlPtr,
                    out _))
            {
                return false;
            }

            try
            {
                sddl = Marshal.PtrToStringUni(sddlPtr) ?? string.Empty;
            }
            finally
            {
                _ = LocalFree(sddlPtr);
            }

            // SDDL owner field: O:<sid>G:...
            if (!sddl.StartsWith("O:", StringComparison.Ordinal))
            {
                return false;
            }

            var ownerEnd = sddl.IndexOf('G', 2);
            if (ownerEnd < 0)
            {
                ownerEnd = sddl.IndexOf('D', 2);
            }

            if (ownerEnd < 0)
            {
                return false;
            }

            ownerSid = sddl[2..ownerEnd];
            return ownerSid.Length > 0 && sddl.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsBroadPrincipalSid(string sid) =>
        sid.Equals("WD", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("BU", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("AU", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("BG", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("WW", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("S-1-1-0", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("S-1-5-11", StringComparison.OrdinalIgnoreCase)
        || sid.Equals("S-1-5-32-545", StringComparison.OrdinalIgnoreCase);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string pathname, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint Geteuid();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurityW(
        string lpFileName,
        uint requestedInformation,
        IntPtr pSecurityDescriptor,
        uint nLength,
        ref uint lpnLengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr securityDescriptor,
        uint requestedStringSDRevision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out uint stringSecurityDescriptorLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
