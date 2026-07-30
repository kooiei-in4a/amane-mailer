using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Everything a host adapter passes to <see cref="SetupAssistantMainSetupOrchestrator"/> to run
/// all or part of the main setup transaction. Adapters collect operator input; the orchestrator
/// owns Docker preflight, apply, and mode-specific follow-up sequencing.
/// </summary>
internal sealed class SetupAssistantMainSetupRunRequest
{
    internal required SetupMode Mode { get; init; }

    internal required SetupAssistantMainSetupRunPhase Phase { get; init; }

    /// <summary>
    /// When true, Docker preflight is skipped because the adapter already recorded a passing probe
    /// (for example the Web Assistant preflight screen).
    /// </summary>
    internal bool SkipDockerPreflight { get; init; }

    internal SetupAssistantMainSetupInput? MainSetupInput { get; init; }

    /// <summary>
    /// The #451 applied proof from a successful apply. Required for follow-up phases.
    /// </summary>
    internal object? ExistingAppliedProof { get; init; }

    internal Guid TenantId { get; init; }

    internal string? StagingRecipientEmail { get; init; }

    internal string? StagingEnvironmentConfirmation { get; init; }

    internal string? StagingIntentConfirmation { get; init; }

    internal string? AssistantSessionId { get; init; }

    internal string? ProductionEnvironmentConfirmation { get; init; }

    internal string? LiveSendingEnableApproval { get; init; }
}

/// <summary>
/// Which portion of the main setup transaction the orchestrator should execute.
/// </summary>
internal enum SetupAssistantMainSetupRunPhase
{
    /// <summary>Docker preflight (unless skipped) and configuration apply only.</summary>
    Apply = 0,

    /// <summary>Staging verification using an existing applied proof.</summary>
    StagingVerification = 1,

    /// <summary>Production live-sending enablement using an existing applied proof.</summary>
    LiveSendingEnablement = 2,

    /// <summary>
    /// Full pipeline through mode-specific completion. Used by terminal and non-interactive hosts.
    /// </summary>
    Full = 3,
}
