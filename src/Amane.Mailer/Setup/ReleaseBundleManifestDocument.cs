using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

/// <summary>
/// Wire DTO for <c>release-bundle-manifest.json</c>.
/// schemaVersion stays 1; packaging fields are additive (#455).
/// Runtime host Docker continues to consume the inventory subset via
/// <see cref="TrustedReleaseInventory"/>.
/// Packaging emit/validate lives in tools/Amane.Mailer.ReleaseBundle (not product CLI).
/// </summary>
public sealed class ReleaseBundleManifestDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("packagingKind")]
    public string? PackagingKind { get; init; }

    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; init; }

    [JsonPropertyName("sourceCommitSha")]
    public string? SourceCommitSha { get; init; }

    [JsonPropertyName("mailerVersion")]
    public string? MailerVersion { get; init; }

    [JsonPropertyName("setupLauncherVersion")]
    public string? SetupLauncherVersion { get; init; }

    [JsonPropertyName("hostRid")]
    public string? HostRid { get; init; }

    [JsonPropertyName("targetRid")]
    public string? TargetRid { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("imageRepository")]
    public string? ImageRepository { get; init; }

    [JsonPropertyName("imageDigest")]
    public string? ImageDigest { get; init; }

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; init; }

    [JsonPropertyName("ociIndexDigest")]
    public string? OciIndexDigest { get; init; }

    [JsonPropertyName("ociLayoutRelativePath")]
    public string? OciLayoutRelativePath { get; init; }

    [JsonPropertyName("composeBundleVersion")]
    public string? ComposeBundleVersion { get; init; }

    [JsonPropertyName("composeSha256")]
    public string? ComposeSha256 { get; init; }

    [JsonPropertyName("composeImageDigestSha256")]
    public string? ComposeImageDigestSha256 { get; init; }

    [JsonPropertyName("composeRecordedMetadataSha256")]
    public string? ComposeRecordedMetadataSha256 { get; init; }

    [JsonPropertyName("composeMailpitSha256")]
    public string? ComposeMailpitSha256 { get; init; }

    [JsonPropertyName("launcherVersionMin")]
    public string? LauncherVersionMin { get; init; }

    [JsonPropertyName("launcherVersionMax")]
    public string? LauncherVersionMax { get; init; }

    [JsonPropertyName("projectNamePrefix")]
    public string? ProjectNamePrefix { get; init; }

    [JsonPropertyName("mailpitImageReference")]
    public string? MailpitImageReference { get; init; }

    [JsonPropertyName("supportedRecordedSchemaMin")]
    public int? SupportedRecordedSchemaMin { get; init; }

    [JsonPropertyName("supportedRecordedSchemaMax")]
    public int? SupportedRecordedSchemaMax { get; init; }

    [JsonPropertyName("supportedInspectEffectiveSchemaMin")]
    public int? SupportedInspectEffectiveSchemaMin { get; init; }

    [JsonPropertyName("supportedInspectEffectiveSchemaMax")]
    public int? SupportedInspectEffectiveSchemaMax { get; init; }

    /// <summary>
    /// Packaging-only additive range (#455). Runtime host Docker / ValidateShape
    /// do not require these fields; packaging requires both == schemaVersion (1).
    /// </summary>
    [JsonPropertyName("supportedReleaseManifestSchemaMin")]
    public int? SupportedReleaseManifestSchemaMin { get; init; }

    /// <summary>
    /// Packaging-only additive range (#455). Runtime host Docker / ValidateShape
    /// do not require these fields; packaging requires both == schemaVersion (1).
    /// </summary>
    [JsonPropertyName("supportedReleaseManifestSchemaMax")]
    public int? SupportedReleaseManifestSchemaMax { get; init; }

    [JsonPropertyName("artifactFileName")]
    public string? ArtifactFileName { get; init; }

    /// <summary>
    /// Non-self-referential staged payload tree digest (excludes manifest + checksum inventory).
    /// </summary>
    [JsonPropertyName("payloadTreeSha256")]
    public string? PayloadTreeSha256 { get; init; }

    /// <summary>Legacy field retained for additive deserialize compatibility.</summary>
    [JsonPropertyName("artifactSha256")]
    public string? ArtifactSha256 { get; init; }

    [JsonPropertyName("reproducibility")]
    public string? Reproducibility { get; init; }
}
