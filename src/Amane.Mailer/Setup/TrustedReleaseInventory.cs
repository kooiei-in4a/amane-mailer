using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Product-trusted release inventory consumed by the host Docker adapter.
/// ACTIVE compose.env image fields are never sufficient alone — digest match is required.
/// Schema aligns with the #455 <c>release-bundle-manifest.json</c> contract.
/// </summary>
public sealed class TrustedReleaseInventory
{
    private static readonly Regex Sha256Digest = new(
        "^sha256:[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const string ManifestFileName = "release-bundle-manifest.json";
    public const int CurrentSchemaVersion = 1;

    public required string AllowedImageRepository { get; init; }
    public required string RequiredImageDigest { get; init; }
    public required string AllowedDisplayTag { get; init; }
    public required string ComposeBundleVersion { get; init; }
    public string? ComposeSha256 { get; init; }
    public string? ComposeImageDigestSha256 { get; init; }
    public string? ComposeRecordedMetadataSha256 { get; init; }
    public string? ComposeMailpitSha256 { get; init; }
    public required string LauncherVersionMin { get; init; }
    public required string LauncherVersionMax { get; init; }
    public required string ProjectNamePrefix { get; init; }

    /// <summary>Digest-pinned Mailpit reference for mode 1 overlay (<c>repo@sha256:…</c>).</summary>
    public string? MailpitImageReference { get; init; }

    public string PinnedMailerImageReference =>
        $"{AllowedImageRepository}@{RequiredImageDigest}";

    public static bool IsValidDigest(string? digest) =>
        !string.IsNullOrWhiteSpace(digest) && Sha256Digest.IsMatch(digest);

    public static bool IsForbiddenDisplayTag(string? tag) =>
        string.IsNullOrWhiteSpace(tag)
        || string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
        || SetupImageDefaults.IsPlaceholderImageTag(tag);

    public SetupDockerResult? ValidateShape()
    {
        if (string.IsNullOrWhiteSpace(AllowedImageRepository)
            || AllowedImageRepository.Contains(' ', StringComparison.Ordinal)
            || AllowedImageRepository.Contains('@', StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release inventory image repository is invalid.");
        }

        if (!IsValidDigest(RequiredImageDigest))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release inventory image digest is invalid.");
        }

        if (IsForbiddenDisplayTag(AllowedDisplayTag))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release inventory display tag is forbidden.");
        }

        if (!IsValidDigest(ComposeSha256)
            || !IsValidDigest(ComposeImageDigestSha256)
            || !IsValidDigest(ComposeRecordedMetadataSha256)
            || (ComposeMailpitSha256 is not null && !IsValidDigest(ComposeMailpitSha256)))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted Compose file digest is invalid.");
        }

        if (string.IsNullOrWhiteSpace(ProjectNamePrefix)
            || ProjectNamePrefix.Length > 32
            || !ProjectNamePrefix.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release inventory project name prefix is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(MailpitImageReference)
            && !MailpitImageReference.Contains("@sha256:", StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted Mailpit image reference must be digest-pinned.");
        }

        return null;
    }

    public bool MatchesActiveImage(string? repository, string? tag) =>
        string.Equals(repository, AllowedImageRepository, StringComparison.Ordinal)
        && string.Equals(tag, AllowedDisplayTag, StringComparison.Ordinal);
}

/// <summary>Wire DTO for <c>release-bundle-manifest.json</c> (#455 shape; consumed by #449).</summary>
public sealed class ReleaseBundleManifestDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("imageRepository")]
    public string? ImageRepository { get; init; }

    [JsonPropertyName("imageDigest")]
    public string? ImageDigest { get; init; }

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; init; }

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
}
