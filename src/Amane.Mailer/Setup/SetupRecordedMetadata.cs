using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

/// <summary>
/// Easy Setup recorded metadata (ADR 0021 D-04). Read-only transport; not a runtime authority.
/// Contains no secrets, HMAC, salt, or sealing material.
/// </summary>
public sealed class SetupRecordedMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SetupBundleLayout.RecordedSchemaVersion;

    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("configurationFingerprint")]
    public required string ConfigurationFingerprint { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("imageRepository")]
    public string? ImageRepository { get; init; }

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; init; }

    [JsonPropertyName("platformSenderPresent")]
    public bool PlatformSenderPresent { get; init; }

    [JsonPropertyName("adminBootstrapRequested")]
    public bool AdminBootstrapRequested { get; init; }

    /// <summary>
    /// Internal runtime guard for an interactive managed Admin bootstrap. Public inspection
    /// surfaces must project <see cref="SetupInspectRecordedSummary"/> instead of serializing
    /// this metadata object.
    /// </summary>
    [JsonPropertyName("adminBootstrapExpectation")]
    public SetupAdminBootstrapExpectation? AdminBootstrapExpectation { get; init; }
}

public sealed class SetupAdminBootstrapExpectation
{
    [JsonPropertyName("operationId")]
    public required string OperationId { get; init; }

    [JsonPropertyName("before")]
    public required SetupAdminDatabaseExpectationState Before { get; init; }

    [JsonPropertyName("after")]
    public required SetupAdminDatabaseExpectationState After { get; init; }
}

public sealed class SetupAdminDatabaseExpectationState
{
    [JsonPropertyName("classification")]
    public required string Classification { get; init; }

    [JsonPropertyName("adminConfigCredentialEpoch")]
    public int? AdminConfigCredentialEpoch { get; init; }

    [JsonPropertyName("adminUserCredentialEpoch")]
    public int? AdminUserCredentialEpoch { get; init; }

    [JsonPropertyName("adminConfigCount")]
    public int AdminConfigCount { get; init; }

    [JsonPropertyName("adminUserCount")]
    public int AdminUserCount { get; init; }

    [JsonPropertyName("scopeFingerprint")]
    public string? ScopeFingerprint { get; init; }

    /// <summary>Fresh-only guard. Null for ManagedSameUser expectations.</summary>
    [JsonPropertyName("freshHasAnyAdminSessionRows")]
    public bool? FreshHasAnyAdminSessionRows { get; init; }
}
