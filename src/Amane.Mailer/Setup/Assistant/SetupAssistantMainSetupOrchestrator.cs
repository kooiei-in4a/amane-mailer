using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Shared Main setup application service. Owns operation ordering, AppliedProof continuity,
/// completion gates, and retry eligibility. Adapters submit only a service-issued
/// <see cref="ISetupAssistantMainWorkflowState"/> plus newly collected operator input.
/// </summary>
internal static class SetupAssistantMainSetupOrchestrator
{
    /// <summary>Issues the only legal initial Main workflow handle for the given mode.</summary>
    internal static ISetupAssistantMainWorkflowState CreateInitial(SetupMode mode) =>
        State.CreateInitial(mode);

    /// <summary>
    /// Records a successful Docker preflight onto a service-issued state. Adapters cannot invent a
    /// skip flag; only a Passed outcome from the typed Docker check is accepted.
    /// </summary>
    internal static ISetupAssistantMainWorkflowState AcknowledgeDockerPreflight(
        ISetupAssistantMainWorkflowState state,
        SetupAssistantDockerPreflightOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        if (state is not State issued)
        {
            throw new InvalidOperationException("Main workflow state must be service-issued.");
        }

        return outcome.Passed ? issued.WithSkipDockerPreflight(true) : issued;
    }

    /// <summary>
    /// Clears a failed Staging outcome for retry while preserving the service-issued AppliedProof.
    /// Adapters must not reconstruct Apply-succeeded state from raw outcomes.
    /// </summary>
    internal static ISetupAssistantMainWorkflowState PrepareStagingRetry(
        ISetupAssistantMainWorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is not State issued || !issued.CanRetryStaging)
        {
            throw new InvalidOperationException("Staging retry is not eligible.");
        }

