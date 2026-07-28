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
    public void Real_secret_file_writer_leaves_the_acs_secret_temp_file_readable_on_disk_when_discard_fails_after_a_prepare_failure()
    {
        // End-to-end (real SecretFileWriter, real filesystem) version of the exact scenario the
        // reviewer flagged: first.Prepare() succeeds and writes an ACS-secret-like temp file,
        // second.Prepare() fails, and cleaning up the first's temp file also fails. The temp file
        // must remain verifiably on disk (not silently swallowed), matching the fake-driven
        // WriteBothCore_reports_cleanup_failed_when_the_second_prepare_fails_and_the_first_discard_also_fails
        // above, which asserts the coordinator's canonical-code branch for this same sequence.
        // WriteBoth itself calls first.Prepare() before second.Prepare() can fail, so there is no
        // hook to flip firstDir's permission in between from outside — this drives the same two
        // real SecretFileWriter calls the coordinator would make, in the same order, directly.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Directory write-permission enforcement is verified against real POSIX mode bits, Linux only.");
            return;
        }

        using var firstDir = new ScratchDirectory();
        var firstPath = Path.Combine(firstDir.Path, "acs_connection_string");
        var missingSecondDirectory = Path.Combine(Path.GetTempPath(), "amane-mailer-missing-" + Guid.NewGuid().ToString("N"));
        var secondPath = Path.Combine(missingSecondDirectory, "platform-sender.json");
        const string acsLikeSecret = "Endpoint=https://synthetic.example.communication.azure.com/;AccessKey=SYNTHETIC-NOT-REAL";

        var first = new SecretFileWriter(firstPath);
        first.Prepare(acsLikeSecret);
        var tempFileBeforePrepareFailure = Assert.Single(Directory.GetFiles(firstDir.Path));

        File.SetUnixFileMode(firstDir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var second = new SecretFileWriter(secondPath);
            var prepareEx = Record.Exception(() => second.Prepare("second-content"));
            Assert.NotNull(prepareEx);

            var discarded = first.TryDiscardPrepared();

            Assert.False(discarded);
            var tempFileAfterDiscardFailure = Assert.Single(Directory.GetFiles(firstDir.Path));
            Assert.Equal(tempFileBeforePrepareFailure, tempFileAfterDiscardFailure);
            Assert.Equal(acsLikeSecret, File.ReadAllText(tempFileAfterDiscardFailure));
        }
        finally
        {
            File.SetUnixFileMode(firstDir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        Assert.Empty(Directory.GetFiles(firstDir.Path));
        // secondDir.Path contains the `secondPath` obstruction directory created above, which
        // Directory.GetFiles does not return (it only lists files) — this isolates whether
        // second's own temp file (`.platform-sender.json.tmp-*`) was left behind uncleaned.
        Assert.Empty(Directory.GetFiles(secondDir.Path));
    }

    [Fact]
    public void WriteBothCore_reports_rollback_failed_when_the_rollback_itself_fails()
    {
        // Forcing a real rollback delete to fail at exactly the right moment (after the first
        // commit but before the second's failure) is not reliably reproducible through the real
        // filesystem within a single synchronous WriteBoth call. ISecretFileWriter exists so this
        // can be exercised deterministically instead.
        var first = new FakeSecretFileWriter { CommitSucceeds = true, RollbackSucceeds = false };
        var second = new FakeSecretFileWriter { CommitSucceeds = false };

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBothCore(first, "first-content", second, "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed, ex.CanonicalCode);
        Assert.True(first.RollbackAttempted);
        Assert.True(second.DiscardAttempted, "the second writer's temp file must still be discarded even when the first's rollback fails");
    }

    [Fact]
    public void WriteBothCore_reports_cleanup_failed_when_the_second_commit_fails_and_the_second_discard_also_fails()
    {
        // Rollback of the first succeeds, but cleaning up the second's own temp file fails —
        // distinct from the rollback-failure branch above.
        var first = new FakeSecretFileWriter { CommitSucceeds = true, RollbackSucceeds = true };
        var second = new FakeSecretFileWriter { CommitSucceeds = false, DiscardSucceeds = false };

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBothCore(first, "first-content", second, "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed, ex.CanonicalCode);
        Assert.True(first.RollbackAttempted);
        Assert.True(second.DiscardAttempted);
    }

    [Fact]
    public void WriteBothCore_reports_cleanup_failed_when_the_first_commit_fails_and_the_second_discard_also_fails()
    {
        var first = new FakeSecretFileWriter { CommitSucceeds = false };
        var second = new FakeSecretFileWriter { DiscardSucceeds = false };

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBothCore(first, "first-content", second, "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed, ex.CanonicalCode);
        Assert.True(second.DiscardAttempted);
    }

    [Fact]
    public void WriteBothCore_reports_cleanup_failed_when_the_second_prepare_fails_and_the_first_discard_also_fails()
    {
        var first = new FakeSecretFileWriter { DiscardSucceeds = false };
        var second = new FakeSecretFileWriter { PrepareSucceeds = false };

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBothCore(first, "first-content", second, "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed, ex.CanonicalCode);
        Assert.True(first.DiscardAttempted);
        Assert.True(second.DiscardAttempted);
    }

    [Fact]
    public void WriteBothCore_reports_cleanup_failed_when_the_first_prepare_fails_and_discard_also_fails()
    {
        var first = new FakeSecretFileWriter { PrepareSucceeds = false, DiscardSucceeds = false };
        var second = new FakeSecretFileWriter();

        var ex = Assert.Throws<SecretOperationException>(() =>
            TwoPhaseSecretWriteCoordinator.WriteBothCore(first, "first-content", second, "second-content"));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed, ex.CanonicalCode);
        Assert.True(first.DiscardAttempted);
        Assert.False(second.DiscardAttempted);
    }

    private sealed class FakeSecretFileWriter : ISecretFileWriter
    {
        public bool PrepareSucceeds { get; init; } = true;

        public bool CommitSucceeds { get; init; } = true;

        public bool RollbackSucceeds { get; init; } = true;

        public bool DiscardSucceeds { get; init; } = true;

        public bool RollbackAttempted { get; private set; }

        public bool DiscardAttempted { get; private set; }

        public void Prepare(string content)
        {
            if (!PrepareSucceeds)
            {
                throw new IOException("simulated prepare failure");
            }
        }

        public void Commit()
        {
            if (!CommitSucceeds)
            {
                throw new IOException("simulated commit failure");
            }
        }

        public bool TryDiscardPrepared()
        {
            DiscardAttempted = true;
            return DiscardSucceeds;
        }

        public bool TryRollbackCommitted()
        {
            RollbackAttempted = true;
            return RollbackSucceeds;
        }
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
