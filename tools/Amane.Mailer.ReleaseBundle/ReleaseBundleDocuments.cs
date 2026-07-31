using System.Text.Json.Serialization;

namespace Amane.Mailer.ReleaseBundle;

/// <summary>
/// Wire DTO for candidate <c>release-bundle-manifest.json</c> (schemaVersion 1, additive).
/// Shape mirrors the product runtime document for packaging emit/validate.
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

    [JsonPropertyName("artifactFileName")]
    public string? ArtifactFileName { get; init; }

    /// <summary>
    /// SHA-256 over staged payload paths/contents excluding the manifest and checksum inventory files.
    /// </summary>
    [JsonPropertyName("payloadTreeSha256")]
    public string? PayloadTreeSha256 { get; init; }

    /// <summary>Legacy self-referential field; no longer written by packaging.</summary>
    [JsonPropertyName("artifactSha256")]
    public string? ArtifactSha256 { get; init; }

    [JsonPropertyName("reproducibility")]
    public string? Reproducibility { get; init; }
}

public sealed class ImageIdentityDocument
{
    [JsonPropertyName("imageRepository")]
    public string? ImageRepository { get; init; }

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; init; }

    [JsonPropertyName("imageDigest")]
    public string? ImageDigest { get; init; }

    [JsonPropertyName("sourceCommitSha")]
    public string? SourceCommitSha { get; init; }

    [JsonPropertyName("mailerVersion")]
    public string? MailerVersion { get; init; }

    [JsonPropertyName("platforms")]
    public string[]? Platforms { get; init; }
}

public sealed class CandidateProvenanceDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("sourceCommitSha")]
    public string? SourceCommitSha { get; init; }

    [JsonPropertyName("releaseVersion")]
    public string? ReleaseVersion { get; init; }

    [JsonPropertyName("workflowRunId")]
    public string? WorkflowRunId { get; init; }

    [JsonPropertyName("workflowRunAttempt")]
    public string? WorkflowRunAttempt { get; init; }

    [JsonPropertyName("workflowRef")]
    public string? WorkflowRef { get; init; }

    [JsonPropertyName("imageRepository")]
    public string? ImageRepository { get; init; }

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; init; }

    [JsonPropertyName("ociIndexDigest")]
    public string? OciIndexDigest { get; init; }

    [JsonPropertyName("ociPlatforms")]
    public string[]? OciPlatforms { get; init; }

    [JsonPropertyName("mailpitImageReference")]
    public string? MailpitImageReference { get; init; }

    [JsonPropertyName("dotnetSdkVersion")]
    public string? DotnetSdkVersion { get; init; }

    [JsonPropertyName("archives")]
    public CandidateArchiveProvenance[]? Archives { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed class CandidateArchiveProvenance
{
    [JsonPropertyName("artifactName")]
    public string? ArtifactName { get; init; }

    [JsonPropertyName("archiveFileName")]
    public string? ArchiveFileName { get; init; }

    [JsonPropertyName("archiveSha256")]
    public string? ArchiveSha256 { get; init; }

    [JsonPropertyName("targetRid")]
    public string? TargetRid { get; init; }

    [JsonPropertyName("mailerVersion")]
    public string? MailerVersion { get; init; }

    [JsonPropertyName("setupLauncherVersion")]
    public string? SetupLauncherVersion { get; init; }

    [JsonPropertyName("payloadTreeSha256")]
    public string? PayloadTreeSha256 { get; init; }

    [JsonPropertyName("smokeResult")]
    public string? SmokeResult { get; init; }
}

public sealed class OciIndexDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("manifests")]
    public OciDescriptor[]? Manifests { get; init; }
}

public sealed class OciManifestDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("config")]
    public OciDescriptor? Config { get; init; }

    [JsonPropertyName("layers")]
    public OciDescriptor[]? Layers { get; init; }

    [JsonPropertyName("manifests")]
    public OciDescriptor[]? Manifests { get; init; }
}

public sealed class OciDescriptor
{
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("platform")]
    public OciPlatform? Platform { get; init; }
}

public sealed class OciPlatform
{
    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }

    [JsonPropertyName("variant")]
    public string? Variant { get; init; }
}
