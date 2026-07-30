namespace Amane.Mailer.Setup.NonInteractive;

internal sealed class SetupNonInteractiveInput
{
    internal required SetupMode Mode { get; init; }
    internal required Guid TenantId { get; init; }
    internal required string TenantName { get; init; }
    internal required string SourceService { get; init; }
    internal required string SenderEmail { get; init; }
    internal required string SenderDisplayName { get; init; }
    internal required string ServiceToken { get; init; }
    internal string? AcsConnectionString { get; init; }
    internal string? EnvironmentConfirmation { get; init; }
    internal string? IntentConfirmation { get; init; }
    internal string? StagingRecipientEmail { get; init; }
    internal string? StagingIntentConfirmation { get; init; }
    internal string? LiveSendingEnableApproval { get; init; }
}
