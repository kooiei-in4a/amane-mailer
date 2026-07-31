using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// The server-side state machine. A route and its action are only accepted from the steps that
/// actually render them, so a stale browser tab, a back button, a double submit, or a hand-made
/// POST cannot skip a stage, re-run an operation that already produced a canonical result, or
/// reach a completion screen the current mode has not earned.
/// </summary>
internal static class SetupAssistantTransitions
{
    internal static bool IsAllowed(SetupAssistantSession session, string route, string action) =>
        (route, action) switch
        {
            ("/welcome", "") => At(session, SetupAssistantStep.Welcome),

            // Re-running the Docker probe is read-only, so it stays available on its own screen.
            ("/preflight", "") => At(session, SetupAssistantStep.DockerPreflight),
            ("/preflight", "continue") => At(session, SetupAssistantStep.DockerPreflight),

            ("/mode", "") => At(session, SetupAssistantStep.ModeSelection),
            ("/mode", "back") => At(session, SetupAssistantStep.ManualModeGuidance),

            ("/tenant", "") => At(session, SetupAssistantStep.TenantBasics),
            ("/provider", "") => At(session, SetupAssistantStep.ProviderSettings),
            ("/acs", "") => At(session, SetupAssistantStep.AcsSettings),

            ("/confirm", "") => At(session, SetupAssistantStep.ApplyConfirmation),
            ("/confirm", "retry") => At(session, SetupAssistantStep.ApplyOutcome)
                && CanRetryMainApply(session),

            ("/verify", "continue") => At(session, SetupAssistantStep.ApplyOutcome)
                && session.MainSetupSucceeded,
            ("/verify", "staging") => AtVerification(session, SetupMode.StagingVerification)
                && session.Staging is null,
            ("/verify", "staging-retry") => AtVerification(session, SetupMode.StagingVerification)
                && CanRetryStaging(session),
            ("/verify", "production") => AtVerification(session, SetupMode.ProductionAcs)
                && CanRunLiveSending(session),
            ("/verify", "finish") => At(session, SetupAssistantStep.DeploymentVerification)
                && IsMainSetupCompletable(session),

            ("/admin-choice", "open") => At(session, SetupAssistantStep.MainSetupComplete),
            ("/admin-preflight", "open") => At(session, SetupAssistantStep.AdminChoice),
            ("/admin-preflight", "") => At(session, SetupAssistantStep.AdminAccessPreflight)
                && session.AdminPreflight is null,
            ("/admin-bootstrap", "open") => At(session, SetupAssistantStep.AdminAccessPreflight)
                && session.AdminPreflight is { Satisfied: true },
            ("/admin-bootstrap", "") => At(session, SetupAssistantStep.AdminBootstrapOutcome)
                && session.AdminBootstrap is null,

            ("/finish", "skip") => session.Step is SetupAssistantStep.MainSetupComplete
                or SetupAssistantStep.AdminChoice
                or SetupAssistantStep.AdminAccessPreflight,
            ("/finish", "continue") => At(session, SetupAssistantStep.AdminBootstrapOutcome),
            ("/finish", "incomplete") => session.Step is SetupAssistantStep.ApplyOutcome
                or SetupAssistantStep.DeploymentVerification,
            ("/finish", "stop") => session.Step is SetupAssistantStep.FinalGuidance
                or SetupAssistantStep.ManualModeGuidance,

            _ => false,
        };

    /// <summary>
    /// The completion condition each mode has to reach before the main setup transaction may be
    /// closed. Mode 4 cannot be closed before live sending is enabled, which is what keeps the
    /// send-ready boundary a server-side invariant rather than a screen-level convention.
    /// </summary>
    internal static bool IsMainSetupCompletable(SetupAssistantSession session) =>
        session.MainWorkflow?.IsComplete == true
        || (session.MainWorkflow is null
            && session.MainSetupSucceeded
            && session.Mode switch
            {
                SetupMode.StagingVerification =>
                    session.Staging is { Kind: SetupAssistantOutcomeKind.Succeeded },
                SetupMode.ProductionAcs => session.DeploymentSendReady,
                _ => true,
            });

    internal static bool CanRetryMainApply(SetupAssistantSession session) =>
        session.MainWorkflow?.CanRetryApply == true;

    internal static bool CanRetryStaging(SetupAssistantSession session) =>
        session.MainWorkflow?.CanRetryStaging == true
        || (session.MainWorkflow is null
            && session.Staging is
            {
                SendRequestAccepted: false,
                Kind: SetupAssistantOutcomeKind.Rejected or SetupAssistantOutcomeKind.Failed,
            });

    internal static bool CanRunLiveSending(SetupAssistantSession session) =>
        session.MainWorkflow?.CanRunLiveSending == true
        || (session.MainWorkflow is null
            && (session.LiveSending is null || CanRetryApplyOutcome(session.LiveSending)));

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

    private static bool At(SetupAssistantSession session, SetupAssistantStep step) =>
        session.Step == step;

    private static bool AtVerification(SetupAssistantSession session, SetupMode mode) =>
        At(session, SetupAssistantStep.DeploymentVerification)
        && session.Mode == mode
        && session.MainSetupSucceeded;
}
