using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Opaque Main setup workflow authority. Only
/// <see cref="SetupAssistantMainSetupOrchestrator"/> can issue concrete instances; adapters may
/// hold and return the handle but cannot construct, invent outcomes, or set skip flags.
/// </summary>
internal interface ISetupAssistantMainWorkflowState
{
    SetupMode Mode { get; }

    SetupAssistantMainWorkflowStage Stage { get; }

    bool SkipDockerPreflight { get; }

    SetupAssistantMainSetupOutcome? MainSetup { get; }

    SetupAssistantStagingOutcome? Staging { get; }

    SetupAssistantMainSetupOutcome? LiveSending { get; }

    /// <summary>Service-issued applied proof from a successful configuration apply only.</summary>
    object? AppliedProof { get; }

    bool ConfigurationStageSucceeded { get; }

    bool DeploymentSendReady { get; }

    bool IsComplete { get; }

    bool CanRetryApply { get; }

    bool CanRetryStaging { get; }

    bool CanRunLiveSending { get; }

    bool CanFinish { get; }
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
/// shared service maps <see cref="ISetupAssistantMainWorkflowState.Stage"/> plus this input to the
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
    internal required ISetupAssistantMainWorkflowState State { get; init; }

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
