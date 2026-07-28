namespace Amane.Mailer.Setup;

/// <summary>
/// Minimal filesystem abstraction for cross-platform safety checks and focused tests.
/// </summary>
public interface ISetupFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    bool IsSymlinkOrReparsePoint(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void CreateOwnerOnlyDirectory(string path);
    void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content);
    void WriteProtectedFileCreateNew(string path, string content);
    byte[] ReadAllBytes(string path);
    void DeleteFile(string path);
    void DeleteDirectoryRecursive(string path);
    void MoveReplace(string sourcePath, string destinationPath);
    void FlushDirectory(string path);
    void SetUnixOwnership(string path, uint userId, uint groupId);
    void SetUnixFileModeOwnerOnly(string path, bool executableDirectory);
    bool TryGetUnixFileMode(string path, out UnixFileMode mode);
    uint? GetEffectiveUnixUserId();
}
