namespace Amane.Mailer.Operations;

/// <summary>
/// Writes two independent secret files as a best-effort transaction. POSIX filesystems have no
/// primitive for atomically renaming two unrelated files together, so this uses a
/// prepare-then-commit pattern: both temp files are staged first, then both are committed in
/// sequence. If the second commit fails after the first succeeded, the first is rolled back
/// (deleted) so the on-disk state returns to "neither registered" rather than being left with
/// only one of the two values. Every failure path also discards whichever temp file(s) are still
/// pending, so no <c>.tmp-*</c> file is ever left behind regardless of which step failed.
/// <para>
/// The one residual gap (a crash between the second commit failing and the rollback delete
/// completing) is not recoverable in-process; <see cref="RegisteredSecretStateInspector"/>
/// catches it on the next invocation as <see cref="RegisteredSecretState.PartialOrCorrupt"/> and
/// fails closed instead of silently retrying. If the rollback delete itself fails synchronously
/// here (not a crash, an observed exception), that is reported as
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed"/> rather than
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack"/> — the latter
/// would incorrectly claim the on-disk state is clean again. Likewise, if discarding an
/// uncommitted temp file fails, that is reported as
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed"/> rather than silently
/// re-throwing only the original triggering failure — an operator needs to know a
/// secret-bearing temp file may still be on disk.
/// </para>
/// </summary>
public static class TwoPhaseSecretWriteCoordinator
{
    public static void WriteBoth(
        SecretFileWriter first,
        string firstContent,
        SecretFileWriter second,
        string secondContent) =>
        WriteBothCore(first, firstContent, second, secondContent);

    /// <summary>
    /// Same logic as <see cref="WriteBoth"/>, expressed over <see cref="ISecretFileWriter"/> so
    /// tests can substitute a fake that forces the rollback/cleanup branches deterministically —
    /// see <see cref="ISecretFileWriter"/> for why the real filesystem can't reliably do that.
    /// </summary>
    internal static void WriteBothCore(
        ISecretFileWriter first,
        string firstContent,
        ISecretFileWriter second,
        string secondContent)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        first.Prepare(firstContent);
        try
        {
            second.Prepare(secondContent);
        }
        catch (Exception ex)
        {
            if (!first.TryDiscardPrepared())
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed,
                    "The second file failed to prepare and cleaning up the first file's temp " +
                    "file also failed. A temp file may still be present on disk. Manual review " +
                    "is required.",
                    ex);
            }

            throw;
        }

        try
        {
            first.Commit();
        }
        catch (Exception ex)
        {
            if (!second.TryDiscardPrepared())
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed,
                    "The first file failed to commit and cleaning up the second file's temp " +
                    "file also failed. A temp file may still be present on disk. Manual review " +
                    "is required.",
                    ex);
            }

            throw;
        }

        try
        {
            second.Commit();
        }
        catch (Exception ex)
        {
            var rolledBack = first.TryRollbackCommitted();
            var discarded = second.TryDiscardPrepared();

            if (!rolledBack)
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed,
                    "The second file failed to commit and rolling back the first also failed. " +
                    "The first file's value may still be present on disk. Manual review is required.",
                    ex);
            }

            if (!discarded)
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed,
                    "The second file failed to commit; the first was rolled back, but cleaning " +
                    "up the second file's temp file also failed. A temp file may still be " +
                    "present on disk. Manual review is required.",
                    ex);
            }

            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack,
                "The second file failed to commit; the first was rolled back.",
                ex);
        }
    }
}
