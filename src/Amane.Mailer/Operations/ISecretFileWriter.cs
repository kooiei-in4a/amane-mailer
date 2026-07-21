namespace Amane.Mailer.Operations;

/// <summary>
/// Internal seam implemented by <see cref="SecretFileWriter"/>. Exists solely so
/// <see cref="TwoPhaseSecretWriteCoordinator"/>'s rollback-failure and cleanup-failure branches
/// (distinguishing <see cref="AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack"/>,
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed"/>, and
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedCleanupFailed"/>) can be exercised
/// deterministically in tests via a fake, since forcing a real rollback delete or temp-file
/// discard to fail at exactly the right moment through the real filesystem is not reliably
/// reproducible. The public <see cref="TwoPhaseSecretWriteCoordinator.WriteBoth"/> overload only
/// ever receives real <see cref="SecretFileWriter"/> instances.
/// </summary>
internal interface ISecretFileWriter
{
    void Prepare(string content);

    void Commit();

    bool TryDiscardPrepared();

    bool TryRollbackCommitted();
}
