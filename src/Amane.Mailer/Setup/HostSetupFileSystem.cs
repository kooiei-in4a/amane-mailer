using System.Runtime.InteropServices;
using System.Security.Principal;
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

    public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
        FileSystemLinkInspector.Inspect(path) switch
        {
            FileSystemLinkInspectionResult.NotALink => SetupLinkInspectionResult.NotALink,
            FileSystemLinkInspectionResult.IsLinkOrReparse => SetupLinkInspectionResult.IsLinkOrReparse,
            _ => SetupLinkInspectionResult.InspectionFailed,
        };

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

    public void FlushFile(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            FlushFileUnix(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            FlushFileWindows(path);
            return;
        }

        throw new IOException("File durability flush is not supported on this platform.");
    }


    public FileStream OpenExclusiveGenerationLock(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return OpenExclusiveGenerationLockUnix(path);
        }

        if (OperatingSystem.IsWindows())
        {
            return OpenExclusiveGenerationLockWindows(path);
        }

        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void FlushFileUnix(string path)
    {
        var fd = Open(path, ORdOnly);
        if (fd < 0)
        {
            throw new IOException("Failed to open file for durability flush.");
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                throw new IOException("Failed to fsync file metadata.");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void FlushFileWindows(string path)
    {
        using var handle = CreateFileW(
            path,
            dwDesiredAccess: 0x40000000, // GENERIC_WRITE
            dwShareMode: 0x7,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: 3, // OPEN_EXISTING
            dwFlagsAndAttributes: 0x80, // FILE_ATTRIBUTE_NORMAL
            hTemplateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Failed to open file for durability flush.");
        }

        if (!FlushFileBuffers(handle))
        {
            throw new IOException("Failed to flush file buffers.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyFileWindows(string path)
    {
        if (!TryGetFileSddl(path, out var sddl, out var ownerSid))
        {
            return false;
        }

        return IsOwnerOnlySddl(sddl, ownerSid);
    }

    /// <summary>Test seam for Windows SDDL owner-only parsing (including unknown ACE fail-closed).</summary>
    internal static bool IsOwnerOnlySddl(string sddl, string ownerSid)
    {
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
            else if (parts[0] is "D" or "OD" or "DA")
            {
                // Explicit deny ACEs are acceptable for lockdown.
            }
            else
            {
                // Unknown / callback / object ACE types (XA, ZA, ...) — fail closed.
                return false;
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

    private const int ORdWr = 2;
    private const int OCreat = 64; // 0x40
    private const int UnixOwnerReadWriteMode = 384; // 0600

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static FileStream OpenExclusiveGenerationLockUnix(string path)
    {
        // Linux O_NOFOLLOW=0x20000, O_CLOEXEC=0x80000; macOS O_NOFOLLOW=0x100, O_CLOEXEC=0x1000000.
        var noFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        var cloExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        var fd = Open3(path, ORdWr | OCreat | noFollow | cloExec, UnixOwnerReadWriteMode);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is 40 or 62) // ELOOP
            {
                throw new IOException("Generation lock path resolved through a symlink.", errno);
            }

            throw new IOException($"Failed to open generation lock (errno {errno}).", errno);
        }

        // Own the fd immediately so every exception path releases flock via Dispose.
        SafeFileHandle? handle = null;
        try
        {
            handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            fd = -1;

            const int lockEx = 2;
            const int lockNb = 4;
            var rawFd = handle.DangerousGetHandle().ToInt32();
            if (Flock(rawFd, lockEx | lockNb) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                // EAGAIN/EWOULDBLOCK: Linux 11; macOS EAGAIN often 35.
                if (errno is 11 or 35)
                {
                    throw new IOException(
                        "Generation lock is being used by another process.",
                        unchecked((int)0x80070020));
                }

                throw new IOException($"Failed to flock generation lock (errno {errno}).", errno);
            }

            // Prefer the open descriptor (Linux /proc/self/fd/N) so mode is for the locked inode.
            var mode = GetUnixFileModeForOpenHandle(handle, path);
            const UnixFileMode groupOrOther =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & groupOrOther) != 0)
            {
                throw new UnauthorizedAccessException("Generation lock file permissions are not owner-only.");
            }

            var stream = new FileStream(handle, FileAccess.ReadWrite);
            handle = null; // ownership transferred to FileStream
            return stream;
        }
        finally
        {
            handle?.Dispose();
            if (fd >= 0)
            {
                _ = Close(fd);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static UnixFileMode GetUnixFileModeForOpenHandle(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var rawFd = handle.DangerousGetHandle().ToInt32();
            return File.GetUnixFileMode($"/proc/self/fd/{rawFd}");
        }

        return File.GetUnixFileMode(path);
    }

    [SupportedOSPlatform("windows")]
    private FileStream OpenExclusiveGenerationLockWindows(string path)
    {
        const uint genericRead = 0x80000000;
        const uint genericWrite = 0x40000000;
        const uint createNew = 1;
        const uint openExisting = 3;
        const uint fileAttributeNormal = 0x80;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        const uint fileAttributeReparsePoint = 0x400;
        const int errorFileNotFound = 2;
        const int errorPathNotFound = 3;
        const int errorSharingViolation = 32;
        const int errorFileExists = 80;
        const int errorAlreadyExists = 183;

        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        var sddl = $"D:P(A;;FA;;;{sid.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SddlRevision1,
                out var securityDescriptor,
                out _))
        {
            throw new IOException("Failed to build an owner-only security descriptor for the generation lock.");
        }

        SafeFileHandle? handle = null;
        try
        {
            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                lpSecurityDescriptor = securityDescriptor,
                bInheritHandle = 0,
            };

            // Atomic create-or-open with OPEN_REPARSE_POINT so a symlink is never followed.
            handle = CreateFileWWithSecurity(
                path,
                genericRead | genericWrite,
                dwShareMode: 0,
                ref attributes,
                createNew,
                fileAttributeNormal | fileFlagOpenReparsePoint,
                hTemplateFile: IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var createError = Marshal.GetLastWin32Error();
                handle.Dispose();
                handle = null;

                if (createError == errorSharingViolation)
                {
                    throw Win32LockIOException(
                        "Generation lock is being used by another process.",
                        createError);
                }

                if (createError is not (errorFileExists or errorAlreadyExists))
                {
                    throw Win32LockIOException(
                        $"Failed to create generation lock (Win32 error {createError}).",
                        createError);
                }

                handle = CreateFileW(
                    path,
                    genericRead | genericWrite,
                    dwShareMode: 0,
                    lpSecurityAttributes: IntPtr.Zero,
                    openExisting,
                    fileAttributeNormal | fileFlagOpenReparsePoint,
                    hTemplateFile: IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var openError = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;

                    if (openError is errorSharingViolation or errorFileExists or errorAlreadyExists)
                    {
                        throw Win32LockIOException(
                            "Generation lock is being used by another process.",
                            openError == errorSharingViolation ? errorSharingViolation : openError);
                    }

                    if (openError is errorFileNotFound or errorPathNotFound)
                    {
                        // Lost a create/delete race; treat as contention so callers retry/serialize.
                        throw Win32LockIOException(
                            "Generation lock is being used by another process.",
                            errorSharingViolation);
                    }

                    throw Win32LockIOException(
                        $"Failed to open generation lock (Win32 error {openError}).",
                        openError);
                }
            }

            if (!GetFileInformationByHandle(handle, out var info))
            {
                var infoError = Marshal.GetLastWin32Error();
                throw Win32LockIOException(
                    $"Failed to inspect generation lock handle (Win32 error {infoError}).",
                    infoError);
            }

            if ((info.FileAttributes & fileAttributeReparsePoint) != 0)
            {
                throw new IOException("Generation lock path must not be a reparse point.");
            }

            if (!IsOwnerOnlyFileHandleWindows(handle))
            {
                throw new UnauthorizedAccessException("Generation lock file permissions are not owner-only.");
            }

            var stream = new FileStream(handle, FileAccess.ReadWrite);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IOException Win32LockIOException(string message, int win32Error) =>
        new(message, unchecked((int)0x80070000) | win32Error);

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyFileHandleWindows(SafeFileHandle handle)
    {
        var status = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            out var securityDescriptor);
        if (status != 0 || securityDescriptor == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    securityDescriptor,
                    SddlRevision1,
                    OwnerSecurityInformation | DaclSecurityInformation,
                    out var sddlPtr,
                    out _))
            {
                return false;
            }

            try
            {
                var sddl = Marshal.PtrToStringUni(sddlPtr) ?? string.Empty;
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

                var ownerSid = sddl[2..ownerEnd];
                return IsOwnerOnlySddl(sddl, ownerSid);
            }
            finally
            {
                _ = LocalFree(sddlPtr);
            }
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    private const uint SeFileObject = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileWWithSecurity(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInfo,
        IntPtr sidOwner,
        IntPtr sidGroup,
        IntPtr dacl,
        IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open3(string pathname, int flags, int mode);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

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
