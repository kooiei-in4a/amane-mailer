using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests;

public sealed class TwoPhaseSecretWriteCoordinatorTests
{
    [Fact]
    public void WriteBoth_commits_both_files_on_success()
    {
        using var firstDir = new ScratchDirectory();
        using var secondDir = new ScratchDirectory();
        var firstPath = Path.Combine(firstDir.Path, "acs_connection_string");
        var secondPath = Path.Combine(secondDir.Path, "platform-sender.json");

        TwoPhaseSecretWriteCoordinator.WriteBoth(
            new SecretFileWriter(firstPath), "first-content",
            new SecretFileWriter(secondPath), "second-content");

        Assert.Equal("first-content", File.ReadAllText(firstPath));
        Assert.Equal("second-content", File.ReadAllText(secondPath));
    }

    [Fact]
    public void WriteBoth_rolls_back_the_first_commit_when_the_second_commit_fails()
    {
        using var firstDir = new ScratchDirectory();
        using var secondDir = new ScratchDirectory();
        var firstPath = Path.Combine(firstDir.Path, "acs_connection_string");
        var secondPath = Path.Combine(secondDir.Path, "platform-sender.json");

        // Force the second commit to fail deterministically on any OS: File.Move refuses to
        // rename a file onto an existing directory regardless of overwrite:true. Prepare() only
        // touches the parent directory (still valid), so this failure surfaces at Commit() —
        // exactly the "second file fails to commit after the first already succeeded" scenario.
        Directory.CreateDirectory(secondPath);

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBoth(
                new SecretFileWriter(firstPath), "first-content",
                new SecretFileWriter(secondPath), "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack, ex.CanonicalCode);
        Assert.False(File.Exists(firstPath), "the first file must be rolled back, not left partially registered");
    }

    [Fact]
    public void WriteBoth_leaves_neither_file_when_the_first_prepare_fails()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "amane-mailer-missing-" + Guid.NewGuid().ToString("N"));
        using var secondDir = new ScratchDirectory();
        var firstPath = Path.Combine(missingDirectory, "acs_connection_string");
        var secondPath = Path.Combine(secondDir.Path, "platform-sender.json");

        Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBoth(
                new SecretFileWriter(firstPath), "first-content",
                new SecretFileWriter(secondPath), "second-content"));

        Assert.False(File.Exists(secondPath));
        Assert.Empty(Directory.GetFiles(secondDir.Path));
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "amane-mailer-two-phase-" + Guid.NewGuid().ToString("N"));

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
