using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Sequences Docker preflight, main apply, and mode-specific follow-up for every host adapter.
/// Web, terminal, and non-interactive paths call this type instead of invoking
/// <see cref="ISetupAssistantOperations"/> members directly for the main setup transaction.
/// </summary>
internal static class SetupAssistantMainSetupOrchestrator
{
    internal static async Task<SetupAssistantMainSetupRunResult> RunAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainSetupRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(request);

        return request.Phase switch
        {
            SetupAssistantMainSetupRunPhase.Apply =>
                await RunApplyAsync(operations, request, cancellationToken),
            SetupAssistantMainSetupRunPhase.StagingVerification =>
                await RunStagingVerificationAsync(operations, request, cancellationToken),
            SetupAssistantMainSetupRunPhase.LiveSendingEnablement =>
                await RunLiveSendingEnablementAsync(operations, request, cancellationToken),
            SetupAssistantMainSetupRunPhase.Full =>
                await RunFullAsync(operations, request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Phase, null),
        };
    }

    private static async Task<SetupAssistantMainSetupRunResult> RunApplyAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainSetupRunRequest request,
        CancellationToken cancellationToken)
    {
        var preflight = await RunDockerPreflightIfNeededAsync(
            operations,
            request.SkipDockerPreflight,
            cancellationToken);
        if (preflight is not null)
        {
            return FromDockerPreflightFailure(preflight);
        }

        if (request.MainSetupInput is not { } mainSetupInput)
        {
            return Rejected(AcsSetupResultCode.RejectedInvalidMode);
        }

        var mainSetup = await operations.ApplyMainSetupAsync(mainSetupInput, cancellationToken);
        return FromMainSetupOnly(mainSetup, request.Mode);
    }

    private static async Task<SetupAssistantMainSetupRunResult> RunStagingVerificationAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainSetupRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExistingAppliedProof is null
            || string.IsNullOrEmpty(request.StagingRecipientEmail)
            || string.IsNullOrEmpty(request.StagingEnvironmentConfirmation)
            || string.IsNullOrEmpty(request.StagingIntentConfirmation)
            || string.IsNullOrEmpty(request.AssistantSessionId))
        {
            return Rejected(AcsSetupResultCode.RejectedInvalidMode);
        }

        var staging = await operations.VerifyStagingAsync(
            new SetupAssistantStagingInput
            {
                TenantId = request.TenantId,
                RecipientEmail = request.StagingRecipientEmail,
                EnvironmentConfirmation = request.StagingEnvironmentConfirmation,
                IntentConfirmation = request.StagingIntentConfirmation,
                AssistantSessionId = request.AssistantSessionId,
                AppliedProof = request.ExistingAppliedProof,
            },
            cancellationToken);

        return FromStagingOnly(staging);
    }

    private static async Task<SetupAssistantMainSetupRunResult> RunLiveSendingEnablementAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainSetupRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExistingAppliedProof is null
            || string.IsNullOrEmpty(request.ProductionEnvironmentConfirmation)
            || string.IsNullOrEmpty(request.LiveSendingEnableApproval))
        {
            return Rejected(AcsSetupResultCode.RejectedInvalidMode);
        }

        var liveSending = await operations.EnableLiveSendingAsync(
            new SetupAssistantProductionInput
            {
                EnvironmentConfirmation = request.ProductionEnvironmentConfirmation,
                LiveSendingEnableApproval = request.LiveSendingEnableApproval,
                AppliedProof = request.ExistingAppliedProof,
            },
            cancellationToken);

        return FromLiveSendingOnly(liveSending);
    }

    private static async Task<SetupAssistantMainSetupRunResult> RunFullAsync(
        ISetupAssistantOperations operations,
        SetupAssistantMainSetupRunRequest request,
        CancellationToken cancellationToken)
    {
        var applyResult = await RunApplyAsync(operations, request, cancellationToken);
        if (!IsConfigurationStageSucceeded(applyResult.MainSetup))
        {
            return applyResult;
        }

        return request.Mode switch
        {
            SetupMode.StagingVerification when HasStagingInputs(request) =>
                MergeApplyWithFollowUp(
                    applyResult,
                    await RunStagingVerificationAsync(
                        operations,
                        WithAppliedProof(
                            request,
                            applyResult.AppliedProof,
                            SetupAssistantMainSetupRunPhase.StagingVerification),
                        cancellationToken),
                    request.Mode),
            SetupMode.ProductionAcs when HasLiveSendingInputs(request) =>
                MergeApplyWithFollowUp(
                    applyResult,
                    await RunLiveSendingEnablementAsync(
                        operations,
                        WithAppliedProof(
                            request,
                            applyResult.AppliedProof,
                            SetupAssistantMainSetupRunPhase.LiveSendingEnablement),
                        cancellationToken),
                    request.Mode),
            _ => applyResult,
        };
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

    private static SetupAssistantMainSetupRunResult FromDockerPreflightFailure(
        SetupAssistantDockerPreflightOutcome preflight) =>
        new()
        {
            Succeeded = false,
            Code = preflight.Code,
            Kind = SetupAssistantOutcomeKind.Failed,
            FailedStep = SetupAssistantMainSetupFailedStep.DockerPreflight,
        };

    private static SetupAssistantMainSetupRunResult FromMainSetupOnly(
        SetupAssistantMainSetupOutcome mainSetup,
        SetupMode mode) =>
        new()
        {
            Succeeded = IsMainSetupCompletableForMode(mainSetup, mode, staging: null, liveSending: null),
            Code = mainSetup.Code,
            Kind = mainSetup.Kind,
            ConfigurationApplied = mainSetup.ConfigurationApplied,
            DeploymentSendReady = mainSetup.DeploymentSendReady,
            BundleId = mainSetup.BundleId,
            ActionCode = mainSetup.ActionCode,
            AppliedProof = mainSetup.AppliedProof,
            MainSetup = mainSetup,
            FailedStep = IsConfigurationStageSucceeded(mainSetup)
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.MainApply,
        };

    private static SetupAssistantMainSetupRunResult FromStagingOnly(
        SetupAssistantStagingOutcome staging) =>
        new()
        {
            Succeeded = staging.Kind == SetupAssistantOutcomeKind.Succeeded,
            Code = staging.Code,
            Kind = staging.Kind,
            Staging = staging,
            FailedStep = staging.Kind == SetupAssistantOutcomeKind.Succeeded
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.StagingVerification,
        };

    private static SetupAssistantMainSetupRunResult FromLiveSendingOnly(
        SetupAssistantMainSetupOutcome liveSending) =>
        new()
        {
            Succeeded = liveSending.Kind == SetupAssistantOutcomeKind.Succeeded
                && liveSending.DeploymentSendReady,
            Code = liveSending.Code,
            Kind = liveSending.Kind,
            ConfigurationApplied = liveSending.ConfigurationApplied,
            DeploymentSendReady = liveSending.DeploymentSendReady,
            BundleId = liveSending.BundleId,
            ActionCode = liveSending.ActionCode,
            AppliedProof = liveSending.AppliedProof,
            LiveSending = liveSending,
            FailedStep = liveSending.Kind == SetupAssistantOutcomeKind.Succeeded
                && liveSending.DeploymentSendReady
                ? SetupAssistantMainSetupFailedStep.None
                : SetupAssistantMainSetupFailedStep.LiveSendingEnablement,
        };

    private static SetupAssistantMainSetupRunResult Rejected(string code) =>
        new()
        {
            Succeeded = false,
            Code = code,
            Kind = SetupAssistantOutcomeKind.Rejected,
            FailedStep = SetupAssistantMainSetupFailedStep.None,
        };

    private static bool HasStagingInputs(SetupAssistantMainSetupRunRequest request) =>
        !string.IsNullOrEmpty(request.StagingRecipientEmail)
        && !string.IsNullOrEmpty(request.StagingEnvironmentConfirmation)
        && !string.IsNullOrEmpty(request.StagingIntentConfirmation)
        && !string.IsNullOrEmpty(request.AssistantSessionId);

    private static bool HasLiveSendingInputs(SetupAssistantMainSetupRunRequest request) =>
        !string.IsNullOrEmpty(request.ProductionEnvironmentConfirmation)
        && !string.IsNullOrEmpty(request.LiveSendingEnableApproval);

    private static SetupAssistantMainSetupRunRequest WithAppliedProof(
        SetupAssistantMainSetupRunRequest request,
        object? appliedProof,
        SetupAssistantMainSetupRunPhase phase) =>
        new()
        {
            Mode = request.Mode,
            Phase = phase,
            SkipDockerPreflight = request.SkipDockerPreflight,
            MainSetupInput = request.MainSetupInput,
            ExistingAppliedProof = appliedProof,
            TenantId = request.TenantId,
            StagingRecipientEmail = request.StagingRecipientEmail,
            StagingEnvironmentConfirmation = request.StagingEnvironmentConfirmation,
            StagingIntentConfirmation = request.StagingIntentConfirmation,
            AssistantSessionId = request.AssistantSessionId,
            ProductionEnvironmentConfirmation = request.ProductionEnvironmentConfirmation,
            LiveSendingEnableApproval = request.LiveSendingEnableApproval,
        };

    private static SetupAssistantMainSetupRunResult MergeApplyWithFollowUp(
        SetupAssistantMainSetupRunResult applyResult,
        SetupAssistantMainSetupRunResult followUp,
        SetupMode mode) =>
        new()
        {
            Succeeded = IsMainSetupCompletableForMode(
                applyResult.MainSetup,
                mode,
                followUp.Staging,
                followUp.LiveSending),
            Code = followUp.Code,
            Kind = followUp.FailedStep == SetupAssistantMainSetupFailedStep.None
                ? SetupAssistantOutcomeKind.Succeeded
                : followUp.Kind,
            ConfigurationApplied = applyResult.ConfigurationApplied,
            DeploymentSendReady = followUp.DeploymentSendReady,
            BundleId = applyResult.BundleId,
            ActionCode = followUp.ActionCode ?? applyResult.ActionCode,
            AppliedProof = applyResult.AppliedProof,
            MainSetup = applyResult.MainSetup,
            Staging = followUp.Staging,
            LiveSending = followUp.LiveSending,
            FailedStep = followUp.FailedStep,
        };

    private static bool IsConfigurationStageSucceeded(SetupAssistantMainSetupOutcome? mainSetup) =>
        mainSetup is { ConfigurationApplied: true } outcome
        && (outcome.Kind == SetupAssistantOutcomeKind.Succeeded
            || outcome.ActionCode == SetupApplyActionCode.CompleteSendReadyEvaluation);

    /// <summary>
    /// Mirrors <see cref="SetupAssistantTransitions.IsMainSetupCompleatable"/> without session state.
    /// </summary>
    internal static bool IsMainSetupCompletableForMode(
        SetupAssistantMainSetupOutcome? mainSetup,
        SetupMode mode,
        SetupAssistantStagingOutcome? staging,
        SetupAssistantMainSetupOutcome? liveSending) =>
        IsConfigurationStageSucceeded(mainSetup)
        && mode switch
        {
            SetupMode.StagingVerification =>
                staging is { Kind: SetupAssistantOutcomeKind.Succeeded },
            SetupMode.ProductionAcs =>
                liveSending is { Kind: SetupAssistantOutcomeKind.Succeeded, DeploymentSendReady: true },
            _ => true,
        };
}