        return issued.ClearedForStagingRetry();
    }

    internal static async Task<SetupAssistantMainWorkflowTransition> AdvanceAsync(
        ISetupAssistantOperations operations,
        ISetupAssistantMainWorkflowState state,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);

        if (state is not State issued)
        {
            return Rejected(state, SetupAssistantRejection.StepNotAvailable);
        }

        return issued.Stage switch
        {
            SetupAssistantMainWorkflowStage.AwaitingApply =>
                await AdvanceApplyAsync(operations, issued, input, cancellationToken),
            SetupAssistantMainWorkflowStage.AwaitingStagingVerification =>
                await AdvanceStagingAsync(operations, issued, input, cancellationToken),
            SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement =>
                await AdvanceLiveSendingAsync(operations, issued, input, cancellationToken),
            SetupAssistantMainWorkflowStage.Completed =>
                Rejected(issued, SetupAssistantRejection.StepNotAvailable),
            _ => throw new ArgumentOutOfRangeException(nameof(state), issued.Stage, null),
        };
    }

    /// <summary>
    /// Terminal and non-interactive driver: apply then mode-specific follow-up using one collected
    /// input set. Retry loops remain in the adapter; each retry calls AdvanceAsync or this method
    /// again with a fresh or cleared service-issued state.
    /// </summary>
    internal static async Task<SetupAssistantMainWorkflowTransition> RunToCompletionAsync(
        ISetupAssistantOperations operations,
        ISetupAssistantMainWorkflowState initialState,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        if (initialState is not State issued
            || issued.Stage != SetupAssistantMainWorkflowStage.AwaitingApply
            || issued.MainSetup is not null)
        {
            return Rejected(initialState, SetupAssistantRejection.StepNotAvailable);
        }

        var apply = await AdvanceAsync(operations, issued, input, cancellationToken);
        if (!apply.State.ConfigurationStageSucceeded
            || apply.State.Stage == SetupAssistantMainWorkflowStage.Completed)
        {
            return apply;
        }

        // Follow-up stages reuse the same collected input; the service decides which operation
        // runs from State.Stage and ignores fields that are not required for that stage.
        return await AdvanceAsync(operations, apply.State, input, cancellationToken);
    }

    private static async Task<SetupAssistantMainWorkflowTransition> AdvanceApplyAsync(
        ISetupAssistantOperations operations,
        State state,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        if (state.MainSetup is not null && !state.CanRetryApply)
        {
            return Rejected(state, SetupAssistantRejection.StepNotAvailable);
        }

        if (input.MainSetupInput is not { } mainSetupInput)
        {
            return Rejected(state, SetupAssistantRejection.MissingRequiredField);
        }

        if (mainSetupInput.Mode != state.Mode)
        {
            return Rejected(state, SetupAssistantRejection.StepNotAvailable);
        }

        var working = state.MainSetup is not null ? state.ClearedForApplyRetry() : state;

        var preflight = await RunDockerPreflightIfNeededAsync(
            operations,
            working.SkipDockerPreflight,
            cancellationToken);
        if (preflight is not null)
        {
            var failedMain = new SetupAssistantMainSetupOutcome
            {
                Code = preflight.Code,
                Kind = SetupAssistantOutcomeKind.Failed,
                ConfigurationApplied = false,
            };
            var failed = State.FromApplyResult(
                working.Mode,
                working.SkipDockerPreflight,
                failedMain);
            return TransitionFromState(
                failed,
                succeeded: false,
                code: preflight.Code,
                kind: SetupAssistantOutcomeKind.Failed,
                failedStep: SetupAssistantMainSetupFailedStep.DockerPreflight);
        }

        var mainSetup = await operations.ApplyMainSetupAsync(mainSetupInput, cancellationToken);
        var next = State.FromApplyResult(
            working.Mode,
            working.SkipDockerPreflight,
            mainSetup);
        return TransitionFromState(
            next,
            succeeded: next.IsComplete || next.ConfigurationStageSucceeded,
            code: mainSetup.Code,
            kind: mainSetup.Kind,
            configurationApplied: mainSetup.ConfigurationApplied,
            deploymentSendReady: next.DeploymentSendReady,
            bundleId: mainSetup.BundleId,
            actionCode: mainSetup.ActionCode,
            failedStep: next.ConfigurationStageSucceeded
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.MainApply);
    }

    private static async Task<SetupAssistantMainWorkflowTransition> AdvanceStagingAsync(
        ISetupAssistantOperations operations,
        State state,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        if (state.AppliedProof is null
            || !state.ConfigurationStageSucceeded
            || (state.Staging is not null && !state.CanRetryStaging))
        {
            return Rejected(state, SetupAssistantRejection.StepNotAvailable);
        }

        if (string.IsNullOrEmpty(input.StagingRecipientEmail)
            || string.IsNullOrEmpty(input.StagingEnvironmentConfirmation)
            || string.IsNullOrEmpty(input.StagingIntentConfirmation)
            || string.IsNullOrEmpty(input.AssistantSessionId))
        {
            return Rejected(state, SetupAssistantRejection.MissingRequiredField);
        }

        var staging = await operations.VerifyStagingAsync(
            new SetupAssistantStagingInput
            {
                TenantId = input.TenantId,
                RecipientEmail = input.StagingRecipientEmail,
                EnvironmentConfirmation = input.StagingEnvironmentConfirmation,
                IntentConfirmation = input.StagingIntentConfirmation,
                AssistantSessionId = input.AssistantSessionId,
                AppliedProof = state.AppliedProof,
            },
            cancellationToken);

        var next = state.WithStaging(staging);
        return TransitionFromState(
            next,
            succeeded: next.IsComplete,
            code: staging.Code,
            kind: staging.Kind,
            configurationApplied: next.ConfigurationStageSucceeded,
            failedStep: staging.Kind == SetupAssistantOutcomeKind.Succeeded
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.StagingVerification);
    }

    private static async Task<SetupAssistantMainWorkflowTransition> AdvanceLiveSendingAsync(
        ISetupAssistantOperations operations,
        State state,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        if (state.AppliedProof is null
            || !state.ConfigurationStageSucceeded
            || !state.CanRunLiveSending)
        {
            return Rejected(state, SetupAssistantRejection.StepNotAvailable);
        }

        if (string.IsNullOrEmpty(input.ProductionEnvironmentConfirmation)
            || string.IsNullOrEmpty(input.LiveSendingEnableApproval))
        {
            return Rejected(state, SetupAssistantRejection.MissingRequiredField);
        }

        var liveSending = await operations.EnableLiveSendingAsync(
            new SetupAssistantProductionInput
            {
                EnvironmentConfirmation = input.ProductionEnvironmentConfirmation,
                LiveSendingEnableApproval = input.LiveSendingEnableApproval,
                AppliedProof = state.AppliedProof,
            },
            cancellationToken);

        var next = state.WithLiveSending(liveSending);
        return TransitionFromState(
            next,
            succeeded: next.IsComplete,
            code: liveSending.Code,
            kind: liveSending.Kind,
            configurationApplied: liveSending.ConfigurationApplied,
            deploymentSendReady: next.DeploymentSendReady,
            bundleId: liveSending.BundleId,
            actionCode: liveSending.ActionCode,
            failedStep: next.IsComplete
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.LiveSendingEnablement);
    }

    private static async Task<SetupAssistantDockerPreflightOutcome?> RunDockerPreflightIfNeededAsync(
        ISetupAssistantOperations operations,
        bool skipDockerPreflight,
        CancellationToken cancellationToken)
    {
        if (skipDockerPreflight)
        {
            return null;
        }

        var preflight = await operations.CheckDockerAsync(cancellationToken);
        return preflight.Passed ? null : preflight;
    }

    private static SetupAssistantMainWorkflowTransition Rejected(
        ISetupAssistantMainWorkflowState state,
        string rejectionKey) =>
        new()
        {
            State = state,
            Succeeded = false,
            Code = AcsSetupResultCode.RejectedInvalidMode,
            Kind = SetupAssistantOutcomeKind.Rejected,
            Rejected = true,
            RejectionKey = rejectionKey,
            FailedStep = SetupAssistantMainSetupFailedStep.None,
        };

    private static SetupAssistantMainWorkflowTransition TransitionFromState(
        State state,
        bool succeeded,
        string code,
        SetupAssistantOutcomeKind kind,
        bool configurationApplied = false,
        bool deploymentSendReady = false,
        string? bundleId = null,
        string? actionCode = null,
        SetupAssistantMainSetupFailedStep failedStep = SetupAssistantMainSetupFailedStep.None) =>
        new()
        {
            State = state,
            Succeeded = succeeded && state.IsComplete,
            Code = code,
            Kind = kind,
            ConfigurationApplied = configurationApplied,
            DeploymentSendReady = deploymentSendReady,
            BundleId = bundleId,
            ActionCode = actionCode,
            FailedStep = failedStep,
        };

    /// <summary>
    /// Mirrors <see cref="SetupAssistantTransitions.IsMainSetupCompletable"/> without session state.
    /// </summary>
    internal static bool IsMainSetupCompletableForMode(
        SetupAssistantMainSetupOutcome? mainSetup,
        SetupMode mode,
        SetupAssistantStagingOutcome? staging,
        SetupAssistantMainSetupOutcome? liveSending) =>
        mainSetup is { ConfigurationApplied: true } outcome
        && (outcome.Kind == SetupAssistantOutcomeKind.Succeeded
            || outcome.ActionCode == SetupApplyActionCode.CompleteSendReadyEvaluation)
        && mode switch
        {
            SetupMode.StagingVerification =>
                staging is { Kind: SetupAssistantOutcomeKind.Succeeded },
            SetupMode.ProductionAcs =>
                liveSending is { Kind: SetupAssistantOutcomeKind.Succeeded, DeploymentSendReady: true },
            _ => true,
        };

    /// <summary>
    /// Concrete service-issued state. Nested private so adapters in this assembly cannot construct
    /// or subclass a forged authority that AdvanceAsync would accept.
    /// </summary>
    private sealed class State : ISetupAssistantMainWorkflowState
    {
        private State(
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

        public SetupMode Mode { get; }

        public SetupAssistantMainWorkflowStage Stage { get; }

        public bool SkipDockerPreflight { get; }

        public SetupAssistantMainSetupOutcome? MainSetup { get; }

        public SetupAssistantStagingOutcome? Staging { get; }

        public SetupAssistantMainSetupOutcome? LiveSending { get; }

        public object? AppliedProof { get; }

        public bool ConfigurationStageSucceeded =>
            MainSetup is { ConfigurationApplied: true } outcome
            && (outcome.Kind == SetupAssistantOutcomeKind.Succeeded
                || outcome.ActionCode == SetupApplyActionCode.CompleteSendReadyEvaluation);

        public bool DeploymentSendReady =>
            LiveSending is { Kind: SetupAssistantOutcomeKind.Succeeded, DeploymentSendReady: true };

        public bool IsComplete =>
            Stage == SetupAssistantMainWorkflowStage.Completed
            && IsMainSetupCompletableForMode(MainSetup, Mode, Staging, LiveSending);

        public bool CanRetryApply =>
            Stage == SetupAssistantMainWorkflowStage.AwaitingApply
            && MainSetup is { } outcome
            && CanRetryApplyOutcome(outcome);

        public bool CanRetryStaging =>
            Stage == SetupAssistantMainWorkflowStage.AwaitingStagingVerification
            && Staging is
            {
                SendRequestAccepted: false,
                Kind: SetupAssistantOutcomeKind.Rejected or SetupAssistantOutcomeKind.Failed,
            };

        public bool CanRunLiveSending =>
            Stage == SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement
            && (LiveSending is null || CanRetryApplyOutcome(LiveSending));

        public bool CanFinish => IsComplete;

        internal static State CreateInitial(SetupMode mode) =>
            new(
                mode,
                SetupAssistantMainWorkflowStage.AwaitingApply,
                skipDockerPreflight: false,
                mainSetup: null,
                staging: null,
                liveSending: null,
                appliedProof: null);

        internal State WithSkipDockerPreflight(bool skip) =>
            new(Mode, Stage, skip, MainSetup, Staging, LiveSending, AppliedProof);

        internal static State FromApplyResult(
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

        internal State WithStaging(SetupAssistantStagingOutcome staging)
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

        internal State WithLiveSending(SetupAssistantMainSetupOutcome liveSending)
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

        internal State ClearedForApplyRetry()
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

        internal State ClearedForStagingRetry()
        {
            if (!CanRetryStaging || MainSetup is null || AppliedProof is null)
            {
                throw new InvalidOperationException("Staging retry is not eligible.");
            }

            return new(
                Mode,
                SetupAssistantMainWorkflowStage.AwaitingStagingVerification,
                SkipDockerPreflight,
                MainSetup,
                staging: null,
                liveSending: null,
                AppliedProof);
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
}
