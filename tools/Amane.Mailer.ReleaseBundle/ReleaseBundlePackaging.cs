using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amane.Mailer.ReleaseBundle;

/// <summary>
/// Build-only Easy Setup release-candidate packaging (#455).
/// Not part of the product CLI.
/// </summary>
public static class ReleaseBundlePackaging
{
    public const string PackagingKind = "setup-release-candidate";
    public const string ChecksumsFileName = "FILES-SHA256SUMS";
    public const string LegacyChecksumsFileName = "SHA256SUMS";
    public const string ManifestFileName = "release-bundle-manifest.json";
    public const string ReadmeSetupFileName = "README-SETUP.md";
    public const string LicenseFileName = "LICENSE";
    public const string ExamplesDirectoryName = "examples";
    public const string ConfigDirectoryName = "config";
    public const string MailerConfigDirectoryName = "mailer";
    public const string DeployComposeRelativePath = "compose.yml";
    public const string ImageDigestOverlayRelativePath = "compose.image-digest.yml";
    public const string RecordedMetadataOverlayRelativePath = "compose.recorded-metadata.yml";
    public const string MailpitOverlayRelativePath = "compose.mailpit.yml";
    public const string OciLayoutMarkerFileName = "oci-layout";
    public const string OciIndexFileName = "index.json";
    public const int ComposeBundleVersionValue = 1;
    public const int ManifestSchemaVersion = 1;
    public const int MinimumSupportedRecordedSchemaVersion = 1;
    public const int RecordedSchemaVersion = 2;
    public const int InspectEffectiveSchemaVersion = 1;

    public static readonly string[] SupportedHostRids =
    [
        "win-x64",
        "linux-x64",
        "linux-arm64",
    ];

    public static readonly string[] RequiredOciPlatforms =
    [
        "linux/amd64",
        "linux/arm64",
    ];

