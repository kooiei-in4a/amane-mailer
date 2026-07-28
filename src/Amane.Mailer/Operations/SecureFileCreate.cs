using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amane.Mailer.Operations;

/// <summary>
/// Creates files with owner-only permissions before content is written.
/// Linux uses FileStreamOptions.UnixCreateMode (0600). Windows creates the file
/// with an explicit DACL that grants the current user only (no inheritance).
/// </summary>
internal static class SecureFileCreate
{
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;
    private const uint SddlRevision1 = 1;
    private const uint DaclSecurityInformation = 0x00000004;

    public static void WriteAllBytesCreateNew(string path, ReadOnlySpan<byte> content)
    {
        using var stream = OpenCreateNewWriteStream(path);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public static void WriteAllTextCreateNew(string path, string content)
    {
        WriteAllBytesCreateNew(path, Encoding.UTF8.GetBytes(content));
    }

    public static FileStream OpenCreateNewWriteStream(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            return OpenCreateNewWriteStreamWindows(path);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            return new FileStream(path, options);
        }

        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public static void EnsureOwnerOnlyDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            ApplyOwnerOnlyAclWindows(directoryPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenCreateNewWriteStreamWindows(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        var sddl = $"D:P(A;;FA;;;{sid.Value})";

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SddlRevision1,
                out var securityDescriptor,
                out _))
        {
            throw new IOException("Failed to build an owner-only security descriptor for secure file create.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                lpSecurityDescriptor = securityDescriptor,
                bInheritHandle = 0,
            };

            SafeFileHandle? handle = null;
            try
            {
                handle = CreateFileW(
                    path,
                    GenericWrite,
                    dwShareMode: 0,
                    ref attributes,
                    CreateNew,
                    FileAttributeNormal,
                    hTemplateFile: IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;
                    throw new IOException($"Failed to create a protected file (Win32 error {error}).");
                }

                var stream = new FileStream(handle, FileAccess.Write);
                handle = null; // ownership transferred to FileStream
                return stream;
            }
            finally
            {
                handle?.Dispose();
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAclWindows(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        var sddl = $"D:P(A;;FA;;;{sid.Value})";

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SddlRevision1,
                out var securityDescriptor,
                out _))
        {
            throw new IOException("Failed to build an owner-only security descriptor for directory ACL.");
        }

        try
        {
            if (!SetFileSecurityW(path, DaclSecurityInformation, securityDescriptor))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to apply owner-only directory ACL (Win32 error {error}).");
            }
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurityW(
        string lpFileName,
        uint securityInformation,
        IntPtr pSecurityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
