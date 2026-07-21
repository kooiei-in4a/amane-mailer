namespace Amane.Mailer.Operations;

/// <summary>
/// Internal seam implemented by <see cref="SecretFileWriter"/>. Exists solely so
/// <see cref="TwoPhaseSecretWriteCoordinator"/>'s rollback-failure branch (distinguishing
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedPartialWriteRolledBack"/> from
/// <see cref="AdminProviderRegisterAcsResultCodes.RejectedRollbackFailed"/>) can be exercised
/// deterministically in tests via a fake, since forcing a real rollback delete to fail at exactly
/// the right moment through the real filesystem (after the first commit but before the second's
/// failure) is not reliably reproducible. The public <see cref="TwoPhaseSecretWriteCoordinator.WriteBoth"/>
/// overload only ever receives real <see cref="SecretFileWriter"/> instances.
/// </summary>
internal interface ISecretFileWriter
{
    void Prepare(string content);

    void Commit();

    void DiscardPrepared();

    bool TryRollbackCommitted();
}
