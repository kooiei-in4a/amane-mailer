using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amane.Mailer.Setup.NonInteractive;

/// <summary>
/// TOCTOU-safe owner-only config read for non-interactive setup (issue #453).
/// Linux uses <c>statx(AT_EMPTY_PATH)</c> on an open handle; Windows uses handle metadata.
/// macOS and other platforms fail closed without path-based metadata fallback.
/// </summary>
internal static partial class SetupNonInteractiveConfigReader
{
    internal const int MaxConfigBytes = 256 * 1024;

    internal const uint StatxType = 0x0000_0001u;
    internal const uint StatxMode = 0x0000_0002u;
    internal const uint StatxUid = 0x0000_0008u;
    internal const uint StatxSize = 0x0000_0200u;
    internal const uint StatxRequiredMask = StatxType | StatxMode | StatxUid | StatxSize;
    internal const uint StatxBasicStats = 0x0000_07ffu;
    internal const int AtEmptyPath = 0x1000;
    internal const int LinuxStatxSize = 256;

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
            if (OperatingSystem.IsLinux())
            {
                return ReadLinux(fileSystem, fullPath);
            }

            if (OperatingSystem.IsWindows())
            {
                return ReadWindows(fileSystem, fullPath);
            }

            // macOS and any other host: no fstat / path-based metadata fallback.
            return Fail(SetupNonInteractiveResultCode.UnsupportedPlatform);
        }
        catch (EntryPointNotFoundException)
        {
            return Fail(SetupNonInteractiveResultCode.UnsupportedPlatform);
        }
        catch (DllNotFoundException)
        {
            return Fail(SetupNonInteractiveResultCode.UnsupportedPlatform);
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

    [SupportedOSPlatform("linux")]
    private static ReadOutcome ReadLinux(ISetupFileSystem fileSystem, string fullPath)
    {
        const int oRdOnly = 0;
        const int oNoFollow = 0x20000;
        const int oCloExec = 0x80000;
        var fd = Open3(fullPath, oRdOnly | oNoFollow | oCloExec, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == 38) // ENOSYS
            {
                return Fail(SetupNonInteractiveResultCode.UnsupportedPlatform);
            }

            return errno is 2 or 20
                ? Fail(SetupNonInteractiveResultCode.ConfigNotFound)
                : Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        SafeFileHandle? handle = null;
        try
        {
            handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            fd = -1;

            if (!TryStatx(handle, out var beforeStat, out var statxFailure))
            {
                return Fail(statxFailure);
            }

            if (!IsRegularFile(beforeStat))
            {
                return Fail(SetupNonInteractiveResultCode.ConfigNotRegularFile);
            }

            if (!IsOwnerOnly(beforeStat, fileSystem, out var ownerFailure))
            {
                return Fail(ownerFailure);
            }

            var length = (long)beforeStat.Size;
            if (length < 0 || length > MaxConfigBytes)
            {
                return Fail(SetupNonInteractiveResultCode.ConfigTooLarge);
            }

            using var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            var buffer = new byte[length];
            if (!TryReadExact(stream, buffer))
            {
                return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
            }

            // Reject post-open growth: one extra byte must not be available.
            Span<byte> growthProbe = stackalloc byte[1];
            if (stream.Read(growthProbe) != 0)
            {
                return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
            }

            if (!TryStatx(stream.SafeFileHandle, out var afterStat, out var afterFailure))
            {
                return Fail(afterFailure);
            }

            if (!SameFileIdentity(beforeStat, afterStat) || afterStat.Size != beforeStat.Size)
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
        if (!TryReadExact(stream, buffer))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        Span<byte> growthProbe = stackalloc byte[1];
        if (stream.Read(growthProbe) != 0)
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        if (!GetFileInformationByHandle(handle, out var after))
        {
            return Fail(SetupNonInteractiveResultCode.ConfigPathUnsafe);
        }

        var afterLength = (long)after.FileSizeHigh << 32 | after.FileSizeLow;
        if (after.VolumeSerialNumber != info.VolumeSerialNumber
            || after.FileIndexHigh != info.FileIndexHigh
            || after.FileIndexLow != info.FileIndexLow
            || afterLength != length)
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

    internal static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    [SupportedOSPlatform("linux")]
    private static bool TryStatx(
        SafeFileHandle handle,
        out LinuxStatxView view,
        out string failureCode)
    {
        view = default;
        failureCode = SetupNonInteractiveResultCode.UnsupportedPlatform;
        LinuxStatxBuffer buffer = default;
        int rc;
        try
        {
            rc = Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath,
                StatxBasicStats,
                ref buffer);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }

        if (rc != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            failureCode = errno == 38 // ENOSYS
                ? SetupNonInteractiveResultCode.UnsupportedPlatform
                : SetupNonInteractiveResultCode.ConfigPathUnsafe;
            return false;
        }

        if ((buffer.Mask & StatxRequiredMask) != StatxRequiredMask)
        {
            failureCode = SetupNonInteractiveResultCode.ConfigPathUnsafe;
            return false;
        }

        view = new LinuxStatxView(
            buffer.Mask,
            buffer.Mode,
            buffer.Uid,
            buffer.Size,
            buffer.Ino,
            buffer.DevMajor,
            buffer.DevMinor);
        failureCode = string.Empty;
        return true;
    }

    private static bool IsRegularFile(LinuxStatxView stat)
    {
        const int sIfmt = 0xF000;
        const int sIfreg = 0x8000;
        return (stat.Mode & sIfmt) == sIfreg;
    }

    private static bool IsOwnerOnly(
        LinuxStatxView stat,
        ISetupFileSystem fileSystem,
        out string failureCode)
    {
        const int sIrwxg = 0x0038;
        const int sIrwxo = 0x0007;
        if ((stat.Mode & (sIrwxg | sIrwxo)) != 0)
        {
            failureCode = SetupNonInteractiveResultCode.ConfigPermissionsRejected;
            return false;
        }

        var userId = fileSystem.GetEffectiveUnixUserId();
        if (userId is null)
        {
            // Fail closed: never treat missing euid as "owner verified".
            failureCode = SetupNonInteractiveResultCode.ConfigPermissionsRejected;
            return false;
        }

        if (stat.Uid != userId.Value)
        {
            failureCode = SetupNonInteractiveResultCode.ConfigPermissionsRejected;
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    private static bool SameFileIdentity(LinuxStatxView before, LinuxStatxView after) =>
        before.Ino == after.Ino
        && before.DevMajor == after.DevMajor
        && before.DevMinor == after.DevMinor;

    private static ReadOutcome Fail(string code) =>
        new() { Succeeded = false, FailureCode = code };

    internal readonly struct LinuxStatxView
    {
        internal LinuxStatxView(
            uint mask,
            ushort mode,
            uint uid,
            ulong size,
            ulong ino,
            uint devMajor,
            uint devMinor)
        {
            Mask = mask;
            Mode = mode;
            Uid = uid;
            Size = size;
            Ino = ino;
            DevMajor = devMajor;
            DevMinor = devMinor;
        }

        internal uint Mask { get; }
        internal ushort Mode { get; }
        internal uint Uid { get; }
        internal ulong Size { get; }
        internal ulong Ino { get; }
        internal uint DevMajor { get; }
        internal uint DevMinor { get; }
    }

    /// <summary>
    /// Linux UAPI <c>struct statx</c> (size 0x100). Field offsets follow
    /// <c>include/uapi/linux/stat.h</c>, including reserved trailing space.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = LinuxStatxSize)]
    internal struct LinuxStatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(4)] public uint Blksize;
        [FieldOffset(8)] public ulong Attributes;
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(56)] public ulong AttributesMask;
        // Timestamps and device ids occupy later offsets; only the fields above are read.
        [FieldOffset(128)] public uint RdevMajor;
        [FieldOffset(132)] public uint RdevMinor;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [SupportedOSPlatform("linux")]
    private static partial int Open3(string pathname, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [SupportedOSPlatform("linux")]
    private static partial int Statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        ref LinuxStatxBuffer statxbuf);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
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
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}
