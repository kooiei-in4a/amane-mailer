using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Atomic durable write helpers: create-new tmp, flush, MoveReplace, parent flush, durable delete.
/// </summary>
/// <remarks>
/// Every artifact written here is host-private Managed state, so files are always created
/// owner-only. There is deliberately no world-readable mode.
/// </remarks>
public sealed class SetupDurableAtomicWriter
{
    private readonly ISetupFileSystem _fileSystem;

    public SetupDurableAtomicWriter(ISetupFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public SetupDockerResult TryAtomicReplaceText(
        string managedRoot,
        string destinationPath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        // The root check is a prefix comparison, so both sides must be normalized first or a
        // traversal segment would slip past it.
        if (!TryNormalizeUnderRoot(managedRoot, destinationPath, out destinationPath))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Destination path rejected.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Destination path rejected.");
        }

        if (!_fileSystem.DirectoryExists(directory))
        {
            _fileSystem.CreateOwnerOnlyDirectory(directory);
        }

        var tmpPath = destinationPath + ".tmp";
        try
        {
            if (_fileSystem.FileExists(tmpPath))
            {
                if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(tmpPath)))
                {
                    return SetupDockerResult.Fail(
                        SetupDockerResultCode.UnsafePath,
                        "Stale temporary file rejected.");
                }

                _fileSystem.DeleteFile(tmpPath);
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            try
            {
                _fileSystem.WriteProtectedFileCreateNew(tmpPath, bytes);
            }
            finally
            {
                CryptographicZero(bytes);
            }

            _fileSystem.FlushFile(tmpPath);
            _fileSystem.MoveReplace(tmpPath, destinationPath);
            _fileSystem.FlushDirectory(directory);
            return SetupDockerResult.Ok();
        }
        catch (IOException)
        {
            TryDeleteQuiet(tmpPath);
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Durable atomic replace failed.");
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteQuiet(tmpPath);
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Durable atomic replace failed.");
        }
    }

    public SetupDockerResult TryAtomicReplaceJson<T>(
        string managedRoot,
        string destinationPath,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return TryAtomicReplaceText(managedRoot, destinationPath, json);
    }

    /// <summary>
    /// Atomically claims a destination by creating that destination itself with create-new
    /// semantics, then durably flushes the file and parent directory. It never replaces an
    /// existing destination.
    /// </summary>
    public SetupDockerResult TryCreateNewJson<T>(
        string managedRoot,
        string destinationPath,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!TryNormalizeUnderRoot(managedRoot, destinationPath, out destinationPath))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Destination path rejected.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Destination path rejected.");
        }

        try
        {
            if (!_fileSystem.DirectoryExists(directory))
                _fileSystem.CreateOwnerOnlyDirectory(directory);

            var json = JsonSerializer.Serialize(value, typeInfo);
            _fileSystem.WriteProtectedFileCreateNew(destinationPath, json);
            _fileSystem.FlushFile(destinationPath);
            _fileSystem.FlushDirectory(directory);
            return SetupDockerResult.Ok();
        }
        catch (IOException)
        {
            // The destination may now exist even when a durability flush failed. Keep it as a
            // recovery claim; callers must inspect pending rather than retrying or replacing it.
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Durable create-new failed or the destination already exists.");
        }
        catch (UnauthorizedAccessException)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Durable create-new failed.");
        }
    }

    public SetupDockerResult TryDurableDelete(string managedRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!TryNormalizeUnderRoot(managedRoot, path, out path))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Delete path rejected.");
        }

        if (!_fileSystem.FileExists(path))
        {
            return SetupDockerResult.Ok();
        }

        if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(path)))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Delete path rejected.");
        }

        try
        {
            _fileSystem.DeleteFile(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && _fileSystem.DirectoryExists(directory))
            {
                _fileSystem.FlushDirectory(directory);
            }

            return SetupDockerResult.Ok();
        }
        catch (IOException)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Durable delete failed.");
        }
        catch (UnauthorizedAccessException)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Durable delete failed.");
        }
    }

    private bool TryNormalizeUnderRoot(string managedRoot, string candidate, out string normalized)
    {
        try
        {
            normalized = Path.GetFullPath(candidate);
            return SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem,
                Path.GetFullPath(managedRoot),
                normalized,
                out _,
                out _);
        }
        catch (ArgumentException)
        {
            normalized = candidate;
            return false;
        }
        catch (NotSupportedException)
        {
            normalized = candidate;
            return false;
        }
        catch (PathTooLongException)
        {
            normalized = candidate;
            return false;
        }
    }

    private void TryDeleteQuiet(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CryptographicZero(byte[] bytes)
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
    }
}
