using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup;

/// <summary>
/// Public ACS Easy Setup workflow result for Web / terminal / TTY adapters.
/// Never carries secrets, sender/recipient plaintext, provider raw errors, or release qualification evidence.
/// </summary>
public sealed class AcsSetupWorkflowResult
{
    public required string Code { get; init; }
    public required AcsSetupWorkflowState State { get; init; }
    public bool ConfigurationApplied { get; init; }
    public bool DeploymentSendReady { get; init; }
    public string? BundleId { get; init; }
    public string? ConfigurationFingerprint { get; init; }
    public string? ApplyResultCode { get; init; }
    public string? ConfigRollbackStatus { get; init; }
    public bool PersistentSideEffectMayRemain { get; init; }
    public string? PersistentSideEffectKind { get; init; }
    public string? ActionCode { get; init; }
    public string? Message { get; init; }
    public string? StagingVerificationCode { get; init; }
    public string? StagingMailboxCheckStatus { get; init; }
    public bool StagingSendRequestAccepted { get; init; }
    public bool StagingOperationCompleted { get; init; }
    public string? MaskedSenderEmail { get; init; }
    public string? MaskedRecipientEmail { get; init; }

    /// <summary>
    /// Always false: this workflow never records deployment operational verification or release qualification.
    /// </summary>
    public bool OperationalVerificationRecorded => false;

    public bool IsSuccess =>
        Code is AcsSetupResultCode.ConfigurationApplied
            or AcsSetupResultCode.StagingVerificationSucceeded
            or AcsSetupResultCode.DeploymentSendReady;

    public static AcsSetupWorkflowResult FromStaging(AcsStagingVerificationResult staging) =>
        new()
        {
            Code = staging.IsSuccess
                ? AcsSetupResultCode.StagingVerificationSucceeded
                : AcsSetupResultCode.StagingVerificationFailed,
            State = staging.IsSuccess
                ? AcsSetupWorkflowState.StagingVerificationSucceeded
                : AcsSetupWorkflowState.StagingVerificationFailed,
            StagingVerificationCode = staging.Code,
            StagingMailboxCheckStatus = staging.MailboxCheckStatus,
            StagingSendRequestAccepted = staging.SendRequestAccepted,
            StagingOperationCompleted = staging.OperationCompleted,
            MaskedSenderEmail = staging.MaskedSenderEmail,
            MaskedRecipientEmail = staging.MaskedRecipientEmail,
            Message = staging.IsSuccess
                ? "Staging verification completed; mailbox arrival requires manual ACTION."
                : "Staging verification failed.",
        };
}
