using System.Diagnostics;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupSecureWriterTests
{
    [Fact]
    public void Linux_create_uses_0600_before_content_is_durable()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("UnixCreateMode 0600 is verified on Linux.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "amane-secure-" + Guid.NewGuid().ToString("N"));
        TestSecretDirectory.CreateSecure(dir);
        try
        {
            var path = Path.Combine(dir, "secret-file");
            SecureFileCreate.WriteAllTextCreateNew(path, "secret-value");
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            Assert.Equal(0, (int)(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                           UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Windows_create_applies_owner_only_acl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows owner-only ACL is verified on Windows.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "amane-secure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "secret-file");
            SecureFileCreate.WriteAllTextCreateNew(path, "secret-value");
            Assert.Equal("secret-value", File.ReadAllText(path));

            var psi = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                ArgumentList = { path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("icacls failed to start.");
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            // Owner-only create uses a protected DACL for the current user; inheritance should not
            // reintroduce BUILTIN\Users read grants on the new file.
            Assert.DoesNotContain("BUILTIN\\Users:(R)", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Environment.UserName, stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SecretFileWriter_temp_file_is_owner_only_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix mode check is Linux-only.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "amane-secure-" + Guid.NewGuid().ToString("N"));
        TestSecretDirectory.CreateSecure(dir);
        try
        {
            var target = Path.Combine(dir, "acs_connection_string");
            var writer = new SecretFileWriter(target, dir);
            writer.Prepare("Endpoint=https://example;AccessKey=secret");
            var temp = Assert.Single(Directory.GetFiles(dir));
            var mode = File.GetUnixFileMode(temp);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            writer.TryDiscardPrepared();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
