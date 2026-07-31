using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup.NonInteractive;

internal sealed class SetupNonInteractiveResult
{
    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyOrder(2)]
    public required bool Ok { get; init; }

    [JsonPropertyOrder(3)]
    public required string Code { get; init; }

    [JsonPropertyOrder(4)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(5)]
    public string? Mode { get; init; }

    [JsonPropertyOrder(6)]
    public bool ConfigurationApplied { get; init; }

    [JsonPropertyOrder(7)]
    public bool DeploymentSendReady { get; init; }

    [JsonPropertyOrder(8)]
    public bool AdminBootstrapPerformed { get; init; }

    [JsonPropertyOrder(9)]
    public string? BundleId { get; init; }

    [JsonPropertyOrder(10)]
    public string? ActionCode { get; init; }

    [JsonPropertyOrder(11)]
    public string? MainSetupStatus { get; init; }

    internal static SetupNonInteractiveResult ValidationFailure(
        string code,
        string? mode = null,
        string? actionCode = null) =>
        new()
        {
            Ok = false,
            Code = code,
            Kind = SetupNonInteractiveKindWire.Rejected,
            Mode = mode,
            ConfigurationApplied = false,
            DeploymentSendReady = false,
            AdminBootstrapPerformed = false,
            BundleId = null,
            ActionCode = actionCode,
            MainSetupStatus = null,
        };

    internal static SetupNonInteractiveResult Cancelled(string? mode) =>
        new()
        {
            Ok = false,
            Code = SetupNonInteractiveResultCode.Cancelled,
            Kind = SetupNonInteractiveKindWire.Failed,
            Mode = mode,
            ConfigurationApplied = false,
            DeploymentSendReady = false,
            AdminBootstrapPerformed = false,
            BundleId = null,
            ActionCode = null,
            MainSetupStatus = "cancelled",
        };
}

internal static class SetupNonInteractiveKindWire
{
    internal const string Succeeded = "succeeded";
    internal const string Rejected = "rejected";
    internal const string Failed = "failed";
    internal const string ActionRequired = "action_required";
    internal const string ManualInterventionRequired = "manual_intervention_required";

    internal static string FromAssistantKind(Assistant.SetupAssistantOutcomeKind kind) =>
        kind switch
        {
            Assistant.SetupAssistantOutcomeKind.Succeeded => Succeeded,
            Assistant.SetupAssistantOutcomeKind.Rejected => Rejected,
            Assistant.SetupAssistantOutcomeKind.Failed => Failed,
            Assistant.SetupAssistantOutcomeKind.ActionRequired => ActionRequired,
            Assistant.SetupAssistantOutcomeKind.ManualInterventionRequired => ManualInterventionRequired,
            _ => Failed,
        };
}
