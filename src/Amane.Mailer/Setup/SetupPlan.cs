namespace Amane.Mailer.Setup;

public sealed class SetupPlan
{
    public required string BundleId { get; init; }
    public required string ConfigurationFingerprint { get; init; }
    public required SetupMode Mode { get; init; }
    public required IReadOnlyList<SetupPlannedFile> Files { get; init; }
}

public sealed class SetupPlannedFile
{
    /// <summary>Path relative to the bundle root using forward slashes.</summary>
    public required string RelativePath { get; init; }

    public required SetupPlannedFileKind Kind { get; init; }

    /// <summary>Byte length of the planned content (never secret bytes themselves).</summary>
    public required int ContentLength { get; init; }
}

public enum SetupPlannedFileKind
{
    PublicConfig,
    SecretValuedEnv,
    FileSecret,
    Metadata,
    FinalizedMarker,
}
