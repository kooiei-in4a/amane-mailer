namespace Amane.Mailer.Setup;

/// <summary>
/// Read-only migration classification returned by <c>db migrate --status --format json</c>
/// and <see cref="SetupHostDockerAdapter.InspectMigrationStatusAsync"/>.
/// </summary>
public sealed class SetupMigrationStatusDocument
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string Classification { get; init; }

    public static bool IsKnownClassification(string? classification) =>
        classification is SetupSchemaClassification.DatabaseAbsent
            or SetupSchemaClassification.Current
            or SetupSchemaClassification.Behind
            or SetupSchemaClassification.AheadOrUnsupported
            or SetupSchemaClassification.Unknown;
}

public static class SetupSchemaClassification
{
    public const string DatabaseAbsent = "DatabaseAbsent";
    public const string Current = "Current";
    public const string Behind = "Behind";
    public const string AheadOrUnsupported = "AheadOrUnsupported";
    public const string Unknown = "Unknown";
}

public enum SetupMigrationDecisionKind
{
    MigrationRequired = 0,
    MigrationNotRequired = 1,
    UpgradeRequired = 2,
    NeedsIntervention = 3,
}

public sealed class SetupMigrationDecision
{
    public required SetupMigrationDecisionKind Kind { get; init; }
    public string? ActionCode { get; init; }
    public string? ReasonCode { get; init; }
    public string? Message { get; init; }
}
