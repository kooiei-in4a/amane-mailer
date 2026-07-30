namespace Amane.Mailer.Setup;

/// <summary>
/// Internal companion surface for workflows that must hold APPLY.lock across apply and
/// post-activation verification. The public <see cref="ISetupApplyEngine"/> remains unchanged.
/// </summary>
internal interface ISetupVerifiedWorkflowApplyEngine
{
    Task<SetupVerifiedWorkflowLeaseResult> AcquireVerifiedWorkflowLeaseAsync(
        TrustedSetupHostLayout layout,
        SourceAdminDisposition sourceDisposition,
        CancellationToken cancellationToken);

    Task<SetupApplyResult> RecoverAdminBootstrapRollbackAsync(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument pending,
        CancellationToken cancellationToken);

    Task<SetupAuthorityCheckResult> VerifyPendingCandidateAsync(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument pending,
        CancellationToken cancellationToken);
}

internal interface ISetupVerifiedWorkflowLease : IAsyncDisposable
{
    TrustedVerifiedActiveBundle Source { get; }

    Task<SetupApplyResult> ApplyCandidateAsync(
        string candidateBundleId,
        AdminBootstrapOwnershipDocument pending,
        CancellationToken cancellationToken);

    Task<SetupApplyResult> RollbackToSourceAsync(string reasonCode);

    Task<SetupAuthorityCheckResult> VerifyCandidateStillCurrentAsync(
        CancellationToken cancellationToken);
}

internal sealed class SetupVerifiedWorkflowLeaseResult
{
    internal required SetupApplyResult Result { get; init; }
    internal ISetupVerifiedWorkflowLease? Lease { get; init; }
    internal bool IsSuccess => Lease is not null;
}

internal readonly record struct SetupAuthorityCheckResult(bool IsCurrent, string? ReasonCode)
{
    internal static SetupAuthorityCheckResult Current() => new(true, null);
    internal static SetupAuthorityCheckResult Failed(string reasonCode) => new(false, reasonCode);
}
