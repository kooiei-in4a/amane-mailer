using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// The only seam through which the Web Assistant reaches Setup Core (#448), the host Docker
/// adapter (#449), apply/verify/rollback (#450), the typed ACS workflow (#451), and Admin
/// bootstrap (#459). The assistant owns no configuration, Docker, ACS, or Admin logic of its
/// own; every member here delegates to an existing typed operation and returns canonical codes.
/// Unexpected exceptions are not part of the contract: the Web adapter catches them and ends the
/// session as a manual-intervention fault rather than leaving the operator with an HTTP 500 to retry.
/// </summary>
internal interface ISetupAssistantOperations
{
    Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(CancellationToken cancellationToken);

    Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
        SetupAssistantMainSetupInput input,
        CancellationToken cancellationToken);

    Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
        SetupAssistantStagingInput input,
        CancellationToken cancellationToken);

    Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
        SetupAssistantProductionInput input,
        CancellationToken cancellationToken);

    Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
        SetupAssistantAdminAccessInput input,
        CancellationToken cancellationToken);

    Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
        SetupAssistantAdminBootstrapInput input,
        CancellationToken cancellationToken);
}

internal sealed class SetupAssistantDockerPreflightOutcome
{
    internal required bool Passed { get; init; }

    /// <summary>A <see cref="SetupDockerResultCode"/> constant. Never a raw process message.</summary>
    internal required string Code { get; init; }

    /// <summary>Engine classification enum name, or null. Never a host path or endpoint URI.</summary>
    internal string? EngineKind { get; init; }
}

internal sealed class SetupAssistantMainSetupInput
{
    internal required SetupMode Mode { get; init; }
    internal required MailerTenantsFile Tenants { get; init; }
    internal required IReadOnlyDictionary<string, string> TokenSecrets { get; init; }
    internal string? AcsConnectionString { get; init; }
    internal string? AcsConnectionStringConfirmation { get; init; }
    internal SetupPlatformSenderInput? PlatformSender { get; init; }

    /// <summary>Exact <c>Staging</c> / <c>Production</c> phrase typed by the operator (#451).</summary>
    internal string EnvironmentConfirmation { get; init; } = string.Empty;

    /// <summary>Exact <c>MAILER-ACS-REGISTER</c> phrase typed by the operator (#451).</summary>
    internal string IntentConfirmation { get; init; } = string.Empty;
}

internal sealed class SetupAssistantStagingInput
{
    internal required Guid TenantId { get; init; }
    internal required string RecipientEmail { get; init; }
    internal required string EnvironmentConfirmation { get; init; }
    internal required string IntentConfirmation { get; init; }
    internal required string AssistantSessionId { get; init; }
    internal required object AppliedProof { get; init; }
}

internal sealed class SetupAssistantProductionInput
{
    internal required string EnvironmentConfirmation { get; init; }
    internal required string LiveSendingEnableApproval { get; init; }
    internal required object AppliedProof { get; init; }
}

internal sealed class SetupAssistantMainSetupOutcome
{
    internal required string Code { get; init; }
    internal required SetupAssistantOutcomeKind Kind { get; init; }
    internal bool ConfigurationApplied { get; init; }
    internal bool DeploymentSendReady { get; init; }
    internal string? BundleId { get; init; }
    internal string? ConfigurationFingerprint { get; init; }
    internal string? ApplyResultCode { get; init; }
    internal string? ConfigRollbackStatus { get; init; }
    internal string? ActionCode { get; init; }
    internal bool PersistentSideEffectMayRemain { get; init; }
    internal string? PersistentSideEffectKind { get; init; }

    /// <summary>
    /// The #451 applied proof. It is an in-memory capability that must never be serialized,
    /// persisted, or sent to the browser, so it is carried as an opaque reference.
    /// </summary>
    internal object? AppliedProof { get; init; }
}

internal sealed class SetupAssistantStagingOutcome
{
    internal required string Code { get; init; }
    internal required SetupAssistantOutcomeKind Kind { get; init; }
    internal bool SendRequestAccepted { get; init; }
    internal bool OperationCompleted { get; init; }
    internal string? MailboxCheckStatus { get; init; }

    /// <summary>Already masked by <c>AcsAddressMask</c> inside #451. Never a full address.</summary>
    internal string? MaskedSenderEmail { get; init; }

    internal string? MaskedRecipientEmail { get; init; }
}

internal sealed class SetupAssistantAdminAccessInput
{
    internal required SetupAssistantAdminProfile Profile { get; init; }
    internal required string OriginText { get; init; }
    internal required string EnvironmentName { get; init; }

    /// <summary>
    /// Server-side <c>Connection.LocalIpAddress</c> the Admin policy must match (ADR 0013 D-03).
    /// It is an IP literal, never a host path or connection string.
    /// </summary>
    internal required string AllowedLocalAddress { get; init; }

    internal required bool AllowHttp { get; init; }
    internal required bool LoopbackOnlyPublished { get; init; }
    internal required bool ApprovedReverseProxy { get; init; }
    internal required bool ServerLocalAddressConfirmed { get; init; }
}

internal sealed class SetupAssistantAdminPreflightOutcome
{
    internal required bool Satisfied { get; init; }

    /// <summary>Canonical reason identifier. Never a raw exception or provider message.</summary>
    internal required string ReasonCode { get; init; }

    internal required SetupAssistantAdminProfile Profile { get; init; }
}

internal sealed class SetupAssistantAdminBootstrapInput
{
    internal required SetupAssistantAdminAccessInput Access { get; init; }
    internal required string Username { get; init; }
    internal required SetupAssistantSecret Password { get; init; }
    internal required IReadOnlyCollection<Guid> TenantIds { get; init; }
}

internal sealed class SetupAssistantAdminBootstrapOutcome
{
    internal required string Code { get; init; }
    internal required SetupAssistantOutcomeKind Kind { get; init; }
    internal required string AccessProfile { get; init; }
    internal required string ConfigRollback { get; init; }
    internal required string AdminDatabaseState { get; init; }
    internal required string AdminExposure { get; init; }
    internal required string LoginVerification { get; init; }
    internal required string SetupStatusVerification { get; init; }
    internal required string VerificationSessionCleanup { get; init; }
    internal bool ManualActionRequired { get; init; }
    internal string? ReasonCode { get; init; }
}

/// <summary>
/// Display classification the assistant uses to keep FAIL, operator ACTION, and retryable
/// states distinct. It never upgrades a partial or recovery state into success.
/// </summary>
internal enum SetupAssistantOutcomeKind
{
    Succeeded = 0,
    Rejected = 1,
    Failed = 2,
    ActionRequired = 3,
    ManualInterventionRequired = 4,
}

internal enum SetupAssistantAdminProfile
{
    LocalDevelopment = 0,
    ProductionHttps = 1,
}
