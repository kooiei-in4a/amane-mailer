using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests;

public sealed class SecretFileWriterTests
{
    [Fact]
    public void Prepare_then_commit_writes_content_and_removes_temp_file()
    {
        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        var writer = new SecretFileWriter(targetPath, scratch.Path);

        writer.Prepare("Endpoint=https://example;AccessKey=secret");
        var entriesWhilePrepared = Directory.GetFiles(scratch.Path);
        Assert.Single(entriesWhilePrepared);
        Assert.NotEqual(targetPath, entriesWhilePrepared[0]);

        writer.Commit();

        Assert.True(File.Exists(targetPath));
        Assert.Equal("Endpoint=https://example;AccessKey=secret", File.ReadAllText(targetPath));
        Assert.Single(Directory.GetFiles(scratch.Path));
    }

    [Fact]
    public void Commit_overwrites_a_pre_existing_empty_file()
    {
        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        File.WriteAllText(targetPath, string.Empty);

        var writer = new SecretFileWriter(targetPath, scratch.Path);
        writer.Prepare("new-value");
        writer.Commit();

        Assert.Equal("new-value", File.ReadAllText(targetPath));
    }

    [Fact]
    public void TryDiscardPrepared_removes_temp_file_without_touching_target_and_returns_true()
    {
        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        var writer = new SecretFileWriter(targetPath, scratch.Path);

        writer.Prepare("value");
        var discarded = writer.TryDiscardPrepared();

        Assert.True(discarded);
        Assert.False(File.Exists(targetPath));
        Assert.Empty(Directory.GetFiles(scratch.Path));
    }

    [Fact]
    public void TryDiscardPrepared_returns_true_when_nothing_was_prepared()
    {
        using var scratch = new ScratchDirectory();
        var writer = new SecretFileWriter(Path.Combine(scratch.Path, "acs_connection_string"), scratch.Path);

        Assert.True(writer.TryDiscardPrepared());
    }

    [Fact]
    public void TryDiscardPrepared_returns_false_and_leaves_the_acs_secret_temp_file_on_disk_when_the_delete_fails()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Directory write-permission enforcement is verified against real POSIX mode bits, Linux only.");
            return;
        }

        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        var writer = new SecretFileWriter(targetPath, scratch.Path);
        const string acsLikeSecret = "Endpoint=https://synthetic.example.communication.azure.com/;AccessKey=SYNTHETIC-NOT-REAL";

        writer.Prepare(acsLikeSecret);
        var tempFileBeforeDiscard = Assert.Single(Directory.GetFiles(scratch.Path));

        // Remove write permission on the containing directory so unlink() fails, simulating a
        // discard delete that cannot complete (not merely a crash).
        File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var discarded = writer.TryDiscardPrepared();

            Assert.False(discarded);
            var tempFileAfterDiscard = Assert.Single(Directory.GetFiles(scratch.Path));
            Assert.Equal(tempFileBeforeDiscard, tempFileAfterDiscard);
            Assert.Equal(acsLikeSecret, File.ReadAllText(tempFileAfterDiscard));
        }
        finally
        {
            File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TryRollbackCommitted_deletes_a_previously_committed_file_and_returns_true()
    {
        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        var writer = new SecretFileWriter(targetPath, scratch.Path);
        writer.Prepare("value");
        writer.Commit();
        Assert.True(File.Exists(targetPath));

        var rolledBack = writer.TryRollbackCommitted();

        Assert.True(rolledBack);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public void TryRollbackCommitted_returns_false_when_the_delete_itself_fails()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Directory write-permission enforcement is verified against real POSIX mode bits, Linux only.");
            return;
        }

        using var scratch = new ScratchDirectory();
        var targetPath = Path.Combine(scratch.Path, "acs_connection_string");
        var writer = new SecretFileWriter(targetPath, scratch.Path);
        writer.Prepare("value");
        writer.Commit();
        Assert.True(File.Exists(targetPath));

        // Remove write permission on the containing directory so unlink() fails, simulating a
        // rollback delete that cannot complete (not merely a crash).
        File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var rolledBack = writer.TryRollbackCommitted();

            Assert.False(rolledBack);
            Assert.True(File.Exists(targetPath), "the target must still be present when rollback failed");
        }
        finally
        {
            File.SetUnixFileMode(scratch.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Prepare_throws_when_target_directory_does_not_exist()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "amane-mailer-missing-" + Guid.NewGuid().ToString("N"));
        var writer = new SecretFileWriter(Path.Combine(missingDirectory, "acs_connection_string"), missingDirectory);

        var ex = Assert.Throws<SecretOperationException>(() => writer.Prepare("value"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, ex.CanonicalCode);
    }

    [Fact]
    public void Prepare_rejects_a_symlinked_target_file()
    {
        using var scratch = new ScratchDirectory();
        var realPath = Path.Combine(scratch.Path, "real-secret");
        File.WriteAllText(realPath, "unrelated");
        var symlinkPath = Path.Combine(scratch.Path, "acs_connection_string");

        try
        {
            File.CreateSymbolicLink(symlinkPath, realPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symlink creation is not permitted in this test environment.");
            return;
        }

        var writer = new SecretFileWriter(symlinkPath, scratch.Path);
        var thrown = Assert.Throws<SecretOperationException>(() => writer.Prepare("value"));
        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, thrown.CanonicalCode);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "amane-mailer-secret-writer-" + Guid.NewGuid().ToString("N"));

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
