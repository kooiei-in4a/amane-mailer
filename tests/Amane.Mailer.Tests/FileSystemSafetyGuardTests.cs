using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests;

/// <summary>
/// Direct coverage for the write/read probe and owner-only mode check that back
/// <c>admin provider register-acs</c> preflight (MAILER-ACS-INPUT-01, point 4: "Dockerfileの実
/// UID/GID、Composeのuserとsecret directoryのowner/mode整合を...CIでwrite/read probeを実行する").
/// The owner/mode assertions only apply on Linux, matching this repository's existing stance that
/// Windows dev/test cannot substitute for Linux owner/mode verification; on Windows they no-op by
/// skipping rather than silently passing on unrelated logic.
/// </summary>
public sealed class FileSystemSafetyGuardTests
{
    [Fact]
    public void EnsureDirectoryIsSafe_accepts_an_owner_only_directory()
    {
        using var scratch = new ScratchDirectory();

        var exception = Record.Exception(() => FileSystemSafetyGuard.EnsureDirectoryIsSafe(scratch.Path));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureDirectoryIsSafe_rejects_group_readable_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Owner/mode enforcement only runs on Linux.");
            return;
        }

        using var scratch = new ScratchDirectory();
        File.SetUnixFileMode(
            scratch.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        var ex = Assert.Throws<SecretOperationException>(() => FileSystemSafetyGuard.EnsureDirectoryIsSafe(scratch.Path));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, ex.CanonicalCode);
    }

    [Fact]
    public void EnsureDirectoryIsSafe_rejects_other_readable_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Owner/mode enforcement only runs on Linux.");
            return;
        }

        using var scratch = new ScratchDirectory();
        File.SetUnixFileMode(
            scratch.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

        var ex = Assert.Throws<SecretOperationException>(() => FileSystemSafetyGuard.EnsureDirectoryIsSafe(scratch.Path));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, ex.CanonicalCode);
    }

    [Fact]
    public void EnsureDirectoryIsSafe_rejects_a_missing_directory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "amane-mailer-missing-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<SecretOperationException>(() => FileSystemSafetyGuard.EnsureDirectoryIsSafe(missing));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, ex.CanonicalCode);
    }

    [Fact]
    public void EnsureDirectoryIsWritable_succeeds_for_an_owner_writable_directory()
    {
        using var scratch = new ScratchDirectory();

        var exception = Record.Exception(() => FileSystemSafetyGuard.EnsureDirectoryIsWritable(scratch.Path));

        Assert.Null(exception);
        Assert.Empty(Directory.GetFiles(scratch.Path));
    }

    [Fact]
    public void EnsureDirectoryIsWritable_rejects_a_directory_without_write_permission()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Write-permission enforcement is verified against real POSIX mode bits, Linux only.");
            return;
        }

        using var scratch = new ScratchDirectory();
        File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var ex = Assert.Throws<SecretOperationException>(() => FileSystemSafetyGuard.EnsureDirectoryIsWritable(scratch.Path));
            Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryNotWritable, ex.CanonicalCode);
        }
        finally
        {
            // Restore write permission so ScratchDirectory.Dispose can delete the directory.
            File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "amane-mailer-fs-guard-" + Guid.NewGuid().ToString("N"));

        public ScratchDirectory() => TestSecretDirectory.CreateSecure(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
