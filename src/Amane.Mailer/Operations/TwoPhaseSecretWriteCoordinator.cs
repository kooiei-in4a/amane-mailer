namespace Amane.Mailer.Operations;

/// <summary>
/// Writes two independent secret files as a best-effort transaction. POSIX filesystems have no
/// primitive for atomically renaming two unrelated files together, so this uses a
/// prepare-then-commit pattern: both temp files are staged first, then both are committed in
/// sequence. If the second commit fails after the first succeeded, the first is rolled back
/// (deleted) so the on-disk state returns to "neither registered" rather than being left with
/// only one of the two values.
/// <para>
/// The one residual gap (a crash between the second commit failing and the rollback delete
/// completing) is not recoverable in-process; <see cref="RegisteredSecretStateInspector"/>
/// catches it on the next invocation as <see cref="RegisteredSecretState.PartialOrCorrupt"/> and
/// fails closed instead of silently retrying.
/// </para>
/// </summary>
public static class TwoPhaseSecretWriteCoordinator
{
    public static void WriteBoth(
        SecretFileWriter first,
        string firstContent,
        SecretFileWriter second,
        string secondContent)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        first.Prepare(firstContent);
        try
        {
            second.Prepare(secondContent);
        }
        catch
        {
            first.DiscardPrepared();
            throw;
        }

        try
        {
            first.Commit();
        }
        catch
        {
            second.DiscardPrepared();
            throw;
        }

        try
        {
            second.Commit();
        }
        catch (Exception ex)
        {
            first.RollbackCommitted();
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack,
                "The second file failed to commit; the first was rolled back.",
                ex);
        }
    }
}
