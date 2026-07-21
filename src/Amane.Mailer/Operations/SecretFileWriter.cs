namespace Amane.Mailer.Operations;

/// <summary>
/// Atomic single-file writer: write to a temp file in the same directory as the target (same
/// filesystem, so the final rename is atomic), set restrictive permissions, then rename over the
/// target. Split into <see cref="Prepare"/> / <see cref="Commit"/> / <see cref="DiscardPrepared"/>
/// / <see cref="RollbackCommitted"/> so <see cref="TwoPhaseSecretWriteCoordinator"/> can prepare
/// two independent files before committing either, and roll the first back if the second's
/// commit fails.
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

        File.WriteAllText(tempPath, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _tempPath = tempPath;
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

    public void DiscardPrepared()
    {
        if (_tempPath is not null && File.Exists(_tempPath))
        {
            try
            {
                File.Delete(_tempPath);
            }
            catch
            {
                // Best effort cleanup of an uncommitted temp file; not itself the final secret.
            }
        }

        _tempPath = null;
    }

    /// <summary>
    /// Undo a completed <see cref="Commit"/>. Only meaningful immediately after a successful
    /// commit, used when a sibling write in the same two-phase operation subsequently fails.
    /// Deleting is safe because <see cref="Commit"/> is only reachable once preflight already
    /// confirmed the pre-commit state was absent/empty.
    /// <para>
    /// Returns <see langword="false"/> if the delete itself fails. The caller must not claim the
    /// operation was rolled back in that case — <see cref="TwoPhaseSecretWriteCoordinator"/> maps
    /// a failed rollback to a distinct canonical code
    /// (<see cref="AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed"/>) rather than
    /// <see cref="AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack"/>, since the
    /// latter implies the on-disk state is clean again when it may not be.
    /// </para>
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
