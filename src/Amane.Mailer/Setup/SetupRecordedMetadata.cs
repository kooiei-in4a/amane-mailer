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
}
