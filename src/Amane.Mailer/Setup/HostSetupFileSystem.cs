using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amane.Mailer.Operations;
using Microsoft.Win32.SafeHandles;

namespace Amane.Mailer.Setup;

public sealed class HostSetupFileSystem : ISetupFileSystem
{
    private const int ORdOnly = 0;
    private const int ODirectory = 65536; // 0x10000 on Linux

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool IsSymlinkOrReparsePoint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    return true;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    return true;
                }

                return info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsPathHasReparsePoint(path);
            }

            var dangling = new FileInfo(path);
            return dangling.LinkTarget is not null
                || ((dangling.Exists || dangling.LinkTarget is not null)
                    && dangling.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch
        {
            return false;
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
        }
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

    public uint? GetEffectiveUnixUserId()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        return Geteuid();
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsPathHasReparsePoint(string path)
    {
        var attrs = GetFileAttributesW(path);
        if (attrs == unchecked((uint)(-1)))
        {
            return false;
        }

        const uint fileAttributeReparsePoint = 0x400;
        return (attrs & fileAttributeReparsePoint) != 0;
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
        // Best-effort volume/directory flush. Opening a directory with BACKUP_SEMANTICS and
        // FlushFileBuffers is not reliably available for all Windows/Docker Desktop setups;
        // file contents are already Flush(flushToDisk: true). Linux fsync remains mandatory.
        using var handle = CreateFileW(
            path,
            dwDesiredAccess: 0x80,
            dwShareMode: 0x7,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: 3,
            dwFlagsAndAttributes: 0x02000000,
            hTemplateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return;
        }

        _ = FlushFileBuffers(handle);
    }

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
}
