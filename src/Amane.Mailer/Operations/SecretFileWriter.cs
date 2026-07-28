namespace Amane.Mailer.Operations;

/// <summary>
/// Atomic single-file writer: write to a temp file in the same directory as the target (same
/// filesystem, so the final rename is atomic), with owner-only permissions from creation time,
/// then rename over the target. Split into <see cref="Prepare"/> / <see cref="Commit"/> /
/// <see cref="TryDiscardPrepared"/> / <see cref="TryRollbackCommitted"/> so
/// <see cref="TwoPhaseSecretWriteCoordinator"/> can prepare two independent files before
/// committing either, and roll the first back if the second's commit fails.
/// <para>
/// <see cref="Commit"/> always overwrites the target. This is safe only because the caller
/// (<see cref="AdminProviderRegisterAcsCommand"/>) verifies via
/// <see cref="RegisteredSecretStateInspector"/> — before any secret is read from the operator and
/// before an <see cref="ExclusiveOperationLock"/> is even acquired — that the target is absent or
/// empty. A target already holding a real value causes a fail-closed rejection long before
/// <see cref="Commit"/> would ever run.
/// </para>
/// </summary>
public sealed class SecretFileWriter(string targetPath) : ISecretFileWriter
{
    public string TargetPath { get; } = targetPath;

    private string? _tempPath;

    public void Prepare(string content)
    {
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(TargetPath);
        var directory = Path.GetDirectoryName(TargetPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe,
                "Target path must include a directory.");
        }

        FileSystemSafetyGuard.EnsureDirectoryIsSafe(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(TargetPath)}.tmp-{Guid.NewGuid():N}");
        FileSystemSafetyGuard.EnsureTargetFileIsSafeIfExists(tempPath);

        // Register temp path before content write so discard can run if create-then-write fails.
        _tempPath = tempPath;
        try
        {
            // Create with owner-only permissions before writing content (Linux 0600 / Windows ACL).
            SecureFileCreate.WriteAllTextCreateNew(tempPath, content);
        }
        catch (SecureFileWriteException ex) when (ex.CreatedFileCleanupFailed)
        {
            // Incomplete temp may remain; leave _tempPath set for TryDiscardPrepared.
            throw;
        }
        catch
        {
            // SecureFileCreate removed the incomplete file, or create never succeeded.
            _tempPath = null;
            throw;
        }
    }

    public void Commit()
    {
        if (_tempPath is null)
        {
            throw new InvalidOperationException("Prepare must be called before Commit.");
        }

        File.Move(_tempPath, TargetPath, overwrite: true);
        _tempPath = null;
    }

    /// <summary>
    /// Deletes the uncommitted temp file created by <see cref="Prepare"/>, if any.
    /// Returns <see langword="false"/> if a temp file existed but the delete failed.
    /// </summary>
    public bool TryDiscardPrepared()
    {
        var tempPath = _tempPath;
        _tempPath = null;

        if (tempPath is null || !File.Exists(tempPath))
        {
            return true;
        }

        try
        {
            File.Delete(tempPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Undo a completed <see cref="Commit"/>. Returns <see langword="false"/> if delete fails.
    /// </summary>
    public bool TryRollbackCommitted()
    {
        try
        {
            if (File.Exists(TargetPath))
            {
                File.Delete(TargetPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
