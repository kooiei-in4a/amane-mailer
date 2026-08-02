using System.Text;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupRound7ReviewTests
{
    [Fact]
    public void Generation_lock_symlink_is_rejected_as_path_unsafe_on_windows_or_unix()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        var outside = Path.Combine(Path.GetTempPath(), "amane-r7-lock-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(outside, "outside\n", Encoding.UTF8);
            var lockPath = Path.Combine(root, SetupGenerationLock.LockFileName);
            try
            {
                File.CreateSymbolicLink(lockPath, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("Symlink creation is not permitted in this test environment.");
                return;
            }

            var result = new SetupCore().GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedPathUnsafe, result.Code);
        }
        finally
        {
            TryDelete(root);
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void Held_generation_lock_maps_to_concurrent_execution()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            using var held = SetupGenerationLock.Acquire(new HostSetupFileSystem(), root);
            var result = new SetupCore().GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedConcurrentExecution, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Sealing_key_is_not_read_before_generation_lock()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            // Establish a sealing key via a normal successful generation.
            var first = new SetupCore(bundleIdFactory: static () => "r7-seal-first")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, first.Code);

            var fs = new SealingReadOrderFileSystem(new HostSetupFileSystem());
            var second = new SetupCore(fs, bundleIdFactory: static () => "r7-seal-second")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, second.Code);
            Assert.True(fs.LockAcquiredBeforeSealingRead);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class SealingReadOrderFileSystem(ISetupFileSystem inner) : ISetupFileSystem
    {
        private bool _lockAcquired;
        public bool LockAcquiredBeforeSealingRead { get; private set; } = true;

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void WriteProtectedFileCreateNew(string path, string content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectoryRecursive(string path) => inner.DeleteDirectoryRecursive(path);
        public void MoveReplace(string sourcePath, string destinationPath) =>
            inner.MoveReplace(sourcePath, destinationPath);
        public void FlushDirectory(string path) => inner.FlushDirectory(path);
        public void FlushFile(string path) => inner.FlushFile(path);
        public void SetUnixOwnership(string path, uint userId, uint groupId) =>
            inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) =>
            inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) => inner.IsOwnerOnlyFile(path);
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();

        public FileStream OpenExclusiveGenerationLock(string path)
        {
            var stream = inner.OpenExclusiveGenerationLock(path);
            _lockAcquired = true;
            return stream;
        }

        public byte[] ReadAllBytes(string path)
        {
            if (path.Contains(SetupBundleLayout.HostSealingKeyFileName, StringComparison.Ordinal)
                && !_lockAcquired)
            {
                LockAcquiredBeforeSealingRead = false;
                throw new IOException("sealing key read before generation lock");
            }

            return inner.ReadAllBytes(path);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
