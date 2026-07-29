namespace Amane.Mailer.Setup;

/// <summary>
/// ACS Easy Setup workflow states (#451). Persistent Managed markers stay on #450 types;
/// this enum is the Assistant / typed-operation view and may include session-only phases.
/// </summary>
public enum AcsSetupWorkflowState
{
    NotStarted = 0,
    AcsConfigurationPrepared = 1,
    ConfigurationApplying = 2,
    ConfigurationApplied = 3,
    StagingVerificationPending = 4,
    StagingVerificationSucceeded = 5,
    StagingVerificationFailed = 6,
    ProductionConfirmationPending = 7,
    LiveSendingBundlePrepared = 8,
    LiveSendingApplying = 9,
    DeploymentSendReady = 10,
    ApplyFailed = 11,
    RollbackSucceeded = 12,
    RollbackFailed = 13,
    ExternalSideEffectMayRemain = 14,
    NeedsIntervention = 15,
    BundleGenerationFailed = 16,
    ProductionConfirmationRejected = 17,
}

/// <summary>Canonical ACS workflow result codes. Secrets and PII must never appear in messages.</summary>
public static class AcsSetupResultCode
{
    public const string BundleGenerationFailed = "acs.setup.bundle_generation_failed";
    public const string ConfigurationApplyFailed = "acs.setup.configuration_apply_failed";
    public const string ConfigurationApplied = "acs.setup.configuration_applied";
    public const string StagingVerificationFailed = "acs.setup.staging_verification_failed";
    public const string StagingVerificationSucceeded = "acs.setup.staging_verification_succeeded";
    public const string ProductionConfirmationRejected = "acs.setup.production_confirmation_rejected";
    public const string LiveSendingEnableApplyFailed = "acs.setup.live_sending_enable_apply_failed";
    public const string DeploymentSendReady = "acs.setup.deployment_send_ready";
    public const string ConfigRollbackSucceeded = "acs.setup.config_rollback_succeeded";
    public const string ConfigRollbackFailed = "acs.setup.config_rollback_failed";
    public const string ExternalSideEffectMayRemain = "acs.setup.external_side_effect_may_remain";
    public const string ManualActionRequired = "acs.setup.manual_action_required";
    public const string RejectedLiveSendingWithoutConfirmation = "acs.setup.rejected_live_sending_without_confirmation";
    public const string RejectedInvalidMode = "acs.setup.rejected_invalid_mode";
    public const string FailedUnexpected = "acs.setup.failed_unexpected";
}

/// <summary>
/// Explicit approval phrase for enabling tenant live_sending after exact Production confirmation.
/// </summary>
public static class AcsLiveSendingApproval
{
    public const string EnablePhrase = "MAILER-ENABLE-LIVE-SENDING";
}