    private static readonly HashSet<string> AllowedOciIndexMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
    };

    private static readonly HashSet<string> AllowedOciImageManifestMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    };

    private static readonly HashSet<string> AllowedOciConfigMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oci.image.config.v1+json",
        "application/vnd.docker.container.image.v1+json",
    };

    private static readonly HashSet<string> AllowedOciLayerMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oci.image.layer.v1.tar",
        "application/vnd.oci.image.layer.v1.tar+gzip",
        "application/vnd.oci.image.layer.v1.tar+zstd",
        "application/vnd.oci.image.layer.nondistributable.v1.tar",
        "application/vnd.oci.image.layer.nondistributable.v1.tar+gzip",
        "application/vnd.oci.image.layer.nondistributable.v1.tar+zstd",
        "application/vnd.docker.image.rootfs.diff.tar",
        "application/vnd.docker.image.rootfs.diff.tar.gzip",
        "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip",
    };

    private enum OciWalkRole
    {
        /// <summary>Buildx-bound descriptor: must be an image index.</summary>
        BoundImageIndex,

        /// <summary>Child of an image index: must be an image manifest; may contribute platforms.</summary>
        IndexPlatformManifest,

        /// <summary>Image manifest config descriptor.</summary>
        ManifestConfig,

        /// <summary>Image manifest layer descriptor.</summary>
        ManifestLayer,
    }

    private static readonly Regex FullSha1OrSha256 = new(
        "^[a-fA-F0-9]{40}$|^[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Sha256Digest = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReleaseVersionCore = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlobHexName = new(
        "^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LowercaseSha256Hex = new(
        "^[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ForbiddenBasenameExact =
    [
        ".env",
        "tenants.json",
        "secrets.env",
        "rclone.conf",
        "host-sealing-key",
    ];

    private static readonly HashSet<string> PayloadHashExcludedFileNames = new(StringComparer.Ordinal)
    {
        ManifestFileName,
        ChecksumsFileName,
        LegacyChecksumsFileName,
    };

    public sealed class StageRequest
    {
        public required string OutputDirectory { get; init; }

        /// <summary>
        /// Workflow-created temp parent. Staging may only create/replace directories under this root.
        /// </summary>
        public required string StagingParentDirectory { get; init; }

        public required string HostRid { get; init; }
        public required string HostBinaryPath { get; init; }
        public required string SourceCommitSha { get; init; }
        public required string MailerVersion { get; init; }
        public required string LauncherVersion { get; init; }
        public required string ImageRepository { get; init; }
        public required string ImageDisplayTag { get; init; }
        public required string OciIndexDigest { get; init; }
        public required string DeployComposePath { get; init; }
        public required string ImageDigestOverlayPath { get; init; }
        public required string RecordedMetadataOverlayPath { get; init; }
        public required string MailpitOverlayPath { get; init; }
        public required string EnvExamplePath { get; init; }
        public required string TenantsExamplePath { get; init; }
        public required string TenantsSchemaPath { get; init; }
        public required string TenantsLocalAcsExamplePath { get; init; }
        public required string LicensePath { get; init; }
        public required string MailpitImageReference { get; init; }
        public string ProjectNamePrefix { get; init; } = "amane";
        public bool AssertHostBinaryVersion { get; init; } = true;
        public int SupportedRecordedSchemaMin { get; init; } = MinimumSupportedRecordedSchemaVersion;
        public int SupportedRecordedSchemaMax { get; init; } = RecordedSchemaVersion;
        public int SupportedInspectEffectiveSchemaMin { get; init; } = InspectEffectiveSchemaVersion;
        public int SupportedInspectEffectiveSchemaMax { get; init; } = InspectEffectiveSchemaVersion;
    }

    public sealed class StageResult
    {
        public required bool Success { get; init; }
        public string? ReasonCode { get; init; }
        public string? Message { get; init; }
        public string? OutputDirectory { get; init; }
        public string? ManifestPath { get; init; }
        public string? PayloadTreeSha256 { get; init; }
        public ReleaseBundleManifestDocument? Manifest { get; init; }
    }

    public sealed class PackagingValidationResult
    {
        public required bool Success { get; init; }
        public string? ReasonCode { get; init; }
        public string? Message { get; init; }
    }

    /// <summary>
    /// Parsed Mailpit digest-pin reference (<c>name@sha256:…</c>).
    /// Rejects tag-style name-components (<c>repo:tag@sha256:…</c>) while allowing
    /// registry hosts with ports (<c>localhost:5000/mailpit@sha256:…</c>).
    /// </summary>
    public sealed class MailpitImageReferenceParts
    {
        public required string Name { get; init; }
        public required string Digest { get; init; }
        public required string NameComponent { get; init; }
    }

    public static bool IsSupportedHostRid(string? rid) =>
        !string.IsNullOrWhiteSpace(rid)
        && SupportedHostRids.Contains(rid, StringComparer.Ordinal);

    public static bool IsValidDigest(string? digest) =>
        !string.IsNullOrWhiteSpace(digest) && Sha256Digest.IsMatch(digest);

    public static bool IsValidReleaseVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && ReleaseVersionCore.IsMatch(version);

    public static bool IsValidMailpitImageReference(string? reference) =>
        TryParseMailpitImageReference(reference, out _);

    /// <summary>
    /// Dedicated Mailpit reference parser (not a single loose regex).
    /// Accepts <c>path/name@sha256:&lt;64 hex&gt;</c> and registry ports in the host segment.
    /// Rejects a <c>:</c> inside the final path name-component (image tag before digest).
    /// </summary>
    public static bool TryParseMailpitImageReference(
        string? reference,
        out MailpitImageReferenceParts? parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmed = reference.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal)
            || trimmed.Contains('\t', StringComparison.Ordinal)
            || trimmed.Contains('\n', StringComparison.Ordinal)
            || trimmed.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        const string digestMarker = "@sha256:";
        var atDigest = trimmed.LastIndexOf(digestMarker, StringComparison.Ordinal);
        if (atDigest <= 0)
        {
            return false;
        }

        // Exactly one '@' — the digest separator. Reject extra '@'.
        if (trimmed.IndexOf('@', StringComparison.Ordinal) != atDigest)
        {
            return false;
        }

        var name = trimmed[..atDigest];
        var digest = trimmed[(atDigest + 1)..]; // "sha256:…"
        if (name.Length == 0 || !IsValidDigest(digest))
        {
            return false;
        }

        var hex = digest["sha256:".Length..];
        if (!LowercaseSha256Hex.IsMatch(hex))
        {
            return false;
        }

        var lastSlash = name.LastIndexOf('/');
        var nameComponent = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
        if (nameComponent.Length == 0)
        {
            return false;
        }

        // Tag-before-digest: "axllent/mailpit:latest@sha256:…" → name-component contains ':'.
        // Registry port: "localhost:5000/mailpit@…" → ':' is before the last '/', allowed.
        if (nameComponent.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        parts = new MailpitImageReferenceParts
        {
            Name = name,
            Digest = digest,
            NameComponent = nameComponent,
        };
        return true;
    }

    public static bool IsForbiddenDisplayTag(string? tag) =>
        string.IsNullOrWhiteSpace(tag)
        || string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tag, "replace-with-published-git-sha", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("placeholder", StringComparison.OrdinalIgnoreCase);

    public static string HostBinaryFileName(string hostRid) =>
        string.Equals(hostRid, "win-x64", StringComparison.Ordinal)
            ? "Amane.Mailer.exe"
            : "Amane.Mailer";

    /// <summary>
    /// Runnable path prefix for README-SETUP examples for this host RID
    /// (<c>.\Amane.Mailer.exe</c> on win-x64; <c>./Amane.Mailer</c> otherwise).
    /// </summary>
    public static string ReadmeSetupLauncher(string hostRid) =>
        string.Equals(hostRid, "win-x64", StringComparison.Ordinal)
            ? @".\Amane.Mailer.exe"
            : "./Amane.Mailer";

    public static (string Platform, string Architecture) PlatformArchitectureForRid(string hostRid) =>
        hostRid switch
        {
            "win-x64" => ("windows", "x64"),
            "linux-x64" => ("linux", "x64"),
            "linux-arm64" => ("linux", "arm64"),
            _ => ("unknown", "unknown"),
        };

    public static string ArchiveFileName(string mailerVersion, string hostRid)
    {
        var versionLabel = mailerVersion.StartsWith('v')
            ? mailerVersion
            : "v" + mailerVersion;
        return hostRid switch
        {
            "win-x64" => $"amane-mailer-{versionLabel}-windows-x64.zip",
            "linux-x64" => $"amane-mailer-{versionLabel}-linux-x64.tar.gz",
            "linux-arm64" => $"amane-mailer-{versionLabel}-linux-arm64.tar.gz",
            _ => $"amane-mailer-{versionLabel}-{hostRid}.tar.gz",
        };
    }

    public static string VersionCore(string? informationalOrVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalOrVersion))
        {
            return string.Empty;
        }

        var plus = informationalOrVersion.IndexOf('+', StringComparison.Ordinal);
        var core = plus >= 0 ? informationalOrVersion[..plus] : informationalOrVersion;
        return core.Trim();
    }

    public static PackagingValidationResult AssertBinaryVersionCore(string binaryPath, string expectedCore)
    {
        if (!File.Exists(binaryPath))
        {
            return PackagingFail("binary_missing", "Host binary was not found for version assert.");
        }

        if (!IsValidReleaseVersion(expectedCore))
        {
            return PackagingFail("release_version_invalid", "Expected version core must be major.minor.patch.");
        }

        string? productVersion = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(binaryPath);
            productVersion = info.ProductVersion ?? info.FileVersion;
        }
        catch
        {
            productVersion = null;
        }

        if (string.IsNullOrWhiteSpace(productVersion))
        {
            // Native AOT on Linux often leaves FileVersionInfo empty; scan embedded informational version.
            productVersion = TryReadEmbeddedInformationalVersion(binaryPath, expectedCore);
        }

        var actualCore = VersionCore(productVersion);
        // Accept 1.2.0 or 1.2.0.0 file-version style cores.
        if (actualCore.EndsWith(".0", StringComparison.Ordinal)
            && actualCore.Count(static c => c == '.') == 3)
        {
            actualCore = actualCore[..actualCore.LastIndexOf('.')];
        }

        if (!string.Equals(actualCore, expectedCore, StringComparison.Ordinal))
        {
            return PackagingFail(
                "binary_version_mismatch",
                "Binary version core does not match release_version.");
        }

        return new PackagingValidationResult { Success = true };
    }

    private static string? TryReadEmbeddedInformationalVersion(string binaryPath, string expectedCore)
    {
        // Search for UTF-8 "major.minor.patch+" (InformationalVersion) without loading the whole file as text.
        var needle = Encoding.UTF8.GetBytes(expectedCore + "+");
        var bytes = File.ReadAllBytes(binaryPath);
        for (var i = 0; i <= bytes.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (bytes[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var end = i;
            while (end < bytes.Length)
            {
                var b = bytes[end];
                if (b < 0x20 || b > 0x7e)
                {
                    break;
                }

                end++;
                if (end - i > 200)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(bytes, i, end - i);
        }

        // Also accept plain core without '+' when present as a short ASCII token.
        var coreNeedle = Encoding.UTF8.GetBytes(expectedCore);
        for (var i = 0; i <= bytes.Length - coreNeedle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < coreNeedle.Length; j++)
            {
                if (bytes[i + j] != coreNeedle[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var beforeOk = i == 0 || bytes[i - 1] < 0x30 || bytes[i - 1] > 0x39;
            var afterIdx = i + coreNeedle.Length;
            var afterOk = afterIdx >= bytes.Length
                || bytes[afterIdx] is (byte)'+' or (byte)'.' or < 0x30 or > 0x39;
            if (beforeOk && afterOk)
            {
                return expectedCore;
            }
        }

        return null;
    }

    public static StageResult Stage(StageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsSupportedHostRid(request.HostRid))
        {
            return Fail("host_rid_unsupported", "Host RID is not supported for Easy Setup candidates.");
        }

        if (!IsValidReleaseVersion(request.MailerVersion)
            || !IsValidReleaseVersion(request.LauncherVersion))
        {
            return Fail(
                "release_version_invalid",
                "Mailer and launcher versions must be major.minor.patch (no -candidate suffix).");
        }

        if (!FullSha1OrSha256.IsMatch(request.SourceCommitSha))
        {
            return Fail("source_commit_invalid", "Source commit SHA must be 40 or 64 hex characters.");
        }

        if (IsForbiddenDisplayTag(request.ImageDisplayTag))
        {
            return Fail("image_tag_forbidden", "Image display tag must not be latest or a placeholder.");
        }

        if (!IsValidDigest(request.OciIndexDigest))
        {
            return Fail("oci_index_digest_invalid", "OCI index digest must be sha256:<64 hex>.");
        }

        if (string.IsNullOrWhiteSpace(request.ImageRepository))
        {
            return Fail("required_version_missing", "Image repository is required.");
        }

        if (!IsValidMailpitImageReference(request.MailpitImageReference))
        {
            return Fail(
                "mailpit_image_required",
                "mailpitImageReference is required as repo@sha256:<64 lowercase hex>.");
        }

        if (request.SupportedRecordedSchemaMin > request.SupportedRecordedSchemaMax
            || request.SupportedInspectEffectiveSchemaMin > request.SupportedInspectEffectiveSchemaMax)
        {
            return Fail("schema_range_invalid", "Supported schema ranges must be ordered min <= max.");
        }

        if (!File.Exists(request.HostBinaryPath))
        {
            return Fail("host_binary_missing", "Host Native AOT binary was not found.");
        }

        if (!File.Exists(request.LicensePath))
        {
            return Fail("license_missing", "LICENSE file is required for candidate staging.");
        }

        if (request.AssertHostBinaryVersion)
        {
            var versionAssert = AssertBinaryVersionCore(request.HostBinaryPath, request.MailerVersion);
            if (!versionAssert.Success)
            {
                return Fail(versionAssert.ReasonCode!, versionAssert.Message!);
            }
        }

        string outputFull;
        string parentFull;
        try
        {
            outputFull = Path.GetFullPath(request.OutputDirectory);
            parentFull = Path.GetFullPath(request.StagingParentDirectory);
        }
        catch
        {
            return Fail("output_path_invalid", "Output or staging parent path could not be resolved.");
        }

        var pathGuard = EnsureStagingPathAllowed(outputFull, parentFull);
        if (!pathGuard.Success)
        {
            return Fail(pathGuard.ReasonCode!, pathGuard.Message!);
        }

        try
        {
            if (Directory.Exists(outputFull))
            {
                if (Directory.EnumerateFileSystemEntries(outputFull).Any())
                {
                    return Fail(
                        "output_not_empty",
                        "Staging output must be a new or empty directory under the staging parent.");
                }

                Directory.Delete(outputFull, recursive: false);
            }

            Directory.CreateDirectory(outputFull);

            var binaryName = HostBinaryFileName(request.HostRid);
            var binaryDest = Path.Combine(outputFull, binaryName);
            File.Copy(request.HostBinaryPath, binaryDest, overwrite: true);
            if (!OperatingSystem.IsWindows()
                && !string.Equals(request.HostRid, "win-x64", StringComparison.Ordinal))
            {
                TryMarkExecutable(binaryDest);
            }

            CopyRequired(request.DeployComposePath, Path.Combine(outputFull, DeployComposeRelativePath));
            CopyRequired(
                request.ImageDigestOverlayPath,
                Path.Combine(outputFull, ImageDigestOverlayRelativePath));
            CopyRequired(
                request.RecordedMetadataOverlayPath,
                Path.Combine(outputFull, RecordedMetadataOverlayRelativePath));
            CopyRequired(
                request.MailpitOverlayPath,
                Path.Combine(outputFull, MailpitOverlayRelativePath));
            CopyRequired(request.LicensePath, Path.Combine(outputFull, LicenseFileName));

            var examplesDir = Path.Combine(outputFull, ExamplesDirectoryName);
            Directory.CreateDirectory(examplesDir);
            CopyRequired(request.EnvExamplePath, Path.Combine(examplesDir, ".env.example"));

            var configDir = Path.Combine(examplesDir, ConfigDirectoryName, MailerConfigDirectoryName);
            Directory.CreateDirectory(configDir);
            CopyRequired(request.TenantsExamplePath, Path.Combine(configDir, "tenants.example.json"));
            CopyRequired(request.TenantsSchemaPath, Path.Combine(configDir, "tenants.schema.json"));
            CopyRequired(
                request.TenantsLocalAcsExamplePath,
                Path.Combine(configDir, "tenants.local-acs.json.example"));

            WriteReadmeSetup(outputFull, request);

            var composeSha = DigestFile(Path.Combine(outputFull, DeployComposeRelativePath));
            var imageDigestOverlaySha = DigestFile(
                Path.Combine(outputFull, ImageDigestOverlayRelativePath));
            var recordedOverlaySha = DigestFile(
                Path.Combine(outputFull, RecordedMetadataOverlayRelativePath));
            var mailpitOverlaySha = DigestFile(
                Path.Combine(outputFull, MailpitOverlayRelativePath));

            var (platform, architecture) = PlatformArchitectureForRid(request.HostRid);
            var digestLower = request.OciIndexDigest.ToLowerInvariant();
            var artifactFileName = ArchiveFileName(request.MailerVersion, request.HostRid);
            var artifactId = $"{request.MailerVersion}/{request.HostRid}/{digestLower}";

            // Write a temporary manifest without payloadTreeSha256 so the payload hash can exclude it.
            var manifestWithoutHash = new ReleaseBundleManifestDocument
            {
                SchemaVersion = ManifestSchemaVersion,
                PackagingKind = PackagingKind,
                ArtifactId = artifactId,
                SourceCommitSha = request.SourceCommitSha.ToLowerInvariant(),
                MailerVersion = request.MailerVersion,
                SetupLauncherVersion = request.LauncherVersion,
                HostRid = request.HostRid,
                TargetRid = request.HostRid,
                Platform = platform,
                Architecture = architecture,
                ImageRepository = request.ImageRepository,
                ImageDigest = digestLower,
                ImageTag = request.ImageDisplayTag,
                OciIndexDigest = digestLower,
                ComposeBundleVersion = ComposeBundleVersionValue.ToString(CultureInfo.InvariantCulture),
                ComposeSha256 = composeSha,
                ComposeImageDigestSha256 = imageDigestOverlaySha,
                ComposeRecordedMetadataSha256 = recordedOverlaySha,
                ComposeMailpitSha256 = mailpitOverlaySha,
                LauncherVersionMin = request.LauncherVersion,
                LauncherVersionMax = request.LauncherVersion,
                ProjectNamePrefix = request.ProjectNamePrefix,
                MailpitImageReference = request.MailpitImageReference,
                SupportedRecordedSchemaMin = request.SupportedRecordedSchemaMin,
                SupportedRecordedSchemaMax = request.SupportedRecordedSchemaMax,
                SupportedInspectEffectiveSchemaMin = request.SupportedInspectEffectiveSchemaMin,
                SupportedInspectEffectiveSchemaMax = request.SupportedInspectEffectiveSchemaMax,
                SupportedReleaseManifestSchemaMin = ManifestSchemaVersion,
                SupportedReleaseManifestSchemaMax = ManifestSchemaVersion,
                ArtifactFileName = artifactFileName,
                Reproducibility =
                    "Same source commit SHA, Dockerfile base digests, publish flags "
                    + "(/p:Version + /p:InformationalVersion), and OCI layout inputs "
                    + "must produce the same OCI index digest and the same payloadTreeSha256 "
                    + "(excluding release-bundle-manifest.json and FILES-SHA256SUMS). "
                    + "#458 promotes qualified archive bytes; a rebuild produces a new candidate. "
                    + "Candidate packaging never pushes to GHCR.",
            };

            var packagingValidation = ValidatePackagingDocument(manifestWithoutHash, requirePayloadHash: false);
            if (!packagingValidation.Success)
            {
                return Fail(packagingValidation.ReasonCode!, packagingValidation.Message!);
            }

            var secretScan = ScanStagedTreeForSecrets(outputFull);
            if (!secretScan.Success)
            {
                return Fail(secretScan.ReasonCode!, secretScan.Message!);
            }

            // Payload hash excludes manifest + checksum inventory (non-self-referential).
            var payloadSha = ComputePayloadSha256(outputFull, PayloadHashExcludedFileNames);

            var manifest = CloneWithPayloadHash(manifestWithoutHash, payloadSha);
            packagingValidation = ValidatePackagingDocument(manifest, requirePayloadHash: true);
            if (!packagingValidation.Success)
            {
                return Fail(packagingValidation.ReasonCode!, packagingValidation.Message!);
            }

            var manifestPath = Path.Combine(outputFull, ManifestFileName);
            var manifestJson = JsonSerializer.Serialize(
                manifest,
                ReleaseBundleManifestJsonContext.Default.ReleaseBundleManifestDocument);
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            WriteChecksumsFile(outputFull);

            return new StageResult
            {
                Success = true,
                OutputDirectory = outputFull,
                ManifestPath = manifestPath,
                PayloadTreeSha256 = payloadSha,
                Manifest = manifest,
            };
        }
        catch (Exception ex)
        {
            return Fail("stage_failed", "Release bundle staging failed: " + ex.GetType().Name);
        }
    }

    public static PackagingValidationResult EnsureStagingPathAllowed(string outputFull, string parentFull)
    {
        if (string.IsNullOrWhiteSpace(parentFull)
            || !Directory.Exists(parentFull))
        {
            return PackagingFail(
                "staging_parent_missing",
                "Staging parent directory must exist (workflow-created temp root).");
        }

        var parentWithSep = parentFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!outputFull.StartsWith(parentWithSep, StringComparison.Ordinal)
            && !string.Equals(outputFull, parentFull, StringComparison.Ordinal))
        {
            return PackagingFail(
                "staging_path_outside_parent",
                "Staging output must be under the explicit staging parent directory.");
        }

        if (string.Equals(outputFull, parentFull, StringComparison.Ordinal))
        {
            return PackagingFail(
                "staging_path_is_parent",
                "Staging output must be a subdirectory of the staging parent, not the parent itself.");
        }

        // Refuse obviously dangerous roots.
        var root = Path.GetPathRoot(outputFull);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(
                outputFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail("staging_path_is_root", "Refusing to stage at a filesystem root.");
        }

        return new PackagingValidationResult { Success = true };
    }

    public static PackagingValidationResult ValidatePackagingDocument(
        ReleaseBundleManifestDocument document,
        bool requirePayloadHash = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != ManifestSchemaVersion)
        {
            return PackagingFail("schema_version_unsupported", "release-bundle-manifest schemaVersion must be 1.");
        }

        if (!string.Equals(document.PackagingKind, PackagingKind, StringComparison.Ordinal))
        {
            return PackagingFail("packaging_kind_invalid", "packagingKind must be setup-release-candidate.");
        }

        if (string.IsNullOrWhiteSpace(document.ArtifactId))
        {
            return PackagingFail("artifact_id_missing", "artifactId is required for packaging.");
        }

        if (string.IsNullOrWhiteSpace(document.SourceCommitSha)
            || !FullSha1OrSha256.IsMatch(document.SourceCommitSha))
        {
            return PackagingFail("source_commit_invalid", "sourceCommitSha is required and must be hex SHA.");
        }

        if (!IsValidReleaseVersion(document.MailerVersion)
            || !IsValidReleaseVersion(document.SetupLauncherVersion)
            || string.IsNullOrWhiteSpace(document.HostRid)
            || !IsSupportedHostRid(document.HostRid)
            || string.IsNullOrWhiteSpace(document.TargetRid)
            || !string.Equals(document.TargetRid, document.HostRid, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.Platform)
            || string.IsNullOrWhiteSpace(document.Architecture))
        {
            return PackagingFail(
                "host_metadata_invalid",
                "mailerVersion, setupLauncherVersion, targetRid/platform/architecture are required.");
        }

        if (!IsValidDigest(document.OciIndexDigest)
            || !IsValidDigest(document.ImageDigest)
            || !string.Equals(document.OciIndexDigest, document.ImageDigest, StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "oci_digest_mismatch",
                "ociIndexDigest must equal imageDigest and be a valid sha256 digest.");
        }

        if (IsForbiddenDisplayTag(document.ImageTag))
        {
            return PackagingFail("image_tag_forbidden", "imageTag must not be latest or a placeholder.");
        }

        if (!IsValidMailpitImageReference(document.MailpitImageReference))
        {
            return PackagingFail(
                "mailpit_image_required",
                "mailpitImageReference is required as repo@sha256:<64 lowercase hex>.");
        }

        if (document.SupportedRecordedSchemaMin is null
            || document.SupportedRecordedSchemaMax is null
            || document.SupportedInspectEffectiveSchemaMin is null
            || document.SupportedInspectEffectiveSchemaMax is null)
        {
            return PackagingFail("schema_range_missing", "Supported schema ranges are required for packaging.");
        }

        if (document.SupportedReleaseManifestSchemaMin is null
            || document.SupportedReleaseManifestSchemaMax is null)
        {
            return PackagingFail(
                "release_manifest_schema_range_missing",
                "supportedReleaseManifestSchemaMin/Max are required for packaging.");
        }

        if (document.SupportedReleaseManifestSchemaMin.Value != ManifestSchemaVersion
            || document.SupportedReleaseManifestSchemaMax.Value != ManifestSchemaVersion)
        {
            return PackagingFail(
                "release_manifest_schema_range_invalid",
                "supportedReleaseManifestSchemaMin/Max must both equal schemaVersion (1).");
        }

        var recordedMin = document.SupportedRecordedSchemaMin.Value;
        var recordedMax = document.SupportedRecordedSchemaMax.Value;
        var inspectMin = document.SupportedInspectEffectiveSchemaMin.Value;
        var inspectMax = document.SupportedInspectEffectiveSchemaMax.Value;
        if (recordedMin > recordedMax || inspectMin > inspectMax)
        {
            return PackagingFail("schema_range_invalid", "Supported schema ranges must be ordered min <= max.");
        }

        if (!IsValidDigest(document.ComposeSha256)
            || !IsValidDigest(document.ComposeImageDigestSha256)
            || !IsValidDigest(document.ComposeRecordedMetadataSha256)
            || !IsValidDigest(document.ComposeMailpitSha256))
        {
            return PackagingFail("compose_digest_invalid", "Compose file digests must be sha256 digests.");
        }

        if (requirePayloadHash)
        {
            if (!IsValidDigest(document.PayloadTreeSha256))
            {
                return PackagingFail(
                    "payload_tree_sha_invalid",
                    "payloadTreeSha256 must be sha256:<64 hex>.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(document.PayloadTreeSha256)
            && !IsValidDigest(document.PayloadTreeSha256))
        {
            return PackagingFail(
                "payload_tree_sha_invalid",
                "payloadTreeSha256 must be sha256:<64 hex> when set.");
        }

        if (!string.IsNullOrWhiteSpace(document.ArtifactSha256)
            && !IsValidDigest(document.ArtifactSha256))
        {
            return PackagingFail("artifact_sha_invalid", "artifactSha256 must be sha256:<64 hex> when set.");
        }

        if (!string.IsNullOrWhiteSpace(document.OciLayoutRelativePath))
        {
            return PackagingFail(
                "oci_layout_in_host_archive",
                "Host candidate archives must not embed an oci/ layout (OCI is a separate artifact).");
        }

        if (string.IsNullOrWhiteSpace(document.ArtifactFileName)
            || (!document.ArtifactFileName.Contains(document.MailerVersion!, StringComparison.Ordinal)
                && !document.ArtifactFileName.Contains("v" + document.MailerVersion, StringComparison.Ordinal)))
        {
            return PackagingFail(
                "artifact_filename_invalid",
                "artifactFileName must include the mailer version.");
        }

        return new PackagingValidationResult { Success = true };
    }

    public static PackagingValidationResult ValidateOciLayoutDirectory(
        string ociLayoutDirectory,
        string expectedImageDigest,
        IReadOnlyList<string>? requiredPlatforms = null,
        OciDescriptor? expectedRootDescriptor = null)
    {
        requiredPlatforms ??= RequiredOciPlatforms;

        if (string.IsNullOrWhiteSpace(ociLayoutDirectory) || !Directory.Exists(ociLayoutDirectory))
        {
            return PackagingFail("oci_layout_missing", "OCI layout directory is missing.");
        }

        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(ociLayoutDirectory);
        }
        catch
        {
            return PackagingFail("oci_layout_path_invalid", "OCI layout path could not be resolved.");
        }

        var layoutMarker = Path.Combine(rootFull, OciLayoutMarkerFileName);
        var indexPath = Path.Combine(rootFull, OciIndexFileName);
        if (!File.Exists(layoutMarker) || !File.Exists(indexPath))
        {
            return PackagingFail(
                "oci_layout_incomplete",
                "OCI layout must contain oci-layout and index.json.");
        }

        if (!IsValidDigest(expectedImageDigest))
        {
            return PackagingFail("oci_index_digest_invalid", "Expected OCI image digest is invalid.");
        }

        var blobsRoot = Path.Combine(rootFull, "blobs", "sha256");
        if (!Directory.Exists(blobsRoot))
        {
            return PackagingFail("oci_blobs_missing", "OCI layout blobs/sha256 directory is missing.");
        }

        // Reject symlinks and unexpected entries (descriptor-graph allowlist).
        foreach (var entry in Directory.EnumerateFileSystemEntries(rootFull, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(entry);
            var attr = File.GetAttributes(entry);
            if (attr.HasFlag(FileAttributes.ReparsePoint))
            {
                return PackagingFail("oci_symlink_rejected", "OCI layout must not contain symlinks.");
            }

            var relative = Path.GetRelativePath(rootFull, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                if (relative is "blobs" or "blobs/sha256")
                {
                    continue;
                }

                if (!relative.StartsWith("blobs/sha256/", StringComparison.Ordinal))
                {
                    return PackagingFail("oci_extra_entry", "OCI layout contains an unexpected directory.");
                }

                continue;
            }

            if (relative is OciLayoutMarkerFileName or OciIndexFileName)
            {
                continue;
            }

            if (!relative.StartsWith("blobs/sha256/", StringComparison.Ordinal))
            {
                return PackagingFail("oci_extra_entry", "OCI layout contains an unexpected file.");
            }

            var hex = relative["blobs/sha256/".Length..];
            if (!BlobHexName.IsMatch(hex))
            {
                return PackagingFail("oci_blob_name_invalid", "OCI blob file name must be 64 lowercase hex.");
            }

            if (info.Length == 0)
            {
                return PackagingFail("oci_blob_empty", "OCI layout must not contain empty blobs.");
            }

            var actualDigest = DigestFile(entry);
            var expectedNameDigest = "sha256:" + hex;
            if (!string.Equals(actualDigest, expectedNameDigest, StringComparison.Ordinal))
            {
                return PackagingFail(
                    "oci_blob_digest_mismatch",
                    "OCI blob content digest does not match file name.");
            }
        }

        OciIndexDocument? index;
        byte[] indexBytes;
        try
        {
            indexBytes = File.ReadAllBytes(indexPath);
            index = JsonSerializer.Deserialize(indexBytes, ReleaseBundleJsonContext.Default.OciIndexDocument);
        }
        catch
        {
            return PackagingFail("oci_index_unreadable", "OCI index.json could not be parsed.");
        }

        if (index?.Manifests is null || index.Manifests.Length == 0)
        {
            return PackagingFail("oci_index_empty", "OCI index.json manifests must not be empty.");
        }

        // Bind Buildx image digest to a descriptor inside index.json manifests[].
        // index.json is the OCI layout entrypoint file; its content digest is NOT the
        // Buildx containerimage.descriptor.digest / containerimage.digest identity.
        // That digest names the image-index blob referenced by a descriptor in
        // manifests[] (see OCI Image Layout + Buildx metadata-file).
        var expectedDigest = expectedImageDigest.ToLowerInvariant();
        var boundMatches = index.Manifests
            .Where(d =>
                !string.IsNullOrWhiteSpace(d.Digest)
                && string.Equals(d.Digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (boundMatches.Length == 0)
        {
            return PackagingFail(
                "oci_image_digest_mismatch",
                "expectedImageDigest must match exactly one descriptor digest in index.json manifests.");
        }

        if (boundMatches.Length != 1)
        {
            return PackagingFail(
                "oci_image_digest_ambiguous",
                "expectedImageDigest matched multiple descriptors in index.json manifests.");
        }

        // Candidate layout policy (fail-closed): index.json.manifests[] must contain
        // exactly the single Buildx-bound image-index descriptor. Sibling descriptors
        // must not contribute platforms or blobs outside that digest subtree.
        if (index.Manifests.Length != 1)
        {
            return PackagingFail(
                "oci_layout_sibling_manifests",
                "Candidate OCI layout index.json must contain exactly one manifests[] descriptor (the Buildx-bound image index).");
        }

        var bound = boundMatches[0];
        if (string.IsNullOrWhiteSpace(bound.MediaType)
            || !AllowedOciIndexMediaTypes.Contains(bound.MediaType))
        {
            return PackagingFail(
                "oci_bound_not_image_index",
                "Buildx-bound descriptor must use an OCI/Docker image-index mediaType.");
        }

        var boundBlobPath = BlobPath(rootFull, expectedDigest);
        if (!File.Exists(boundBlobPath))
        {
            return PackagingFail(
                "oci_blob_missing",
                "OCI layout is missing the blob referenced by the Buildx image digest descriptor.");
        }

        byte[] boundBlobBytes;
        try
        {
            boundBlobBytes = File.ReadAllBytes(boundBlobPath);
        }
        catch
        {
            return PackagingFail(
                "oci_blob_unreadable",
                "OCI layout could not read the blob referenced by the Buildx image digest.");
        }

        var boundContract = ValidateDescriptorContentContract(
            bound,
            boundBlobBytes,
            requireJsonMediaTypeMatch: true);
        if (!boundContract.Success)
        {
            return boundContract;
        }

        if (expectedRootDescriptor is not null)
        {
            var metaMatch = AssertImageDigestMatchesMetadata(
                expectedDigest,
                expectedRootDescriptor.Digest);
            if (!metaMatch.Success)
            {
                return PackagingFail(
                    "oci_descriptor_digest_mismatch",
                    "Buildx containerimage.descriptor.digest must equal --image-digest / bound manifests[] digest.");
            }

            if (expectedRootDescriptor.Size is null
                || expectedRootDescriptor.Size.Value != boundBlobBytes.LongLength
                || bound.Size!.Value != expectedRootDescriptor.Size.Value)
            {
                return PackagingFail(
                    "oci_descriptor_size_mismatch",
                    "Buildx containerimage.descriptor.size must equal bound descriptor size and blob byte length.");
            }

            var expectedMediaType = expectedRootDescriptor.MediaType;
            var boundMediaType = bound.MediaType;
            if (!string.IsNullOrWhiteSpace(expectedMediaType)
                && !string.Equals(expectedMediaType, boundMediaType, StringComparison.Ordinal))
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Buildx containerimage.descriptor.mediaType must match the bound index.json descriptor mediaType.");
            }

            if (!string.IsNullOrWhiteSpace(expectedMediaType)
                && !AllowedOciIndexMediaTypes.Contains(expectedMediaType))
            {
                return PackagingFail(
                    "oci_bound_not_image_index",
                    "Buildx containerimage.descriptor.mediaType must be an OCI/Docker image-index mediaType.");
            }
        }

        // Platform / graph validation is rooted at the bound descriptor only so
        // sibling top-level manifests cannot satisfy required platforms.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundPlatforms = new HashSet<string>(StringComparer.Ordinal);
        var walk = WalkOciDescriptors(
            rootFull,
            [bound],
            referenced,
            foundPlatforms,
            OciWalkRole.BoundImageIndex);
        if (!walk.Success)
        {
            return walk;
        }

        foreach (var required in requiredPlatforms)
        {
            if (!foundPlatforms.Contains(required))
            {
                return PackagingFail(
                    "oci_platform_missing",
                    "OCI layout is missing required platform " + required + ".");
            }
        }

        // Exactly the required platforms for candidate multi-arch (no missing; extras beyond required fail).
        if (requiredPlatforms.Count > 0)
        {
            var unexpected = foundPlatforms
                .Where(p => !requiredPlatforms.Contains(p, StringComparer.Ordinal))
                .ToArray();
            if (unexpected.Length > 0)
            {
                return PackagingFail(
                    "oci_platform_extra",
                    "OCI layout has unexpected platforms beyond the required set.");
            }

            if (foundPlatforms.Count != requiredPlatforms.Count)
            {
                return PackagingFail(
                    "oci_platform_count_mismatch",
                    "OCI layout must contain exactly the required platforms.");
            }
        }

        // Every blob on disk must be referenced by the descriptor graph.
        foreach (var blobFile in Directory.EnumerateFiles(blobsRoot))
        {
            var hex = Path.GetFileName(blobFile);
            var digest = "sha256:" + hex;
            if (!referenced.Contains(digest))
            {
                return PackagingFail(
                    "oci_extra_blob",
                    "OCI layout contains a blob not referenced by the descriptor graph.");
            }
        }

        return new PackagingValidationResult { Success = true };
    }

    /// <summary>
    /// Prefer Buildx <c>containerimage.descriptor</c>; fall back to <c>containerimage.digest</c>.
    /// </summary>
    public static PackagingValidationResult TryParseBuildxMetadata(
        string metadataJson,
        out string? imageDigest,
        out OciDescriptor? imageDescriptor)
    {
        imageDigest = null;
        imageDescriptor = null;
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return PackagingFail("buildx_metadata_empty", "Buildx metadata-file content is empty.");
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return PackagingFail("buildx_metadata_unreadable", "Buildx metadata-file could not be parsed.");
        }

        if (root.TryGetProperty("containerimage.descriptor", out var descriptorElement)
            && descriptorElement.ValueKind == JsonValueKind.Object)
        {
            string? digest = null;
            string? mediaType = null;
            long? size = null;
            if (descriptorElement.TryGetProperty("digest", out var digestEl)
                && digestEl.ValueKind == JsonValueKind.String)
            {
                digest = digestEl.GetString();
            }

            if (descriptorElement.TryGetProperty("mediaType", out var mediaEl)
                && mediaEl.ValueKind == JsonValueKind.String)
            {
                mediaType = mediaEl.GetString();
            }

            if (descriptorElement.TryGetProperty("size", out var sizeEl)
                && sizeEl.TryGetInt64(out var sizeValue))
            {
                size = sizeValue;
            }

            if (!IsValidDigest(digest))
            {
                return PackagingFail(
                    "buildx_descriptor_digest_invalid",
                    "containerimage.descriptor.digest is missing or invalid.");
            }

            imageDigest = digest!.ToLowerInvariant();
            imageDescriptor = new OciDescriptor
            {
                Digest = imageDigest,
                MediaType = mediaType,
                Size = size,
            };
            return new PackagingValidationResult { Success = true };
        }

        if (root.TryGetProperty("containerimage.digest", out var digestOnly)
            && digestOnly.ValueKind == JsonValueKind.String)
        {
            var digest = digestOnly.GetString();
            if (!IsValidDigest(digest))
            {
                return PackagingFail(
                    "buildx_digest_invalid",
                    "containerimage.digest is missing or invalid.");
            }

            imageDigest = digest!.ToLowerInvariant();
            return new PackagingValidationResult { Success = true };
        }

        return PackagingFail(
            "buildx_digest_missing",
            "Buildx metadata-file must contain containerimage.descriptor or containerimage.digest.");
    }

    /// <summary>
    /// Assert image-identity.json fields used when generating host RID archives.
    /// </summary>
    public static PackagingValidationResult AssertImageIdentityForHostPackaging(
        ImageIdentityDocument identity,
        string expectedSourceCommitSha,
        string expectedMailerVersion,
        IReadOnlyList<string>? requiredPlatforms = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        requiredPlatforms ??= RequiredOciPlatforms;

        if (string.IsNullOrWhiteSpace(identity.SourceCommitSha)
            || !FullSha1OrSha256.IsMatch(identity.SourceCommitSha)
            || !string.Equals(
                identity.SourceCommitSha,
                expectedSourceCommitSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "image_identity_source_sha_mismatch",
                "image-identity sourceCommitSha must equal git HEAD / SOURCE_SHA.");
        }

        if (!IsValidReleaseVersion(identity.MailerVersion)
            || !string.Equals(identity.MailerVersion, expectedMailerVersion, StringComparison.Ordinal))
        {
            return PackagingFail(
                "image_identity_mailer_version_mismatch",
                "image-identity mailerVersion must equal MAILER_VERSION.");
        }

        if (!IsValidDigest(identity.ImageDigest))
        {
            return PackagingFail(
                "image_identity_digest_invalid",
                "image-identity imageDigest must be sha256:<64 hex>.");
        }

        if (string.IsNullOrWhiteSpace(identity.ImageRepository)
            || string.IsNullOrWhiteSpace(identity.ImageTag)
            || IsForbiddenDisplayTag(identity.ImageTag))
        {
            return PackagingFail(
                "image_identity_tag_invalid",
                "image-identity repository/tag are required and must not use latest/placeholders.");
        }

        var platforms = identity.Platforms ?? [];
        if (platforms.Length != requiredPlatforms.Count
            || requiredPlatforms.Any(p => !platforms.Contains(p, StringComparer.Ordinal))
            || platforms.Any(p => !requiredPlatforms.Contains(p, StringComparer.Ordinal)))
        {
            return PackagingFail(
                "image_identity_platforms_mismatch",
                "image-identity platforms must be exactly linux/amd64 and linux/arm64.");
        }

        return new PackagingValidationResult { Success = true };
    }

    /// <summary>
    /// Fail-closed gate used by <c>validate-oci</c>: <c>--image-digest</c> must equal
    /// the Buildx metadata digest when metadata is supplied.
    /// </summary>
    public static PackagingValidationResult AssertImageDigestMatchesMetadata(
        string imageDigest,
        string? metadataDigest)
    {
        if (!IsValidDigest(imageDigest))
        {
            return PackagingFail(
                "oci_index_digest_invalid",
                "Expected OCI image digest is invalid.");
        }

        if (!IsValidDigest(metadataDigest))
        {
            return PackagingFail(
                "buildx_image_digest_mismatch",
                "--image-digest does not match Buildx metadata digest.");
        }

        if (!string.Equals(imageDigest, metadataDigest, StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "buildx_image_digest_mismatch",
                "--image-digest does not match Buildx metadata digest.");
        }

        return new PackagingValidationResult { Success = true };
    }

    /// <summary>
    /// Validate digest / size / mediaType contracts against blob bytes.
    /// </summary>
    private static PackagingValidationResult ValidateDescriptorContentContract(
        OciDescriptor descriptor,
        byte[] blobBytes,
        bool requireJsonMediaTypeMatch)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Digest) || !IsValidDigest(descriptor.Digest))
        {
            return PackagingFail("oci_descriptor_digest_invalid", "OCI descriptor digest is invalid.");
        }

        var digest = descriptor.Digest.ToLowerInvariant();
        var actualDigest = DigestBytes(blobBytes);
        if (!string.Equals(actualDigest, digest, StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "oci_blob_digest_mismatch",
                "Referenced blob content digest does not match the descriptor digest.");
        }

        if (descriptor.Size is null || descriptor.Size.Value < 0)
        {
            return PackagingFail(
                "oci_descriptor_size_missing",
                "OCI descriptor size is required and must be non-negative.");
        }

        if (descriptor.Size.Value != blobBytes.LongLength)
        {
            return PackagingFail(
                "oci_descriptor_size_mismatch",
                "OCI descriptor size must equal referenced blob byte length.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.MediaType))
        {
            return PackagingFail(
                "oci_descriptor_media_type_missing",
                "OCI descriptor mediaType is required.");
        }

        var mediaType = descriptor.MediaType;
        var allowed =
            AllowedOciIndexMediaTypes.Contains(mediaType)
            || AllowedOciImageManifestMediaTypes.Contains(mediaType)
            || AllowedOciConfigMediaTypes.Contains(mediaType)
            || AllowedOciLayerMediaTypes.Contains(mediaType);
        if (!allowed)
        {
            return PackagingFail(
                "oci_descriptor_media_type_unknown",
                "OCI descriptor mediaType is not in the candidate allowlist.");
        }

        if (!requireJsonMediaTypeMatch)
        {
            return new PackagingValidationResult { Success = true };
        }

        if (AllowedOciIndexMediaTypes.Contains(mediaType))
        {
            OciIndexDocument? nested;
            try
            {
                nested = JsonSerializer.Deserialize(
                    blobBytes,
                    ReleaseBundleJsonContext.Default.OciIndexDocument);
            }
            catch
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType claims an index but blob is not a readable OCI index.");
            }

            if (nested is null || nested.Manifests is null)
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType claims an index but blob is not a readable OCI index.");
            }

            if (!string.IsNullOrWhiteSpace(nested.MediaType)
                && !string.Equals(nested.MediaType, mediaType, StringComparison.Ordinal))
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType does not match mediaType inside the referenced index blob.");
            }

            return new PackagingValidationResult { Success = true };
        }

        if (AllowedOciImageManifestMediaTypes.Contains(mediaType))
        {
            OciManifestDocument? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    blobBytes,
                    ReleaseBundleJsonContext.Default.OciManifestDocument);
            }
            catch
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType claims a manifest but blob is not a readable OCI manifest.");
            }

            if (manifest is null)
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType claims a manifest but blob is not a readable OCI manifest.");
            }

            if (!string.IsNullOrWhiteSpace(manifest.MediaType)
                && !string.Equals(manifest.MediaType, mediaType, StringComparison.Ordinal))
            {
                return PackagingFail(
                    "oci_descriptor_media_type_mismatch",
                    "Descriptor mediaType does not match mediaType inside the referenced manifest blob.");
            }

            // Reject incomplete manifests that cannot form a publishable image graph.
            if (manifest.Config is null)
            {
                return PackagingFail(
                    "oci_manifest_incomplete",
                    "OCI image manifest must declare a config descriptor.");
            }

            return new PackagingValidationResult { Success = true };
        }

        // Config / layer: allowlist + size/digest only (binary layers are not JSON).
        return new PackagingValidationResult { Success = true };
    }

    private static PackagingValidationResult WalkOciDescriptors(
        string rootFull,
        OciDescriptor[] descriptors,
        HashSet<string> referenced,
        HashSet<string> foundPlatforms,
        OciWalkRole role)
    {
        foreach (var descriptor in descriptors)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Digest) || !IsValidDigest(descriptor.Digest))
            {
                return PackagingFail("oci_descriptor_digest_invalid", "OCI descriptor digest is invalid.");
            }

            var digest = descriptor.Digest.ToLowerInvariant();
            var blobPath = BlobPath(rootFull, digest);
            if (!File.Exists(blobPath))
            {
                return PackagingFail("oci_blob_missing", "OCI layout is missing a referenced blob.");
            }

            byte[] blobBytes;
            try
            {
                blobBytes = File.ReadAllBytes(blobPath);
            }
            catch
            {
                return PackagingFail("oci_blob_unreadable", "OCI layout could not read a referenced blob.");
            }

            // Re-visits still enforce this descriptor's size/mediaType against the blob.
            var requireJsonMatch =
                role is OciWalkRole.BoundImageIndex or OciWalkRole.IndexPlatformManifest;
            var contract = ValidateDescriptorContentContract(
                descriptor,
                blobBytes,
                requireJsonMediaTypeMatch: requireJsonMatch);
            if (!contract.Success)
            {
                return contract;
            }

            var mediaType = descriptor.MediaType!;
            switch (role)
            {
                case OciWalkRole.BoundImageIndex:
                    if (!AllowedOciIndexMediaTypes.Contains(mediaType))
                    {
                        return PackagingFail(
                            "oci_bound_not_image_index",
                            "Buildx-bound descriptor must use an OCI/Docker image-index mediaType.");
                    }

                    break;

                case OciWalkRole.IndexPlatformManifest:
                    if (!AllowedOciImageManifestMediaTypes.Contains(mediaType))
                    {
                        return PackagingFail(
                            "oci_platform_manifest_media_type_invalid",
                            "Image-index manifests[] entries must use an OCI/Docker image-manifest mediaType.");
                    }

                    break;

                case OciWalkRole.ManifestConfig:
                    if (!AllowedOciConfigMediaTypes.Contains(mediaType))
                    {
                        return PackagingFail(
                            "oci_config_media_type_invalid",
                            "Image manifest config must use an OCI/Docker config mediaType.");
                    }

                    if (descriptor.Platform is not null)
                    {
                        return PackagingFail(
                            "oci_platform_on_non_manifest",
                            "Config descriptors must not carry platform annotations.");
                    }

                    break;

                case OciWalkRole.ManifestLayer:
                    if (!AllowedOciLayerMediaTypes.Contains(mediaType))
                    {
                        return PackagingFail(
                            "oci_layer_media_type_invalid",
                            "Image manifest layers must use an OCI/Docker layer mediaType.");
                    }

                    if (descriptor.Platform is not null)
                    {
                        return PackagingFail(
                            "oci_platform_on_non_manifest",
                            "Layer descriptors must not carry platform annotations.");
                    }

                    break;
            }

            var alreadyWalked = !referenced.Add(digest);
            if (alreadyWalked)
            {
                continue;
            }

            if (role == OciWalkRole.BoundImageIndex)
            {
                OciIndexDocument? nested;
                try
                {
                    nested = JsonSerializer.Deserialize(
                        blobBytes,
                        ReleaseBundleJsonContext.Default.OciIndexDocument);
                }
                catch
                {
                    return PackagingFail("oci_nested_index_unreadable", "Nested OCI index could not be parsed.");
                }

                if (nested?.Manifests is null || nested.Manifests.Length == 0)
                {
                    return PackagingFail("oci_index_empty", "Nested OCI index manifests must not be empty.");
                }

                var nestedWalk = WalkOciDescriptors(
                    rootFull,
                    nested.Manifests,
                    referenced,
                    foundPlatforms,
                    OciWalkRole.IndexPlatformManifest);
                if (!nestedWalk.Success)
                {
                    return nestedWalk;
                }

                continue;
            }

            if (role == OciWalkRole.IndexPlatformManifest)
            {
                // Platforms are collected only from real image-manifest descriptors under the
                // bound image index (not from config/layer/unknown descriptors).
                if (descriptor.Platform is { Os: { } os, Architecture: { } arch }
                    && !string.IsNullOrWhiteSpace(os)
                    && !string.IsNullOrWhiteSpace(arch))
                {
                    foundPlatforms.Add(os + "/" + arch);
                }

                OciManifestDocument? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize(
                        blobBytes,
                        ReleaseBundleJsonContext.Default.OciManifestDocument);
                }
                catch
                {
                    return PackagingFail("oci_manifest_unreadable", "OCI manifest could not be parsed.");
                }

                if (manifest is null || manifest.Config is null)
                {
                    return PackagingFail(
                        "oci_manifest_incomplete",
                        "OCI image manifest must declare a config descriptor.");
                }

                // Nested index-style manifests[] on an image manifest is out of policy for candidates.
                if (manifest.Manifests is { Length: > 0 })
                {
                    return PackagingFail(
                        "oci_manifest_nested_index_forbidden",
                        "Candidate image manifests must not embed a nested manifests[] index.");
                }

                var configWalk = WalkOciDescriptors(
                    rootFull,
                    [manifest.Config],
                    referenced,
                    foundPlatforms,
                    OciWalkRole.ManifestConfig);
                if (!configWalk.Success)
                {
                    return configWalk;
                }

                if (manifest.Layers is { Length: > 0 })
                {
                    var layerWalk = WalkOciDescriptors(
                        rootFull,
                        manifest.Layers,
                        referenced,
                        foundPlatforms,
                        OciWalkRole.ManifestLayer);
                    if (!layerWalk.Success)
                    {
                        return layerWalk;
                    }
                }

                continue;
            }

            // Config / layer leaves: content contract already enforced; no further children.
        }

        return new PackagingValidationResult { Success = true };
    }

    private static string BlobPath(string rootFull, string digest)
    {
        var hex = digest["sha256:".Length..].ToLowerInvariant();
        return Path.Combine(rootFull, "blobs", "sha256", hex);
    }

    public static PackagingValidationResult ScanStagedTreeForSecrets(string stagedRoot)
    {
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(stagedRoot);
        }
        catch
        {
            return PackagingFail("scan_root_invalid", "Staged root path could not be resolved.");
        }

        if (!Directory.Exists(rootFull))
        {
            return PackagingFail("scan_root_missing", "Staged root directory is missing.");
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(rootFull, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            var basename = Path.GetFileName(path);

            if (string.Equals(basename, "oci", StringComparison.Ordinal)
                && Directory.Exists(path))
            {
                return PackagingFail(
                    "oci_layout_in_host_archive",
                    "Staged host tree must not embed an oci/ directory.");
            }

            if (string.Equals(basename, ".env", StringComparison.Ordinal)
                || relative.EndsWith("/.env", StringComparison.Ordinal))
            {
                return PackagingFail(
                    "secret_path_detected",
                    "Staged tree must not include a private .env file.");
            }

            if (ForbiddenBasenameExact.Contains(basename, StringComparer.Ordinal))
            {
                return PackagingFail(
                    "secret_path_detected",
                    "Staged tree contains a forbidden secret-like path.");
            }

            if (basename.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || basename.EndsWith(".db.age", StringComparison.OrdinalIgnoreCase))
            {
                return PackagingFail(
                    "secret_path_detected",
                    "Staged tree must not include database or backup files.");
            }

            if (relative.Contains("acs_connection_string", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("queue_connection_string", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("host-sealing-key", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("rclone.conf", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("id_rsa", StringComparison.OrdinalIgnoreCase))
            {
                return PackagingFail(
                    "secret_path_detected",
                    "Staged tree contains a forbidden secret-like path.");
            }

            if (File.Exists(path)
                && relative.EndsWith("release-bundle-manifest.json", StringComparison.Ordinal))
            {
                var text = File.ReadAllText(path);
                if (text.Contains("\"latest\"", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("imageTag", StringComparison.Ordinal))
                {
                    // Structural check for forbidden latest tag values.
                    if (Regex.IsMatch(
                            text,
                            "\"imageTag\"\\s*:\\s*\"latest\"",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        return PackagingFail("latest_tag_detected", "Manifest must not pin imageTag latest.");
                    }
                }
            }
        }

        return new PackagingValidationResult { Success = true };
    }

    public static string DigestFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string DigestBytes(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ComputePayloadSha256(
        string stagedRoot,
        IReadOnlyCollection<string>? excludeFileNames = null)
    {
        excludeFileNames ??= PayloadHashExcludedFileNames;
        var rootFull = Path.GetFullPath(stagedRoot);
        var entries = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/'),
            })
            .Where(e => !excludeFileNames.Contains(Path.GetFileName(e.Path)))
            .OrderBy(e => e.Relative, StringComparer.Ordinal)
            .ToArray();

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(entry.Relative + "\n");
            hasher.AppendData(nameBytes);
            var content = File.ReadAllBytes(entry.Path);
            hasher.AppendData(BitConverter.GetBytes(content.LongLength));
            hasher.AppendData(content);
        }

        return "sha256:" + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    public static PackagingValidationResult VerifyChecksumsFile(string stagedRoot)
    {
        var rootFull = Path.GetFullPath(stagedRoot);
        var checksumsPath = Path.Combine(rootFull, ChecksumsFileName);
        if (!File.Exists(checksumsPath))
        {
            var legacy = Path.Combine(rootFull, LegacyChecksumsFileName);
            if (File.Exists(legacy))
            {
                checksumsPath = legacy;
            }
            else
            {
                return PackagingFail("checksums_missing", "FILES-SHA256SUMS is missing from staged tree.");
            }
        }

        var lines = File.ReadAllLines(checksumsPath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64)
            {
                return PackagingFail("checksums_line_invalid", "Checksum inventory line is invalid.");
            }

            var relative = parts[1].Trim();
            if (relative.StartsWith('*'))
            {
                relative = relative[1..];
            }

            var filePath = Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                return PackagingFail("checksums_file_missing", "Checksum inventory references a missing file.");
            }

            var actual = DigestFile(filePath);
            var expected = "sha256:" + parts[0].ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                return PackagingFail("checksums_mismatch", "Checksum inventory digest mismatch.");
            }

            seen.Add(relative.Replace('\\', '/'));
        }

        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            var name = Path.GetFileName(file);
            if (string.Equals(name, ChecksumsFileName, StringComparison.Ordinal)
                || string.Equals(name, LegacyChecksumsFileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!seen.Contains(relative))
            {
                return PackagingFail("checksums_incomplete", "Checksum inventory is missing a staged file.");
            }
        }

        return new PackagingValidationResult { Success = true };
    }

    private static ReleaseBundleManifestDocument CloneWithPayloadHash(
        ReleaseBundleManifestDocument source,
        string payloadTreeSha256) =>
        new()
        {
            SchemaVersion = source.SchemaVersion,
            PackagingKind = source.PackagingKind,
            ArtifactId = source.ArtifactId,
            SourceCommitSha = source.SourceCommitSha,
            MailerVersion = source.MailerVersion,
            SetupLauncherVersion = source.SetupLauncherVersion,
            HostRid = source.HostRid,
            TargetRid = source.TargetRid,
            Platform = source.Platform,
            Architecture = source.Architecture,
            ImageRepository = source.ImageRepository,
            ImageDigest = source.ImageDigest,
            ImageTag = source.ImageTag,
            OciIndexDigest = source.OciIndexDigest,
            ComposeBundleVersion = source.ComposeBundleVersion,
            ComposeSha256 = source.ComposeSha256,
            ComposeImageDigestSha256 = source.ComposeImageDigestSha256,
            ComposeRecordedMetadataSha256 = source.ComposeRecordedMetadataSha256,
            ComposeMailpitSha256 = source.ComposeMailpitSha256,
            LauncherVersionMin = source.LauncherVersionMin,
            LauncherVersionMax = source.LauncherVersionMax,
            ProjectNamePrefix = source.ProjectNamePrefix,
            MailpitImageReference = source.MailpitImageReference,
            SupportedRecordedSchemaMin = source.SupportedRecordedSchemaMin,
            SupportedRecordedSchemaMax = source.SupportedRecordedSchemaMax,
            SupportedInspectEffectiveSchemaMin = source.SupportedInspectEffectiveSchemaMin,
            SupportedInspectEffectiveSchemaMax = source.SupportedInspectEffectiveSchemaMax,
            SupportedReleaseManifestSchemaMin = source.SupportedReleaseManifestSchemaMin,
            SupportedReleaseManifestSchemaMax = source.SupportedReleaseManifestSchemaMax,
            ArtifactFileName = source.ArtifactFileName,
            PayloadTreeSha256 = payloadTreeSha256,
            Reproducibility = source.Reproducibility,
        };

    private static void WriteChecksumsFile(string stagedRoot)
    {
        var rootFull = Path.GetFullPath(stagedRoot);
        var lines = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
                if (string.Equals(Path.GetFileName(path), ChecksumsFileName, StringComparison.Ordinal)
                    || string.Equals(Path.GetFileName(path), LegacyChecksumsFileName, StringComparison.Ordinal))
                {
                    return null;
                }

                var digest = DigestFile(path);
                var hex = digest["sha256:".Length..];
                return $"{hex}  {relative}";
            })
            .Where(static line => line is not null)
            .OrderBy(static line => line, StringComparer.Ordinal)
            .ToArray();

        File.WriteAllLines(
            Path.Combine(rootFull, ChecksumsFileName),
            lines!,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteReadmeSetup(string stagedRoot, StageRequest request)
    {
        var sha = request.SourceCommitSha;
        var launcher = ReadmeSetupLauncher(request.HostRid);
        var setupGuideJa =
            $"https://github.com/kooiei-in4a/amane-mailer/blob/{sha}/docs/ops/setup-guide.md";
        var setupGuideEn =
            $"https://github.com/kooiei-in4a/amane-mailer/blob/{sha}/docs/ops/setup-guide.en.md";

        var content =
            $"""
            # Amane Mailer Easy Setup candidate bundle

            This file is a **minimal entry** for operators who extracted this host
            bundle. Detailed judgment, order, and safety boundaries live only in
            the setup guide (same commit as this candidate). This README is
            inventory and start commands — not the procedure authority.

            This directory is a **release-candidate / qualification** Easy Setup
            host bundle (#455). It is **not** a published GitHub Release artifact
            and must not be treated as published (#458 owns publish).

            ## Setup guide (authority)

            - JA: {setupGuideJa}
            - EN: {setupGuideEn}

            ## Offline fallback (GitHub unavailable)

            From this extracted directory:

            ```text
            {launcher} setup assistant --help
            {launcher} setup assistant --terminal
            ```

            ## Start commands

            From this extracted directory (host RID `{request.HostRid}`):

            ```text
            {launcher} setup assistant
            ```

            Headless / VPS (SSH tunnel / terminal details are in the setup guide).
            Browser alone does not complete setup on a remote VPS:

            ```text
            {launcher} setup assistant --no-browser
            {launcher} setup assistant --terminal
            ```

            Non-interactive Main setup only (Admin stays disabled; details in
            the setup guide):

            ```text
            {launcher} setup apply --config <absolute-path> --non-interactive
            ```

            Mode 5 (production ACS + Event Grid / Storage Queue) is **Manual /
            not Easy Setup**. Do not expect the assistant to automate mode 5.

            ## Inventory (identity — not procedure authority)

            - Host RID: `{request.HostRid}`
            - Mailer version: `{request.MailerVersion}`
            - Setup launcher version: `{request.LauncherVersion}`
            - Source commit: `{request.SourceCommitSha}`
            - Image repository: `{request.ImageRepository}`
            - Display tag: `{request.ImageDisplayTag}`
            - OCI index digest: `{request.OciIndexDigest}`
            - Mailpit: `{request.MailpitImageReference}`

            These fields identify this candidate. Follow the setup guide for
            how to verify and operate.

            ## Checksum concepts (do not conflate)

            - `FILES-SHA256SUMS` — verify **files after extract** (per-file inventory
              inside this tree).
            - `CANDIDATE-SHA256SUMS` (outer handoff) — verify the **archive itself**
              before / as you extract.
            - `payloadTreeSha256` in `release-bundle-manifest.json` — a tree digest
              of staged payload bytes (excludes the manifest and checksum files).
              It is **not** the archive checksum.

            ## Distinctions

            - `release-bundle-manifest.json` describes this **distribution** candidate
              (versions, source SHA, OCI index digest, schema ranges, payloadTreeSha256).
            - Managed deployment metadata under operator `managed/` (ACTIVE pointer,
              recorded.json, verification) is created later on the host and is a
              different concept. Do not merge the two.
            - Archive SHA-256 lives in outer `CANDIDATE-SHA256SUMS` / provenance (#458
              promotes qualified archive bytes; a rebuild is a new candidate).

            Setup never pulls `latest`. Compose uses the digest-pinned overlay
            (`compose.image-digest.yml`) so runtime references the immutable digest.

            ## Upgrade

            Product upgrade / publish is owned by later release issues (#458). This
            candidate package is for qualification (#456) only. Setup is not upgrade.

            ## Non-goals

            - No Git tag, GitHub Release, or GHCR push from this packaging path
            - No MSI / deb / rpm installer
            - No auto-updater
            - No macOS formal artifact
            """;

        File.WriteAllText(
            Path.Combine(stagedRoot, ReadmeSetupFileName),
            content.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CopyRequired(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Required packaging input missing.", source);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void TryMarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort on restricted FS; archive smoke verifies bit after extract without chmod.
        }
    }

    private static StageResult Fail(string reasonCode, string message) =>
        new()
        {
            Success = false,
            ReasonCode = reasonCode,
            Message = message,
        };

    private static PackagingValidationResult PackagingFail(string reasonCode, string message) =>
        new()
        {
            Success = false,
            ReasonCode = reasonCode,
            Message = message,
        };
}
