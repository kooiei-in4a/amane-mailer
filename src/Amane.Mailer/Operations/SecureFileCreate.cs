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
        var created = false;
        try
        {
            using (var stream = OpenCreateNewWriteStream(path))
            {
                created = true;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            if (!created)
            {
                throw;
            }

            var cleanupFailed = !TryDeleteCreatedFile(path);
            throw new SecureFileWriteException(
                "Failed to write a newly created protected file.",
                ex,
                cleanupFailed);
        }
    }

    public static void WriteAllTextCreateNew(string path, string content)
    {
        WriteAllBytesCreateNew(path, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Test seam: write through a custom stream factory so create-then-write failures can be simulated.
    /// </summary>
    internal static void WriteAllBytesCreateNewForTests(
        string path,
        ReadOnlySpan<byte> content,
        Func<string, Stream> openStream,
        Action<string> deleteFile)
    {
        var created = false;
        try
        {
            using (var stream = openStream(path))
            {
                created = true;
                stream.Write(content);
                if (stream is FileStream fileStream)
                {
                    fileStream.Flush(flushToDisk: true);
                }
                else
                {
                    stream.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            if (!created)
            {
                throw;
            }

            var cleanupFailed = false;
            try
            {
                deleteFile(path);
            }
            catch
            {
                cleanupFailed = true;
            }

            throw new SecureFileWriteException(
                "Failed to write a newly created protected file.",
                ex,
                cleanupFailed);
        }
    }

    private static bool TryDeleteCreatedFile(string path)
    {
        try
        {
            // Do not probe with File.Exists first: it returns false on access/IO errors and can
            // hide an orphaned create-new file as a successful cleanup.
            File.Delete(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
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
