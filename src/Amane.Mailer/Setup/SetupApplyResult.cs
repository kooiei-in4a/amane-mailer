namespace Amane.Mailer.Setup;

/// <summary>
/// Durable Managed deployment state reconstructed from on-disk markers.
/// </summary>
public enum SetupManagedDeploymentState
{
    NoManaged = 0,
    Active = 1,
    TransactionInProgress = 2,
    RecoveryRequired = 3,
    NeedsIntervention = 4,

    /// <summary>
    /// Durable state was never read because the operation stopped before APPLY.lock was held.
    /// Reported instead of guessing a state that was not observed.
    /// </summary>
    NotInspected = 5,
}

/// <summary>
/// Canonical apply/rollback/recovery outcome codes (Issue #450).
/// </summary>
public static class SetupApplyResultCode
{
    public const string ApplySucceeded = "setup.apply.apply_succeeded";
    public const string FreshApplyFailed = "setup.apply.fresh_apply_failed";
    public const string ApplyFailedRollbackSucceeded = "setup.apply.apply_failed_rollback_succeeded";
    public const string ApplyFailedRollbackFailed = "setup.apply.apply_failed_rollback_failed";
    public const string RollbackSucceeded = "setup.apply.rollback_succeeded";
    public const string CancelledBeforeActivation = "setup.apply.cancelled_before_activation";
    public const string UpgradeRequired = "setup.apply.upgrade_required";
    public const string ConcurrentApplyRejected = "setup.apply.concurrent_apply_rejected";
    public const string RecoveryRequired = "setup.apply.recovery_required";
    public const string NeedsIntervention = "setup.apply.needs_intervention";
    public const string IneligibleExistingActive = "setup.apply.ineligible_existing_active";

    /// <summary>Docker preflight or lock acquisition failed before durable state was inspected.</summary>
    public const string PreflightFailed = "setup.apply.preflight_failed";
    public const string FailedUnexpected = "setup.apply.failed_unexpected";
}

public static class SetupApplyActionCode
{
    public const string CompleteSendReadyEvaluation = "complete_send_ready_evaluation";
    public const string ReviewDatabaseSchema = "review_database_schema";
    public const string ReviewDatabaseFiles = "review_database_files";
    public const string ManualInterventionRequired = "manual_intervention_required";
    public const string UnsafeVerifierResidue = "unsafe_verifier_residue";
}

public static class SetupConfigRollbackStatus
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string NotApplicable = "not-applicable";
}

/// <summary>
/// Public apply result. Never carries secrets, private paths, raw process output, or HMAC material.
/// </summary>
public sealed class SetupApplyResult
{
    public required string Code { get; init; }
    public required SetupManagedDeploymentState DeploymentState { get; init; }
    public bool ConfigurationApplied { get; init; }
    public bool VerificationCommitted { get; init; }
    public bool SendReadyAsserted { get; init; }
    public string SendReadyEvaluation { get; init; } = "not-evaluated";
    public string? SendReadyReasonCode { get; init; }
    public string? ActionCode { get; init; }
    public string? Message { get; init; }
    public string? ReasonCode { get; init; }
    public string? BundleId { get; init; }
    public long? ActivationGeneration { get; init; }
    public string ConfigRollbackStatus { get; init; } = SetupConfigRollbackStatus.NotApplicable;
    public bool PersistentSideEffectMayRemain { get; init; }
    public string PersistentSideEffectKind { get; init; } = SetupPersistentSideEffectKind.None;

    public bool IsSuccess => Code == SetupApplyResultCode.ApplySucceeded
        || Code == SetupApplyResultCode.RollbackSucceeded;

    public static SetupApplyResult Create(
        string code,
        SetupManagedDeploymentState deploymentState,
        string? message = null,
        string? actionCode = null,
        string? reasonCode = null,
        string? bundleId = null,
        long? activationGeneration = null,
        bool configurationApplied = false,
        bool verificationCommitted = false,
        string configRollbackStatus = SetupConfigRollbackStatus.NotApplicable,
        bool persistentSideEffectMayRemain = false,
        string persistentSideEffectKind = SetupPersistentSideEffectKind.None) =>
        new()
        {
            Code = code,
            DeploymentState = deploymentState,
            Message = message,
            ActionCode = actionCode,
            ReasonCode = reasonCode,
            BundleId = bundleId,
            ActivationGeneration = activationGeneration,
            ConfigurationApplied = configurationApplied,
            VerificationCommitted = verificationCommitted,
            SendReadyAsserted = false,
            SendReadyEvaluation = "not-evaluated",
            SendReadyReasonCode = "doctor-operation-not-available",
            ConfigRollbackStatus = configRollbackStatus,
            PersistentSideEffectMayRemain = persistentSideEffectMayRemain,
            PersistentSideEffectKind = persistentSideEffectKind,
        };
}
