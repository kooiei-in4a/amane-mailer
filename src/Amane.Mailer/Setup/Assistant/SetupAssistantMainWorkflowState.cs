using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Immutable Main setup workflow authority issued exclusively by
/// <see cref="SetupAssistantMainSetupOrchestrator"/>. Adapters may hold and return a state, but
/// cannot construct or invent prior outcomes, AppliedProof, or next-stage eligibility.
/// </summary>
internal sealed class SetupAssistantMainWorkflowState
{
    private SetupAssistantMainWorkflowState(
        SetupMode mode,
        SetupAssistantMainWorkflowStage stage,
        bool skipDockerPreflight,
        SetupAssistantMainSetupOutcome? mainSetup,
        SetupAssistantStagingOutcome? staging,
        SetupAssistantMainSetupOutcome? liveSending,
        object? appliedProof)
    {
        Mode = mode;
        Stage = stage;
        SkipDockerPreflight = skipDockerPreflight;
        MainSetup = mainSetup;
        Staging = staging;
        LiveSending = liveSending;
        AppliedProof = appliedProof;
    }

    internal SetupMode Mode { get; }

    internal SetupAssistantMainWorkflowStage Stage { get; }

    internal bool SkipDockerPreflight { get; }

    internal SetupAssistantMainSetupOutcome? MainSetup { get; }

    internal SetupAssistantStagingOutcome? Staging { get; }

    internal SetupAssistantMainSetupOutcome? LiveSending { get; }

    /// <summary>Service-issued applied proof from a successful configuration apply only.</summary>
    internal object? AppliedProof { get; }

    internal bool ConfigurationStageSucceeded =>
        MainSetup is { ConfigurationApplied: true } outcome
        && (outcome.Kind == SetupAssistantOutcomeKind.Succeeded
            || outcome.ActionCode == SetupApplyActionCode.CompleteSendReadyEvaluation);

    internal bool DeploymentSendReady =>
        LiveSending is { Kind: SetupAssistantOutcomeKind.Succeeded, DeploymentSendReady: true };

    internal bool IsComplete =>
        Stage == SetupAssistantMainWorkflowStage.Completed
        && SetupAssistantMainSetupOrchestrator.IsMainSetupCompletableForMode(
            MainSetup,
            Mode,
            Staging,
            LiveSending);

    internal bool CanRetryApply =>
        Stage == SetupAssistantMainWorkflowStage.AwaitingApply
        && MainSetup is { } outcome
        && CanRetryApplyOutcome(outcome);

    internal bool CanRetryStaging =>
        Stage == SetupAssistantMainWorkflowStage.AwaitingStagingVerification
        && Staging is
        {
            SendRequestAccepted: false,
            Kind: SetupAssistantOutcomeKind.Rejected or SetupAssistantOutcomeKind.Failed,
        };

    internal bool CanRunLiveSending =>
        Stage == SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement
        && (LiveSending is null || CanRetryApplyOutcome(LiveSending));

    internal bool CanFinish => IsComplete;

    internal static SetupAssistantMainWorkflowState CreateInitial(
        SetupMode mode,
        bool skipDockerPreflight = false) =>
        new(
            mode,
            SetupAssistantMainWorkflowStage.AwaitingApply,
            skipDockerPreflight,
            mainSetup: null,
            staging: null,
            liveSending: null,
            appliedProof: null);

    internal SetupAssistantMainWorkflowState WithSkipDockerPreflight(bool skip) =>
        new(Mode, Stage, skip, MainSetup, Staging, LiveSending, AppliedProof);

    internal static SetupAssistantMainWorkflowState FromApplyResult(
        SetupMode mode,
        bool skipDockerPreflight,
        SetupAssistantMainSetupOutcome mainSetup)
    {
        var applied = IsConfigurationStageSucceeded(mainSetup) ? mainSetup.AppliedProof : null;
        if (!IsConfigurationStageSucceeded(mainSetup))
        {
            return new(
                mode,
                SetupAssistantMainWorkflowStage.AwaitingApply,
                skipDockerPreflight,
                mainSetup,
                staging: null,
                liveSending: null,
                appliedProof: null);
        }

        return mode switch
        {
            SetupMode.StagingVerification => new(
                mode,
                SetupAssistantMainWorkflowStage.AwaitingStagingVerification,
                skipDockerPreflight,
                mainSetup,
                staging: null,
                liveSending: null,
                applied),
            SetupMode.ProductionAcs => new(
                mode,
                SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement,
                skipDockerPreflight,
                mainSetup,
                staging: null,
                liveSending: null,
                applied),
            _ => new(
                mode,
                SetupAssistantMainWorkflowStage.Completed,
                skipDockerPreflight,
                mainSetup,
                staging: null,
                liveSending: null,
                applied),
        };
    }

