using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amane.Mailer.Setup.NonInteractive;

/// <summary>
/// TOCTOU-safe owner-only config read for non-interactive setup (issue #453).
/// Opens with no-follow, revalidates on the handle, then reads up to 256 KiB.
/// </summary>
internal static class SetupNonInteractiveConfigReader
{
    internal const int MaxConfigBytes = 256 * 1024;

    internal sealed class ReadOutcome
    {
        internal required bool Succeeded { get; init; }
        internal string FailureCode { get; init; } = string.Empty;
        internal byte[] Content { get; init; } = [];
    }

    internal static ReadOutcome Read(ISetupFileSystem fileSystem, string configPath)
    {
        if (!TryResolveAbsolutePath(configPath, out var fullPath))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathRejected);
        }

        if (SetupPathGuard.HasSymlinkOrReparseInAncestry(fileSystem, fullPath))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        if (!fileSystem.FileExists(fullPath))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigNotFound);
        }

        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return ReadUnix(fileSystem, fullPath);
            }

            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(fileSystem, fullPath);
            }

            return ReadFallback(fileSystem, fullPath);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPermissionsRejected);
        }
        catch (IOException)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }
    }

    internal static bool TryResolveAbsolutePath(string rawPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathRooted(rawPath))
        {
            return false;
        }

        fullPath = Path.GetFullPath(rawPath);
        return Path.IsPathRooted(fullPath);
    }

    private static ReadOutcome ReadFallback(ISetupFileSystem fileSystem, string fullPath)
    {
        if (SetupPathGuard.IsUnsafeLink(fileSystem.InspectSymlinkOrReparsePoint(fullPath))
            || !fileSystem.IsOwnerOnlyFile(fullPath))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPermissionsRejected);
        }

        var bytes = fileSystem.ReadAllBytes(fullPath);
        return ValidateSize(bytes);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static ReadOutcome ReadUnix(ISetupFileSystem fileSystem, string fullPath)
    {
        const int oRdOnly = 0;
        var noFollow = OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        var cloExec = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        var fd = Open3(fullPath, oRdOnly | noFollow | cloExec, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno is 2 or 20
                ? Fail(SetupNonInteractiveResultCode.ConfigNotFound)
                : Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            fd = -1;

            if (!IsRegularFileUnix(handle))
            {
                return Fail(SetupNonInteractiveResultCode.ConfigNotRegularFile);
            }

            if (!IsOwnerOnlyHandleUnix(handle, fullPath, fileSystem))
            {
                return Fail(SetupNonInteractiveResultCode.ConfigPermissionsRejected);
            }

            var length = GetLengthUnix(handle);
            if (length < 0 || length > MaxConfigBytes)
            {
                return Fail(SetupNonInteractiveResultCode.ConfigTooLarge);
            }

            using var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read != length)
            {
                return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
            }

            return new ReadOutcome { Succeeded = true, Content = buffer };
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

    [SupportedOSPlatform("windows")]
    private static ReadOutcome ReadWindows(ISetupFileSystem fileSystem, string fullPath)
    {
        const uint genericRead = 0x80000000;
        const uint fileShareRead = 0x00000001;
        const uint openExisting = 3;
        const uint fileAttributeNormal = 0x80;
        const uint fileFlagOpenReparsePoint = 0x00200000;
        const uint fileAttributeDirectory = 0x10;
        const uint fileAttributeReparsePoint = 0x400;

        using var handle = CreateFileW(
            fullPath,
            genericRead,
            fileShareRead,
            IntPtr.Zero,
            openExisting,
            fileAttributeNormal | fileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error is 2 or 3
                ? Fail(SetupNonInteractiveResultCode.ConfigNotFound)
                : Fail(SetupNonInteractiveResultCode.ConfigPermissionsRejected);
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        if ((info.FileAttributes & (fileAttributeDirectory | fileAttributeReparsePoint)) != 0)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigNotRegularFile);
        }

        if (!fileSystem.IsOwnerOnlyFile(fullPath))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPermissionsRejected);
        }

        var length = (long)info.FileSizeHigh << 32 | info.FileSizeLow;
        if (length < 0 || length > MaxConfigBytes)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigTooLarge);
        }

        using var stream = new FileStream(handle, FileAccess.Read);
        var buffer = new byte[length];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read != length)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        return new ReadOutcome { Succeeded = true, Content = buffer };
    }

    internal static string DecodeUtf8(ReadOnlySpan<byte> content, out bool validUtf8)
    {
        validUtf8 = true;
        var span = content;
        if (span.Length >= 3
            && span[0] == 0xEF
            && span[1] == 0xBB
            && span[2] == 0xBF)
        {
            span = span[3..];
        }

        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return encoding.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            validUtf8 = false;
            return string.Empty;
        }
    }

    private static ReadOutcome ValidateSize(byte[] bytes) =>
        bytes.Length > MaxConfigBytes
            ? Fail(SetupNonInteractiveResultCode.ConfigTooLarge)
            : new ReadOutcome { Succeeded = true, Content = bytes };

    private static ReadOutcome Fail(string code) =>
        new() { Succeeded = false, FailureCode = code };

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool IsRegularFileUnix(SafeFileHandle handle)
    {
        if (FStat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0)
        {
            return false;
        }

        const int sIfmt = 0xF000;
        const int sIfreg = 0x8000;
        return (stat.st_mode & sIfmt) == sIfreg;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static long GetLengthUnix(SafeFileHandle handle)
    {
        if (FStat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0)
        {
            return -1;
        }

        return stat.st_size;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool IsOwnerOnlyHandleUnix(
        SafeFileHandle handle,
        string path,
        ISetupFileSystem fileSystem)
    {
        if (FStat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0)
        {
            return false;
        }

        const int sIrwxg = 0x0038;
        const int sIrwxo = 0x0007;
        if ((stat.st_mode & (sIrwxg | sIrwxo)) != 0)
        {
            return false;
        }

        var userId = fileSystem.GetEffectiveUnixUserId();
        return userId is null || stat.st_uid == userId.Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Stat
    {
        public long st_dev;
        public long st_ino;
        public int st_mode;
        public int st_nlink;
        public int st_uid;
        public int st_gid;
        public long st_rdev;
        public long st_size;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open3(string pathname, int flags, int mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fd, out Stat stat);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}
