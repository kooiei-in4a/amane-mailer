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
            var fs = new FailingCleanupFileSystem(new HostSetupFileSystem())
            {
                FailWritesAfter = 1,
                FailDeletes = true,
            };
            var core = new SetupCore(fs, bundleIdFactory: static () => "cleanup-fail");
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedCleanupFailed, result.Code);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Durability_failure_with_cleanup_failure_returns_cleanup_failed()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var fs = new FailingCleanupFileSystem(new HostSetupFileSystem())
            {
                FailFlushAfterSuccessfulWrites = true,
                FailDeletes = true,
            };
            var core = new SetupCore(fs, bundleIdFactory: static () => "durability-cleanup");
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedCleanupFailed, result.Code);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Link_inspection_failure_is_treated_as_unsafe()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-insp-" + Guid.NewGuid().ToString("N")));
        var fs = new InspectionFailedFileSystem(new HostSetupFileSystem());
        var result = new SetupCore(fs).GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
        Assert.Equal(SetupResultCode.RejectedPathUnsafe, result.Code);
    }

    private sealed class InspectionFailedFileSystem(ISetupFileSystem inner) : ISetupFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            SetupLinkInspectionResult.InspectionFailed;
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public void WriteProtectedFileCreateNew(string path, string content) =>
            inner.WriteProtectedFileCreateNew(path, content);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
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
    }

    private sealed class FailingCleanupFileSystem(ISetupFileSystem inner) : ISetupFileSystem
    {
        private int _writes;
        private int _flushes;
        public int FailWritesAfter { get; set; } = int.MaxValue;
        public bool FailDeletes { get; set; }
        public bool FailFlushAfterSuccessfulWrites { get; set; }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) => inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void MoveReplace(string sourcePath, string destinationPath) => inner.MoveReplace(sourcePath, destinationPath);
        public void SetUnixOwnership(string path, uint userId, uint groupId) => inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) => inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) => inner.IsOwnerOnlyFile(path);
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();

        public void FlushDirectory(string path)
        {
            _flushes++;
            // Fail after sealing durability flushes (2) once bundle member writes have started,
            // so writtenFiles is non-empty and cleanup runs.
            if (FailFlushAfterSuccessfulWrites && _writes > 0 && _flushes > 2)
            {
                throw new IOException("simulated durability flush failure");
            }

            inner.FlushDirectory(path);
        }

        public void FlushFile(string path) => inner.FlushFile(path);

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
