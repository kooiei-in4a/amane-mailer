namespace Amane.Mailer.Setup;

/// <summary>
/// Minimal filesystem abstraction for cross-platform safety checks and focused tests.
/// </summary>
public interface ISetupFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void CreateOwnerOnlyDirectory(string path);
    void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content);
    void WriteProtectedFileCreateNew(string path, string content);
    byte[] ReadAllBytes(string path);
    void DeleteFile(string path);
    void DeleteDirectoryRecursive(string path);
    void MoveReplace(string sourcePath, string destinationPath);
    void FlushDirectory(string path);
    void FlushFile(string path);

    /// <summary>Open or create an owner-only exclusive lock file without following symlinks.</summary>
    FileStream OpenExclusiveGenerationLock(string path);
    void SetUnixOwnership(string path, uint userId, uint groupId);
    void SetUnixFileModeOwnerOnly(string path, bool executableDirectory);
    bool TryGetUnixFileMode(string path, out UnixFileMode mode);

    /// <summary>
    /// Returns <c>true</c> only when the file is verifiably owner-only.
    /// Returns <c>false</c> when permissions are weak or when inspection fails (fail-closed).
    /// </summary>
    bool IsOwnerOnlyFile(string path);

    uint? GetEffectiveUnixUserId();

    /// <summary>
    /// Effective Unix group id, or <c>null</c> on platforms without one. Callers that build a
    /// <see cref="SetupRuntimeFileOwnership"/> need it alongside the user id; implementations
    /// that do not model Unix ownership can keep the default.
    /// </summary>
    uint? GetEffectiveUnixGroupId() => null;
}