    internal SetupAssistantMainWorkflowState WithStaging(SetupAssistantStagingOutcome staging)
    {
        if (AppliedProof is null || !ConfigurationStageSucceeded)
        {
            throw new InvalidOperationException("Staging requires a service-issued applied proof.");
        }

        var nextStage = staging.Kind == SetupAssistantOutcomeKind.Succeeded
            ? SetupAssistantMainWorkflowStage.Completed
            : SetupAssistantMainWorkflowStage.AwaitingStagingVerification;
        return new(Mode, nextStage, SkipDockerPreflight, MainSetup, staging, LiveSending, AppliedProof);
    }

    internal SetupAssistantMainWorkflowState WithLiveSending(SetupAssistantMainSetupOutcome liveSending)
    {
        if (AppliedProof is null || !ConfigurationStageSucceeded)
        {
            throw new InvalidOperationException("Live sending requires a service-issued applied proof.");
        }

        var nextStage = liveSending is
        { Kind: SetupAssistantOutcomeKind.Succeeded, DeploymentSendReady: true }
            ? SetupAssistantMainWorkflowStage.Completed
            : SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement;
        return new(Mode, nextStage, SkipDockerPreflight, MainSetup, Staging, liveSending, AppliedProof);
    }

    internal SetupAssistantMainWorkflowState ClearedForApplyRetry()
    {
        if (!CanRetryApply)
        {
            throw new InvalidOperationException("Apply retry is not eligible.");
        }

        return new(
            Mode,
            SetupAssistantMainWorkflowStage.AwaitingApply,
            SkipDockerPreflight,
            mainSetup: null,
            staging: null,
            liveSending: null,
            appliedProof: null);
    }

    private static bool IsConfigurationStageSucceeded(SetupAssistantMainSetupOutcome? mainSetup) =>
        mainSetup is { ConfigurationApplied: true } outcome
        && (outcome.Kind == SetupAssistantOutcomeKind.Succeeded
            || outcome.ActionCode == SetupApplyActionCode.CompleteSendReadyEvaluation);

    private static bool CanRetryApplyOutcome(SetupAssistantMainSetupOutcome outcome) =>
        !outcome.ConfigurationApplied
        && !outcome.PersistentSideEffectMayRemain
        && outcome.ConfigRollbackStatus != SetupConfigRollbackStatus.Failed
        && outcome.Kind is SetupAssistantOutcomeKind.Rejected or SetupAssistantOutcomeKind.Failed
        && outcome.Code is not (SetupApplyResultCode.RecoveryRequired
            or SetupApplyResultCode.NeedsIntervention
            or SetupApplyResultCode.ApplyFailedRollbackFailed
            or AcsSetupResultCode.ConfigRollbackFailed
            or AcsSetupResultCode.ManualActionRequired)
        && outcome.ActionCode is not (SetupApplyActionCode.ManualInterventionRequired
            or SetupApplyActionCode.UnsafeVerifierResidue);
}

internal enum SetupAssistantMainWorkflowStage
{
    AwaitingApply = 0,
    AwaitingStagingVerification = 1,
    AwaitingLiveSendingEnablement = 2,
    Completed = 3,
}

/// <summary>
/// Operator-collected input for one Main workflow advance. Adapters never choose a phase; the
/// shared service maps <see cref="SetupAssistantMainWorkflowState.Stage"/> plus this input to the
/// next typed operation.
/// </summary>
internal sealed class SetupAssistantMainCollectedInput
{
    internal SetupAssistantMainSetupInput? MainSetupInput { get; init; }

    internal Guid TenantId { get; init; }

    internal string? StagingRecipientEmail { get; init; }

    internal string? StagingEnvironmentConfirmation { get; init; }

    internal string? StagingIntentConfirmation { get; init; }

    internal string? AssistantSessionId { get; init; }

    internal string? ProductionEnvironmentConfirmation { get; init; }

    internal string? LiveSendingEnableApproval { get; init; }
}

/// <summary>Result of one AdvanceAsync call: the new service-issued state plus presentation fields.</summary>
internal sealed class SetupAssistantMainWorkflowTransition
{
    internal required SetupAssistantMainWorkflowState State { get; init; }

    internal required bool Succeeded { get; init; }

    internal required string Code { get; init; }

    internal required SetupAssistantOutcomeKind Kind { get; init; }

    internal bool ConfigurationApplied { get; init; }

    internal bool DeploymentSendReady { get; init; }

    internal string? BundleId { get; init; }

    internal string? ActionCode { get; init; }

    internal SetupAssistantMainSetupFailedStep FailedStep { get; init; }

    internal bool Rejected { get; init; }

    internal string? RejectionKey { get; init; }
}
