using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup;

public sealed class HostSetupFileSystem : ISetupFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool IsSymlinkOrReparsePoint(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }

        if (File.Exists(path))
        {
            return new FileInfo(path).LinkTarget is not null;
        }

        return false;
    }

    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public void CreateOwnerOnlyDirectory(string path) => SecureFileCreate.EnsureOwnerOnlyDirectory(path);

    public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content) =>
        SecureFileCreate.WriteAllBytesCreateNew(path, content);

    public void WriteProtectedFileCreateNew(string path, string content) =>
        SecureFileCreate.WriteAllTextCreateNew(path, content);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectoryRecursive(string path) => Directory.Delete(path, recursive: true);

    public void MoveReplace(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: true);
}
