using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Shared Main setup application service. Owns operation ordering, AppliedProof continuity,
/// completion gates, and retry eligibility. Adapters submit only a service-issued
/// <see cref="SetupAssistantMainWorkflowState"/> plus newly collected operator input.
/// </summary>
internal static class SetupAssistantMainSetupOrchestrator
{
    internal static async Task<SetupAssistantMainWorkflowTransition> AdvanceAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainWorkflowState state,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);

        return state.Stage switch
        {
            SetupAssistantMainWorkflowStage.AwaitingApply =>
                await AdvanceApplyAsync(operations, state, input, cancellationToken),
            SetupAssistantMainWorkflowStage.AwaitingStagingVerification =>
                await AdvanceStagingAsync(operations, state, input, cancellationToken),
            SetupAssistantMainWorkflowStage.AwaitingLiveSendingEnablement =>
                await AdvanceLiveSendingAsync(operations, state, input, cancellationToken),
            SetupAssistantMainWorkflowStage.Completed =>
                Rejected(state, SetupAssistantRejection.StepNotAvailable),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Stage, null),
        };
    }

    /// <summary>
    /// Terminal and non-interactive driver: apply then mode-specific follow-up using one collected
    /// input set. Retry loops remain in the adapter; each retry calls AdvanceAsync or this method
    /// again with a fresh or cleared service-issued state.
    /// </summary>
    internal static async Task<SetupAssistantMainWorkflowTransition> RunToCompletionAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainWorkflowState initialState,
        SetupAssistantMainCollectedInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        if (initialState.Stage != SetupAssistantMainWorkflowStage.AwaitingApply
            || initialState.MainSetup is not null)
        {
            return Rejected(initialState, SetupAssistantRejection.StepNotAvailable);
        }

        var apply = await AdvanceAsync(operations, initialState, input, cancellationToken);
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
        SetupAssistantMainWorkflowState state,
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
            var failed = SetupAssistantMainWorkflowState.FromApplyResult(
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
        var next = SetupAssistantMainWorkflowState.FromApplyResult(
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
        SetupAssistantMainWorkflowState state,
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
        SetupAssistantMainWorkflowState state,
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
        SetupAssistantMainWorkflowState state,
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
        SetupAssistantMainWorkflowState state,
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
}
