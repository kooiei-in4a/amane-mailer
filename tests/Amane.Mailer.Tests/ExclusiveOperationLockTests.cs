using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests;

public sealed class ExclusiveOperationLockTests
{
    [Fact]
    public void Acquire_succeeds_on_a_clean_directory()
    {
        using var scratch = new ScratchDirectory();

        using var acquired = ExclusiveOperationLock.Acquire(scratch.Path);

        Assert.True(File.Exists(Path.Combine(scratch.Path, ExclusiveOperationLock.LockFileName)));
    }

    [Fact]
    public void Concurrent_acquire_is_rejected_while_the_first_lock_is_held()
    {
        using var scratch = new ScratchDirectory();
        using var first = ExclusiveOperationLock.Acquire(scratch.Path);

        var ex = Assert.Throws<SecretOperationException>(() => ExclusiveOperationLock.Acquire(scratch.Path));

        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedConcurrentExecution, ex.CanonicalCode);
    }

    [Fact]
    public void Acquire_succeeds_again_after_the_first_lock_is_released()
    {
        using var scratch = new ScratchDirectory();
        var first = ExclusiveOperationLock.Acquire(scratch.Path);
        first.Dispose();

        using var second = ExclusiveOperationLock.Acquire(scratch.Path);
    }

    [Fact]
    public void Acquire_succeeds_when_a_stale_unlocked_lock_file_is_left_behind_by_an_abnormal_exit()
    {
        // Simulates crash recovery: a lock file exists on disk (e.g. left over from a killed
        // process) but nothing currently holds the underlying OS-level advisory lock, because
        // that lock is tied to the process's open file description and is released automatically
        // when the process dies — never held open across process death. A fresh acquire must not
        // treat the file's mere existence as "still locked".
        using var scratch = new ScratchDirectory();
        File.WriteAllText(Path.Combine(scratch.Path, ExclusiveOperationLock.LockFileName), string.Empty);

        using var acquired = ExclusiveOperationLock.Acquire(scratch.Path);
    }

    [Fact]
    public void Acquire_rejects_a_symlinked_lock_file()
    {
        using var scratch = new ScratchDirectory();
        var realPath = Path.Combine(scratch.Path, "real-lock-target");
        File.WriteAllText(realPath, string.Empty);
        var lockPath = Path.Combine(scratch.Path, ExclusiveOperationLock.LockFileName);

        try
        {
            File.CreateSymbolicLink(lockPath, realPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symlink creation is not permitted in this test environment.");
            return;
        }

        var thrown = Assert.Throws<SecretOperationException>(() => ExclusiveOperationLock.Acquire(scratch.Path));
        Assert.Equal(AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe, thrown.CanonicalCode);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "amane-mailer-lock-" + Guid.NewGuid().ToString("N"));

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
