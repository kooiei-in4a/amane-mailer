using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupPathAndCleanupTests
{
    [Fact]
    public void Relative_managed_root_is_rejected()
    {
        var request = SetupTestFixtures.LocalMailpitRequest("relative-root");
        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedPathUnsafe, result.Code);
    }

    [Fact]
    public void Mode_5_is_not_accepted_by_parser()
    {
        Assert.False(SetupModeParser.TryParse("production-queue", out _));
    }

    [Fact]
    public void Cleanup_failure_uses_distinct_result_code()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var fs = new FailingCleanupFileSystem(new HostSetupFileSystem());
            var core = new SetupCore(fs, bundleIdFactory: static () => "cleanup-fail");
            // Force failure after first write by marking write to fail on tenants.json second path via
            // FailAfterCount — write tenants then fail on compose.env, then fail cleanup.
            fs.FailWritesAfter = 1;
            fs.FailDeletes = true;
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedCleanupFailed, result.Code);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class FailingCleanupFileSystem(ISetupFileSystem inner) : ISetupFileSystem
    {
        private int _writes;
        public int FailWritesAfter { get; set; } = int.MaxValue;
        public bool FailDeletes { get; set; }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool IsSymlinkOrReparsePoint(string path) => inner.IsSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void MoveReplace(string sourcePath, string destinationPath) => inner.MoveReplace(sourcePath, destinationPath);
        public void DeleteDirectoryRecursive(string path)
        {
            if (FailDeletes)
            {
                throw new IOException("simulated cleanup failure");
            }

            inner.DeleteDirectoryRecursive(path);
        }

        public void DeleteFile(string path)
        {
            if (FailDeletes)
            {
                throw new IOException("simulated cleanup failure");
            }

            inner.DeleteFile(path);
        }

        public void WriteProtectedFileCreateNew(string path, string content)
        {
            if (_writes++ >= FailWritesAfter)
            {
                throw new IOException("simulated write failure");
            }

            inner.WriteProtectedFileCreateNew(path, content);
        }

        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content)
        {
            if (_writes++ >= FailWritesAfter)
            {
                throw new IOException("simulated write failure");
            }

            inner.WriteProtectedFileCreateNew(path, content);
        }
    }
}
