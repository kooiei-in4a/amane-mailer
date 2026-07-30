namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Canonical outcome of a main setup orchestration run. Adapters map this into session state or
/// terminal output without re-sequencing typed operations themselves.
/// </summary>
internal sealed class SetupAssistantMainSetupRunResult
{
    internal required bool Succeeded { get; init; }

    internal required string Code { get; init; }

    internal required SetupAssistantOutcomeKind Kind { get; init; }

    internal bool ConfigurationApplied { get; init; }

    internal bool DeploymentSendReady { get; init; }

    internal string? BundleId { get; init; }

    internal string? ActionCode { get; init; }

    internal object? AppliedProof { get; init; }

    internal SetupAssistantMainSetupOutcome? MainSetup { get; init; }

    internal SetupAssistantStagingOutcome? Staging { get; init; }

    internal SetupAssistantMainSetupOutcome? LiveSending { get; init; }

    internal SetupAssistantMainSetupFailedStep FailedStep { get; init; }
}

/// <summary>Identifies which orchestrated step produced the terminal failure, if any.</summary>
internal enum SetupAssistantMainSetupFailedStep
{
    None = 0,
    DockerPreflight = 1,
    MainApply = 2,
    StagingVerification = 3,
    LiveSendingEnablement = 4,
}
