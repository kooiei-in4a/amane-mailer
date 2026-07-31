using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Candidate Easy Setup release-bundle packaging (#455).
/// Distinct from Managed deployment metadata under <c>managed/</c>.
/// Packaging validation is separate from <see cref="TrustedReleaseInventory.ValidateShape"/>.
/// </summary>
public static class ReleaseBundlePackaging
{
    public const string PackagingKind = "setup-release-candidate";
    public const string ChecksumsFileName = "SHA256SUMS";
    public const string ReadmeSetupFileName = "README-SETUP.md";
    public const string EnvExampleFileName = ".env.example";
    public const string OciLayoutDirectoryName = "oci";
    public const string OciLayoutMarkerFileName = "oci-layout";
    public const string OciIndexFileName = "index.json";
    public const string ConfigDirectoryName = "config";
    public const string MailerConfigDirectoryName = "mailer";
    public const int ComposeBundleVersionValue = 1;

    public static readonly string[] SupportedHostRids =
    [
        "win-x64",
        "linux-x64",
        "linux-arm64",
    ];

    private static readonly Regex FullSha1OrSha256 = new(
        "^[a-fA-F0-9]{40}$|^[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ForbiddenBasenameExact =
    [
        ".env",
        "tenants.json",
        "secrets.env",
        "rclone.conf",
        "host-sealing-key",
    ];

    /// <summary>Inputs required to stage one host RID candidate tree (not an archive).</summary>
    public sealed class StageRequest
    {
        public required string OutputDirectory { get; init; }
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
        public string? MailpitImageReference { get; init; }
        public string? OciLayoutSourceDirectory { get; init; }
        public string ProjectNamePrefix { get; init; } = "amane";
        public int SupportedRecordedSchemaMin { get; init; } =
            SetupBundleLayout.MinimumSupportedRecordedSchemaVersion;
        public int SupportedRecordedSchemaMax { get; init; } =
            SetupBundleLayout.RecordedSchemaVersion;
        public int SupportedInspectEffectiveSchemaMin { get; init; } =
            SetupInspectEffectiveResult.CurrentSchemaVersion;
        public int SupportedInspectEffectiveSchemaMax { get; init; } =
            SetupInspectEffectiveResult.CurrentSchemaVersion;
    }

    public sealed class StageResult
    {
        public required bool Success { get; init; }
        public string? ReasonCode { get; init; }
        public string? Message { get; init; }
        public string? OutputDirectory { get; init; }
        public string? ManifestPath { get; init; }
        public string? ArtifactSha256 { get; init; }
        public ReleaseBundleManifestDocument? Manifest { get; init; }
    }

    public sealed class PackagingValidationResult
    {
        public required bool Success { get; init; }
        public string? ReasonCode { get; init; }
        public string? Message { get; init; }
    }

    public static bool IsSupportedHostRid(string? rid) =>
        !string.IsNullOrWhiteSpace(rid)
        && SupportedHostRids.Contains(rid, StringComparer.Ordinal);

    public static string HostBinaryFileName(string hostRid) =>
        string.Equals(hostRid, "win-x64", StringComparison.Ordinal)
            ? "Amane.Mailer.exe"
            : "Amane.Mailer";

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

    public static StageResult Stage(StageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsSupportedHostRid(request.HostRid))
        {
            return Fail("host_rid_unsupported", "Host RID is not supported for Easy Setup candidates.");
        }

        if (!FullSha1OrSha256.IsMatch(request.SourceCommitSha))
        {
            return Fail("source_commit_invalid", "Source commit SHA must be 40 or 64 hex characters.");
        }

        if (TrustedReleaseInventory.IsForbiddenDisplayTag(request.ImageDisplayTag))
        {
            return Fail("image_tag_forbidden", "Image display tag must not be latest or a placeholder.");
        }

        if (!TrustedReleaseInventory.IsValidDigest(request.OciIndexDigest))
        {
            return Fail("oci_index_digest_invalid", "OCI index digest must be sha256:<64 hex>.");
        }

        if (string.IsNullOrWhiteSpace(request.MailerVersion)
            || string.IsNullOrWhiteSpace(request.LauncherVersion)
            || string.IsNullOrWhiteSpace(request.ImageRepository))
        {
            return Fail("required_version_missing", "Mailer version, launcher version, and image repository are required.");
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

        string outputFull;
        try
        {
            outputFull = Path.GetFullPath(request.OutputDirectory);
        }
        catch
        {
            return Fail("output_path_invalid", "Output directory path could not be resolved.");
        }

        try
        {
            if (Directory.Exists(outputFull))
            {
                Directory.Delete(outputFull, recursive: true);
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

            CopyRequired(request.DeployComposePath, Path.Combine(outputFull, SetupDockerInventory.DeployComposeRelativePath));
            CopyRequired(
                request.ImageDigestOverlayPath,
                Path.Combine(outputFull, SetupDockerInventory.ImageDigestOverlayRelativePath));
            CopyRequired(
                request.RecordedMetadataOverlayPath,
                Path.Combine(outputFull, SetupDockerInventory.RecordedMetadataOverlayRelativePath));
            CopyRequired(
                request.MailpitOverlayPath,
                Path.Combine(outputFull, SetupDockerInventory.MailpitOverlayRelativePath));
            CopyRequired(request.EnvExamplePath, Path.Combine(outputFull, EnvExampleFileName));

            var configDir = Path.Combine(outputFull, ConfigDirectoryName, MailerConfigDirectoryName);
            Directory.CreateDirectory(configDir);
            CopyRequired(request.TenantsExamplePath, Path.Combine(configDir, "tenants.example.json"));
            CopyRequired(request.TenantsSchemaPath, Path.Combine(configDir, "tenants.schema.json"));
            CopyRequired(
                request.TenantsLocalAcsExamplePath,
                Path.Combine(configDir, "tenants.local-acs.json.example"));

            WriteReadmeSetup(outputFull, request);

            string? ociRelative = null;
            if (!string.IsNullOrWhiteSpace(request.OciLayoutSourceDirectory))
            {
                var ociSource = Path.GetFullPath(request.OciLayoutSourceDirectory);
                var ociValidation = ValidateOciLayoutDirectory(ociSource, request.OciIndexDigest);
                if (!ociValidation.Success)
                {
                    return Fail(ociValidation.ReasonCode!, ociValidation.Message!);
                }

                var ociDest = Path.Combine(outputFull, OciLayoutDirectoryName);
                CopyDirectory(ociSource, ociDest);
                ociRelative = OciLayoutDirectoryName;
            }

            var composeSha = DigestFile(Path.Combine(outputFull, SetupDockerInventory.DeployComposeRelativePath));
            var imageDigestOverlaySha = DigestFile(
                Path.Combine(outputFull, SetupDockerInventory.ImageDigestOverlayRelativePath));
            var recordedOverlaySha = DigestFile(
                Path.Combine(outputFull, SetupDockerInventory.RecordedMetadataOverlayRelativePath));
            var mailpitOverlaySha = DigestFile(
                Path.Combine(outputFull, SetupDockerInventory.MailpitOverlayRelativePath));

            var manifest = new ReleaseBundleManifestDocument
            {
                SchemaVersion = TrustedReleaseInventory.CurrentSchemaVersion,
                PackagingKind = PackagingKind,
                SourceCommitSha = request.SourceCommitSha.ToLowerInvariant(),
                MailerVersion = request.MailerVersion,
                HostRid = request.HostRid,
                ImageRepository = request.ImageRepository,
                ImageDigest = request.OciIndexDigest.ToLowerInvariant(),
                ImageTag = request.ImageDisplayTag,
                OciIndexDigest = request.OciIndexDigest.ToLowerInvariant(),
                OciLayoutRelativePath = ociRelative,
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
                ArtifactFileName = ArchiveFileName(request.MailerVersion, request.HostRid),
                Reproducibility =
                    "Same source commit SHA, Dockerfile base digests, publish flags, and OCI layout inputs "
                    + "must produce the same OCI index digest and the same staged payload SHA-256 "
                    + "(excluding archive container metadata). Candidate packaging never pushes to GHCR.",
            };

            var packagingValidation = ValidatePackagingDocument(manifest);
            if (!packagingValidation.Success)
            {
                return Fail(packagingValidation.ReasonCode!, packagingValidation.Message!);
            }

            // Runtime inventory shape must also hold so staged manifests load via host Docker resolver.
            var inventory = ToInventory(manifest);
            var shape = inventory.ValidateShape();
            if (shape is not null)
            {
                return Fail("inventory_shape_invalid", shape.Message ?? "Trusted inventory shape invalid.");
            }

            var secretScan = ScanStagedTreeForSecrets(outputFull);
            if (!secretScan.Success)
            {
                return Fail(secretScan.ReasonCode!, secretScan.Message!);
            }

            var manifestPath = Path.Combine(outputFull, TrustedReleaseInventory.ManifestFileName);
            var manifestJson = JsonSerializer.Serialize(
                manifest,
                SetupHostDockerJsonContext.Default.ReleaseBundleManifestDocument);
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var payloadSha = ComputePayloadSha256(outputFull, excludeFileNames: [ChecksumsFileName]);
            manifest = new ReleaseBundleManifestDocument
            {
                SchemaVersion = manifest.SchemaVersion,
                PackagingKind = manifest.PackagingKind,
                SourceCommitSha = manifest.SourceCommitSha,
                MailerVersion = manifest.MailerVersion,
                HostRid = manifest.HostRid,
                ImageRepository = manifest.ImageRepository,
                ImageDigest = manifest.ImageDigest,
                ImageTag = manifest.ImageTag,
                OciIndexDigest = manifest.OciIndexDigest,
                OciLayoutRelativePath = manifest.OciLayoutRelativePath,
                ComposeBundleVersion = manifest.ComposeBundleVersion,
                ComposeSha256 = manifest.ComposeSha256,
                ComposeImageDigestSha256 = manifest.ComposeImageDigestSha256,
                ComposeRecordedMetadataSha256 = manifest.ComposeRecordedMetadataSha256,
                ComposeMailpitSha256 = manifest.ComposeMailpitSha256,
                LauncherVersionMin = manifest.LauncherVersionMin,
                LauncherVersionMax = manifest.LauncherVersionMax,
                ProjectNamePrefix = manifest.ProjectNamePrefix,
                MailpitImageReference = manifest.MailpitImageReference,
                SupportedRecordedSchemaMin = manifest.SupportedRecordedSchemaMin,
                SupportedRecordedSchemaMax = manifest.SupportedRecordedSchemaMax,
                SupportedInspectEffectiveSchemaMin = manifest.SupportedInspectEffectiveSchemaMin,
                SupportedInspectEffectiveSchemaMax = manifest.SupportedInspectEffectiveSchemaMax,
                ArtifactFileName = manifest.ArtifactFileName,
                ArtifactSha256 = payloadSha,
                Reproducibility = manifest.Reproducibility,
            };

            packagingValidation = ValidatePackagingDocument(manifest);
            if (!packagingValidation.Success)
            {
                return Fail(packagingValidation.ReasonCode!, packagingValidation.Message!);
            }

            manifestJson = JsonSerializer.Serialize(
                manifest,
                SetupHostDockerJsonContext.Default.ReleaseBundleManifestDocument);
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            WriteChecksumsFile(outputFull);

            return new StageResult
            {
                Success = true,
                OutputDirectory = outputFull,
                ManifestPath = manifestPath,
                ArtifactSha256 = payloadSha,
                Manifest = manifest,
            };
        }
        catch (Exception ex)
        {
            return Fail("stage_failed", "Release bundle staging failed: " + ex.GetType().Name);
        }
    }

    public static PackagingValidationResult ValidatePackagingDocument(ReleaseBundleManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != TrustedReleaseInventory.CurrentSchemaVersion)
        {
            return PackagingFail("schema_version_unsupported", "release-bundle-manifest schemaVersion must be 1.");
        }

        if (!string.Equals(document.PackagingKind, PackagingKind, StringComparison.Ordinal))
        {
            return PackagingFail("packaging_kind_invalid", "packagingKind must be setup-release-candidate.");
        }

        if (string.IsNullOrWhiteSpace(document.SourceCommitSha)
            || !FullSha1OrSha256.IsMatch(document.SourceCommitSha))
        {
            return PackagingFail("source_commit_invalid", "sourceCommitSha is required and must be hex SHA.");
        }

        if (string.IsNullOrWhiteSpace(document.MailerVersion)
            || string.IsNullOrWhiteSpace(document.HostRid)
            || !IsSupportedHostRid(document.HostRid))
        {
            return PackagingFail("host_metadata_invalid", "mailerVersion and a supported hostRid are required.");
        }

        if (!TrustedReleaseInventory.IsValidDigest(document.OciIndexDigest)
            || !TrustedReleaseInventory.IsValidDigest(document.ImageDigest)
            || !string.Equals(document.OciIndexDigest, document.ImageDigest, StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "oci_digest_mismatch",
                "ociIndexDigest must equal imageDigest and be a valid sha256 digest.");
        }

        if (TrustedReleaseInventory.IsForbiddenDisplayTag(document.ImageTag))
        {
            return PackagingFail("image_tag_forbidden", "imageTag must not be latest or a placeholder.");
        }

        if (document.SupportedRecordedSchemaMin is null
            || document.SupportedRecordedSchemaMax is null
            || document.SupportedInspectEffectiveSchemaMin is null
            || document.SupportedInspectEffectiveSchemaMax is null)
        {
            return PackagingFail("schema_range_missing", "Supported schema ranges are required for packaging.");
        }

        var recordedMin = document.SupportedRecordedSchemaMin.Value;
        var recordedMax = document.SupportedRecordedSchemaMax.Value;
        var inspectMin = document.SupportedInspectEffectiveSchemaMin.Value;
        var inspectMax = document.SupportedInspectEffectiveSchemaMax.Value;
        if (recordedMin > recordedMax || inspectMin > inspectMax)
        {
            return PackagingFail("schema_range_invalid", "Supported schema ranges must be ordered min <= max.");
        }

        if (!string.IsNullOrWhiteSpace(document.ArtifactSha256)
            && !TrustedReleaseInventory.IsValidDigest(document.ArtifactSha256))
        {
            return PackagingFail("artifact_sha_invalid", "artifactSha256 must be sha256:<64 hex> when set.");
        }

        if (!string.IsNullOrWhiteSpace(document.OciLayoutRelativePath)
            && (document.OciLayoutRelativePath.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(document.OciLayoutRelativePath)))
        {
            return PackagingFail("oci_layout_path_unsafe", "ociLayoutRelativePath must be a relative safe path.");
        }

        return new PackagingValidationResult { Success = true };
    }

    public static PackagingValidationResult ValidateOciLayoutDirectory(
        string ociLayoutDirectory,
        string expectedIndexDigest)
    {
        if (string.IsNullOrWhiteSpace(ociLayoutDirectory) || !Directory.Exists(ociLayoutDirectory))
        {
            return PackagingFail("oci_layout_missing", "OCI layout directory is missing.");
        }

        var layoutMarker = Path.Combine(ociLayoutDirectory, OciLayoutMarkerFileName);
        var indexPath = Path.Combine(ociLayoutDirectory, OciIndexFileName);
        if (!File.Exists(layoutMarker) || !File.Exists(indexPath))
        {
            return PackagingFail(
                "oci_layout_incomplete",
                "OCI layout must contain oci-layout and index.json (B1).");
        }

        if (!TrustedReleaseInventory.IsValidDigest(expectedIndexDigest))
        {
            return PackagingFail("oci_index_digest_invalid", "Expected OCI index digest is invalid.");
        }

        // Pinning uses the digest of the index.json bytes (canonical candidate evidence).
        var actual = DigestFile(indexPath);
        if (!string.Equals(actual, expectedIndexDigest, StringComparison.OrdinalIgnoreCase))
        {
            return PackagingFail(
                "oci_index_digest_mismatch",
                "OCI index.json digest does not match the declared ociIndexDigest.");
        }

        var blobs = Path.Combine(ociLayoutDirectory, "blobs", "sha256");
        if (!Directory.Exists(blobs))
        {
            return PackagingFail("oci_blobs_missing", "OCI layout blobs/sha256 directory is missing.");
        }

        return new PackagingValidationResult { Success = true };
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
        }

        return new PackagingValidationResult { Success = true };
    }

    public static string DigestFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string ComputePayloadSha256(string stagedRoot, IReadOnlyList<string>? excludeFileNames = null)
    {
        excludeFileNames ??= [];
        var rootFull = Path.GetFullPath(stagedRoot);
        var entries = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/'),
            })
            .Where(e => !excludeFileNames.Contains(Path.GetFileName(e.Path), StringComparer.Ordinal))
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

    public static TrustedReleaseInventory ToInventory(ReleaseBundleManifestDocument document) =>
        new()
        {
            AllowedImageRepository = document.ImageRepository ?? string.Empty,
            RequiredImageDigest = document.ImageDigest ?? string.Empty,
            AllowedDisplayTag = document.ImageTag ?? string.Empty,
            ComposeBundleVersion = document.ComposeBundleVersion ?? string.Empty,
            ComposeSha256 = document.ComposeSha256,
            ComposeImageDigestSha256 = document.ComposeImageDigestSha256,
            ComposeRecordedMetadataSha256 = document.ComposeRecordedMetadataSha256,
            ComposeMailpitSha256 = document.ComposeMailpitSha256,
            LauncherVersionMin = document.LauncherVersionMin ?? string.Empty,
            LauncherVersionMax = document.LauncherVersionMax ?? string.Empty,
            ProjectNamePrefix = document.ProjectNamePrefix ?? "amane",
            MailpitImageReference = document.MailpitImageReference,
            MailerVersion = document.MailerVersion,
            SourceCommitSha = document.SourceCommitSha,
        };

    private static void WriteChecksumsFile(string stagedRoot)
    {
        var rootFull = Path.GetFullPath(stagedRoot);
        var lines = Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
                if (string.Equals(Path.GetFileName(path), ChecksumsFileName, StringComparison.Ordinal))
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
        var content =
            $"""
            # Amane Mailer Easy Setup candidate bundle

            This directory is a **release-candidate** Easy Setup host bundle (#455).
            It is not a GitHub Release artifact and must not be treated as published.

            - Host RID: `{request.HostRid}`
            - Mailer version: `{request.MailerVersion}`
            - Source commit: `{request.SourceCommitSha}`
            - Image repository: `{request.ImageRepository}`
            - Display tag: `{request.ImageDisplayTag}`
            - OCI index digest: `{request.OciIndexDigest}`

            ## Distinctions

            - `release-bundle-manifest.json` describes this **distribution** candidate
              (versions, source SHA, OCI index digest, schema ranges, checksums).
            - Managed deployment metadata under operator `managed/` (ACTIVE pointer,
              recorded.json, verification) is created later on the host and is a
              different concept. Do not merge the two.

            ## Start setup

            From this directory:

            ```text
            ./{HostBinaryFileName(request.HostRid)} setup assistant --help
            ./{HostBinaryFileName(request.HostRid)} --help
            ```

            Setup never pulls `latest`. Compose uses the digest-pinned overlay
            (`compose.image-digest.yml`) so runtime references the immutable digest.

            ## Upgrade

            Product upgrade / publish is owned by later release issues (#458). This
            candidate package is for qualification (#456) only.

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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
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
            // Best-effort on restricted FS; smoke scripts can chmod.
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

/// <summary>
/// Wire DTO for <c>release-bundle-manifest.json</c>.
/// schemaVersion stays 1; packaging fields are additive (#455).
/// Runtime host Docker continues to consume the inventory subset via
/// <see cref="TrustedReleaseInventory"/>.
/// </summary>
public sealed class ReleaseBundleManifestDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("packagingKind")]
    public string? PackagingKind { get; init; }

    [JsonPropertyName("sourceCommitSha")]
    public string? SourceCommitSha { get; init; }

    [JsonPropertyName("mailerVersion")]
    public string? MailerVersion { get; init; }

    [JsonPropertyName("hostRid")]
    public string? HostRid { get; init; }

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

    [JsonPropertyName("artifactSha256")]
    public string? ArtifactSha256 { get; init; }

    [JsonPropertyName("reproducibility")]
    public string? Reproducibility { get; init; }
}
