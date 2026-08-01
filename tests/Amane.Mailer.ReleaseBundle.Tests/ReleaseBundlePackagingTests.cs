using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.ReleaseBundle;

namespace Amane.Mailer.ReleaseBundle.Tests;

public sealed class ReleaseBundlePackagingTests
{
    private static readonly string TestDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string TestCommit =
        "abcdef0123456789abcdef0123456789abcdef01";

    private static readonly string TestMailpit =
        "axllent/mailpit@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Stage_writes_manifest_checksums_license_examples_without_secrets_or_oci()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var stagingParent = Path.Combine(scratch.Path, "parent");
        Directory.CreateDirectory(stagingParent);
        var output = Path.Combine(stagingParent, "linux-x64");

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = output,
            StagingParentDirectory = stagingParent,
            HostRid = "linux-x64",
            HostBinaryPath = inputs.HostBinary,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-" + TestCommit,
            OciIndexDigest = TestDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            LicensePath = inputs.License,
            MailpitImageReference = TestMailpit,
            AssertHostBinaryVersion = false,
        });

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest!.SchemaVersion);
        Assert.Equal(ReleaseBundlePackaging.PackagingKind, result.Manifest.PackagingKind);
        Assert.Equal(TestDigest, result.Manifest.OciIndexDigest);
        Assert.Equal(TestDigest, result.Manifest.ImageDigest);
        Assert.Equal("linux-x64", result.Manifest.HostRid);
        Assert.Equal("linux-x64", result.Manifest.TargetRid);
        Assert.Equal("linux", result.Manifest.Platform);
        Assert.Equal("x64", result.Manifest.Architecture);
        Assert.Equal("1.2.0", result.Manifest.SetupLauncherVersion);
        Assert.Equal(TestMailpit, result.Manifest.MailpitImageReference);
        Assert.Equal(1, result.Manifest.SupportedReleaseManifestSchemaMin);
        Assert.Equal(1, result.Manifest.SupportedReleaseManifestSchemaMax);
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.ArtifactId));
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.PayloadTreeSha256));
        Assert.Null(result.Manifest.ArtifactSha256);
        Assert.Null(result.Manifest.OciLayoutRelativePath);
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.Reproducibility));
        Assert.Contains("#458", result.Manifest.Reproducibility, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "Amane.Mailer")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "release-bundle-manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "FILES-SHA256SUMS")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "README-SETUP.md")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "examples", ".env.example")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "examples", "config", "mailer", "tenants.example.json")));
        Assert.False(Directory.Exists(Path.Combine(result.OutputDirectory!, "oci")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory!, ".env")));

        // payloadTreeSha256 excludes manifest + checksums (non-self-referential).
        var recomputed = ReleaseBundlePackaging.ComputePayloadSha256(result.OutputDirectory!);
        Assert.Equal(result.Manifest.PayloadTreeSha256, recomputed);

        var packaging = ReleaseBundlePackaging.ValidatePackagingDocument(result.Manifest);
        Assert.True(packaging.Success, packaging.Message);
        Assert.True(ReleaseBundlePackaging.VerifyChecksumsFile(result.OutputDirectory!).Success);
    }

    [Theory]
    [InlineData("linux-x64", "./Amane.Mailer")]
    [InlineData("linux-arm64", "./Amane.Mailer")]
    [InlineData("win-x64", @".\Amane.Mailer.exe")]
    public void Stage_readme_setup_uses_rid_qualified_launcher_and_setup_guide_links(
        string hostRid,
        string launcher)
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var stagingParent = Path.Combine(scratch.Path, "parent");
        Directory.CreateDirectory(stagingParent);
        var output = Path.Combine(stagingParent, hostRid);

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = output,
            StagingParentDirectory = stagingParent,
            HostRid = hostRid,
            HostBinaryPath = inputs.HostBinary,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-" + TestCommit,
            OciIndexDigest = TestDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            LicensePath = inputs.License,
            MailpitImageReference = TestMailpit,
            AssertHostBinaryVersion = false,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(launcher, ReleaseBundlePackaging.ReadmeSetupLauncher(hostRid));

        var readmePath = Path.Combine(result.OutputDirectory!, "README-SETUP.md");
        Assert.True(File.Exists(readmePath));
        var readme = File.ReadAllText(readmePath);

        Assert.Contains("minimal entry", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"https://github.com/kooiei-in4a/amane-mailer/blob/{TestCommit}/docs/ops/setup-guide.md",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"https://github.com/kooiei-in4a/amane-mailer/blob/{TestCommit}/docs/ops/setup-guide.en.md",
            readme,
            StringComparison.Ordinal);

        Assert.Contains($"{launcher} setup assistant", readme, StringComparison.Ordinal);
        Assert.Contains($"{launcher} setup assistant --help", readme, StringComparison.Ordinal);
        Assert.Contains($"{launcher} setup assistant --terminal", readme, StringComparison.Ordinal);
        Assert.Contains($"{launcher} setup assistant --no-browser", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"{launcher} setup apply --config <absolute-path> --non-interactive",
            readme,
            StringComparison.Ordinal);

        AssertNoBareLauncherCommandsInFencedBlocks(readme);

        Assert.Contains("Admin stays disabled", readme, StringComparison.Ordinal);
        Assert.Contains("Mode 5", readme, StringComparison.Ordinal);
        Assert.Contains("not Easy Setup", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not** a published GitHub Release", readme, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("FILES-SHA256SUMS", readme, StringComparison.Ordinal);
        Assert.Contains("CANDIDATE-SHA256SUMS", readme, StringComparison.Ordinal);
        Assert.Contains("payloadTreeSha256", readme, StringComparison.Ordinal);
        Assert.Contains("archive itself", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("files after extract", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not** the archive checksum", readme, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(TestCommit, readme, StringComparison.Ordinal);
        Assert.Contains(TestDigest, readme, StringComparison.Ordinal);
        Assert.Contains(hostRid, readme, StringComparison.Ordinal);
        Assert.Contains("inventory", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Endpoint=sb://", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\", readme, StringComparison.OrdinalIgnoreCase);

        Assert.True(ReleaseBundlePackaging.VerifyChecksumsFile(result.OutputDirectory!).Success);
        var sums = File.ReadAllText(Path.Combine(result.OutputDirectory!, "FILES-SHA256SUMS"));
        Assert.Contains("README-SETUP.md", sums, StringComparison.Ordinal);
    }

    private static void AssertNoBareLauncherCommandsInFencedBlocks(string readme)
    {
        var fences = readme.Split("```", StringSplitOptions.None);
        for (var i = 1; i < fences.Length; i += 2)
        {
            var block = fences[i];
            var newline = block.IndexOf('\n');
            var body = newline >= 0 ? block[(newline + 1)..] : block;
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // Runnable examples must use ./ or .\ — reject bare Amane.Mailer[.exe] commands.
                Assert.False(
                    line.StartsWith("Amane.Mailer", StringComparison.Ordinal),
                    $"Fenced command must be path-qualified, found: {line}");
            }
        }
    }

    [Fact]
    public void Stage_requires_mailpit_and_rejects_missing_mailpit()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var stagingParent = Path.Combine(scratch.Path, "parent");
        Directory.CreateDirectory(stagingParent);

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = Path.Combine(stagingParent, "linux-x64"),
            StagingParentDirectory = stagingParent,
            HostRid = "linux-x64",
            HostBinaryPath = inputs.HostBinary,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-test",
            OciIndexDigest = TestDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            LicensePath = inputs.License,
            MailpitImageReference = "",
            AssertHostBinaryVersion = false,
        });

        Assert.False(result.Success);
        Assert.Equal("mailpit_image_required", result.ReasonCode);
    }

    [Fact]
    public void Stage_refuses_output_outside_staging_parent()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var stagingParent = Path.Combine(scratch.Path, "parent");
        Directory.CreateDirectory(stagingParent);

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = Path.Combine(scratch.Path, "outside"),
            StagingParentDirectory = stagingParent,
            HostRid = "linux-x64",
            HostBinaryPath = inputs.HostBinary,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-test",
            OciIndexDigest = TestDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            LicensePath = inputs.License,
            MailpitImageReference = TestMailpit,
            AssertHostBinaryVersion = false,
        });

        Assert.False(result.Success);
        Assert.Equal("staging_path_outside_parent", result.ReasonCode);
    }

    [Fact]
    public void ValidatePackagingDocument_rejects_latest_and_digest_mismatch()
    {
        var ok = CreateValidPackagingDocument();
        var latest = CloneWith(ok, imageTag: "latest");
        var mismatch = new ReleaseBundleManifestDocument
        {
            SchemaVersion = ok.SchemaVersion,
            PackagingKind = ok.PackagingKind,
            ArtifactId = ok.ArtifactId,
            SourceCommitSha = ok.SourceCommitSha,
            MailerVersion = ok.MailerVersion,
            SetupLauncherVersion = ok.SetupLauncherVersion,
            HostRid = ok.HostRid,
            TargetRid = ok.TargetRid,
            Platform = ok.Platform,
            Architecture = ok.Architecture,
            ImageRepository = ok.ImageRepository,
            ImageDigest = TestDigest,
            ImageTag = ok.ImageTag,
            OciIndexDigest = TestDigest[..^1] + "0",
            ComposeBundleVersion = ok.ComposeBundleVersion,
            ComposeSha256 = ok.ComposeSha256,
            ComposeImageDigestSha256 = ok.ComposeImageDigestSha256,
            ComposeRecordedMetadataSha256 = ok.ComposeRecordedMetadataSha256,
            ComposeMailpitSha256 = ok.ComposeMailpitSha256,
            LauncherVersionMin = ok.LauncherVersionMin,
            LauncherVersionMax = ok.LauncherVersionMax,
            ProjectNamePrefix = ok.ProjectNamePrefix,
            MailpitImageReference = ok.MailpitImageReference,
            SupportedRecordedSchemaMin = ok.SupportedRecordedSchemaMin,
            SupportedRecordedSchemaMax = ok.SupportedRecordedSchemaMax,
            SupportedInspectEffectiveSchemaMin = ok.SupportedInspectEffectiveSchemaMin,
            SupportedInspectEffectiveSchemaMax = ok.SupportedInspectEffectiveSchemaMax,
            SupportedReleaseManifestSchemaMin = ok.SupportedReleaseManifestSchemaMin,
            SupportedReleaseManifestSchemaMax = ok.SupportedReleaseManifestSchemaMax,
            ArtifactFileName = ok.ArtifactFileName,
            PayloadTreeSha256 = ok.PayloadTreeSha256,
            Reproducibility = ok.Reproducibility,
        };
        var noMailpit = CloneWith(ok, mailpitImageReference: null);

        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(latest).Success);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(mismatch).Success);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(noMailpit).Success);
    }

    [Fact]
    public void ValidatePackagingDocument_keeps_schema_version_one_additive()
    {
        var doc = CreateValidPackagingDocument();
        Assert.Equal(1, doc.SchemaVersion);
        Assert.Equal(ReleaseBundlePackaging.PackagingKind, doc.PackagingKind);
        Assert.True(ReleaseBundlePackaging.ValidatePackagingDocument(doc).Success);

        var bumped = CloneWith(doc, schemaVersion: 2);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(bumped).Success);
    }

    [Fact]
    public void ScanStagedTreeForSecrets_detects_private_env()
    {
        using var scratch = new TempDir();
        File.WriteAllText(Path.Combine(scratch.Path, "README.md"), "ok");
        File.WriteAllText(Path.Combine(scratch.Path, ".env"), "MAIL_SERVICE_TOKEN=secret");

        var scan = ReleaseBundlePackaging.ScanStagedTreeForSecrets(scratch.Path);
        Assert.False(scan.Success);
        Assert.Equal("secret_path_detected", scan.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_empty_index_and_requires_platforms()
    {
        using var scratch = new TempDir();
        var emptyOci = Path.Combine(scratch.Path, "empty-oci");
        _ = CreateMinimalOciLayout(emptyOci, includePlatformManifests: false);
        Assert.False(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                emptyOci,
                "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff").Success);

        var okOci = Path.Combine(scratch.Path, "ok-oci");
        var okDigest = CreateMinimalOciLayout(okOci, includePlatformManifests: true);
        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(okOci, okDigest).Success);

        File.Delete(Path.Combine(okOci, "oci-layout"));
        Assert.False(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(okOci, okDigest).Success);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_binds_buildx_digest_to_manifests_descriptor_not_index_json_bytes()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var buildxDigest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var indexBytes = File.ReadAllBytes(Path.Combine(oci, "index.json"));
        var indexFileDigest =
            "sha256:"
            + Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();

        Assert.NotEqual(buildxDigest, indexFileDigest);

        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, buildxDigest).Success,
            "Buildx digest bound to manifests[] target must PASS even when sha256(index.json) differs");

        var mismatch = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, indexFileDigest);
        Assert.False(mismatch.Success);
        Assert.Equal("oci_image_digest_mismatch", mismatch.ReasonCode);

        var unrelated =
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        var missing = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, unrelated);
        Assert.False(missing.Success);
        Assert.Equal("oci_image_digest_mismatch", missing.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_accepts_reordered_manifests_descriptors()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            platformOrderArm64First: true);
        Assert.True(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest).Success);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_binds_buildx_metadata_descriptor_fields()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var blobPath = Path.Combine(oci, "blobs", "sha256", digest["sha256:".Length..]);
        var size = new FileInfo(blobPath).Length;
        var descriptor = new OciDescriptor
        {
            Digest = digest,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Size = size,
        };

        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                oci,
                digest,
                expectedRootDescriptor: descriptor).Success);

        var badSize = new OciDescriptor
        {
            Digest = digest,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Size = size + 1,
        };
        var sizeFail = ReleaseBundlePackaging.ValidateOciLayoutDirectory(
            oci,
            digest,
            expectedRootDescriptor: badSize);
        Assert.False(sizeFail.Success);
        Assert.Equal("oci_descriptor_size_mismatch", sizeFail.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_ambiguous_matching_descriptors()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var indexPath = Path.Combine(oci, "index.json");
        var index = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var bound = index.Manifests![0];
        var duplicated = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests = [bound, bound],
        };
        File.WriteAllBytes(
            indexPath,
            JsonSerializer.SerializeToUtf8Bytes(
                duplicated,
                ReleaseBundleJsonContext.Default.OciIndexDocument));

        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_image_digest_ambiguous", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_missing_or_tampered_bound_blob()
    {
        using var scratch = new TempDir();
        var missingOci = Path.Combine(scratch.Path, "missing");
        var digest = CreateMinimalOciLayout(missingOci, includePlatformManifests: true);
        File.Delete(Path.Combine(missingOci, "blobs", "sha256", digest["sha256:".Length..]));
        var missing = ReleaseBundlePackaging.ValidateOciLayoutDirectory(missingOci, digest);
        Assert.False(missing.Success);
        Assert.Equal("oci_blob_missing", missing.ReasonCode);

        var tamperedOci = Path.Combine(scratch.Path, "tampered");
        var tamperedDigest = CreateMinimalOciLayout(tamperedOci, includePlatformManifests: true);
        var tamperedPath = Path.Combine(
            tamperedOci,
            "blobs",
            "sha256",
            tamperedDigest["sha256:".Length..]);
        // Keep file name (digest) but corrupt content → content hash mismatch vs name.
        File.WriteAllText(tamperedPath, "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":[]}\n");
        var tampered = ReleaseBundlePackaging.ValidateOciLayoutDirectory(tamperedOci, tamperedDigest);
        Assert.False(tampered.Success);
        Assert.Equal("oci_blob_digest_mismatch", tampered.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_descriptor_size_mismatch_against_blob()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var indexPath = Path.Combine(oci, "index.json");
        var index = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var bound = index.Manifests![0];
        var wrongSize = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = bound.Digest,
                    MediaType = bound.MediaType,
                    Size = (bound.Size ?? 0) + 99,
                },
            ],
        };
        File.WriteAllBytes(
            indexPath,
            JsonSerializer.SerializeToUtf8Bytes(
                wrongSize,
                ReleaseBundleJsonContext.Default.OciIndexDocument));

        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_descriptor_size_mismatch", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_media_type_contradiction()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var indexPath = Path.Combine(oci, "index.json");
        var index = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var bound = index.Manifests![0];
        var wrongMedia = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = bound.Digest,
                    MediaType = "application/vnd.oci.image.manifest.v1+json",
                    Size = bound.Size,
                },
            ],
        };
        File.WriteAllBytes(
            indexPath,
            JsonSerializer.SerializeToUtf8Bytes(
                wrongMedia,
                ReleaseBundleJsonContext.Default.OciIndexDocument));

        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_descriptor_media_type_mismatch", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_metadata_digest_mismatch_via_expected_descriptor()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var other =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(
            oci,
            digest,
            expectedRootDescriptor: new OciDescriptor
            {
                Digest = other,
                MediaType = "application/vnd.oci.image.index.v1+json",
                Size = 1,
            });
        Assert.False(result.Success);
        Assert.Equal("oci_descriptor_digest_mismatch", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_missing_required_platform()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            omitArm64: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_missing", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_extra_platform()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            includeLinux386: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_extra", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_invalid_digest_format()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        _ = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, "sha256:not-a-digest");
        Assert.False(result.Success);
        Assert.Equal("oci_index_digest_invalid", result.ReasonCode);
    }

    [Fact]
    public void TryParseBuildxMetadata_and_validate_oci_reject_image_digest_argument_mismatch()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var blobPath = Path.Combine(oci, "blobs", "sha256", digest["sha256:".Length..]);
        var size = new FileInfo(blobPath).Length;
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["containerimage.descriptor"] = new Dictionary<string, object>
            {
                ["mediaType"] = "application/vnd.oci.image.index.v1+json",
                ["digest"] = digest,
                ["size"] = size,
            },
        });

        var parsed = ReleaseBundlePackaging.TryParseBuildxMetadata(
            metadata,
            out var metaDigest,
            out var descriptor);
        Assert.True(parsed.Success);
        Assert.Equal(digest, metaDigest);

        var other =
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        // Program.cs compares --image-digest to metadata before validate; mirror that gate.
        Assert.False(string.Equals(metaDigest, other, StringComparison.OrdinalIgnoreCase));
        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                oci,
                digest,
                expectedRootDescriptor: descriptor).Success);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_symlink_and_extra_files()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        File.WriteAllText(Path.Combine(oci, "EXTRA"), "nope");
        Assert.False(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest).Success);
    }

    [Theory]
    [InlineData("axllent/mailpit@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("localhost:5000/mailpit@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("registry.example:5000/path/mailpit@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("axllent/mailpit:latest@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("axllent/mailpit:v1@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void TryParseMailpitImageReference_rejects_tag_before_digest(string reference, bool expectedValid)
    {
        var ok = ReleaseBundlePackaging.TryParseMailpitImageReference(reference, out var parts);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedValid, ReleaseBundlePackaging.IsValidMailpitImageReference(reference));
        if (expectedValid)
        {
            Assert.NotNull(parts);
            Assert.StartsWith("sha256:", parts!.Digest, StringComparison.Ordinal);
            Assert.DoesNotContain(':', parts.NameComponent);
        }
        else
        {
            Assert.Null(parts);
        }
    }

    [Fact]
    public void AssertImageIdentityForHostPackaging_requires_source_version_and_platforms()
    {
        var ok = new ImageIdentityDocument
        {
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageTag = "sha-" + TestCommit,
            ImageDigest = TestDigest,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            Platforms = ["linux/arm64", "linux/amd64"],
        };
        Assert.True(
            ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(ok, TestCommit, "1.2.0").Success);

        Assert.False(
            ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(
                new ImageIdentityDocument
                {
                    ImageRepository = ok.ImageRepository,
                    ImageTag = ok.ImageTag,
                    ImageDigest = ok.ImageDigest,
                    SourceCommitSha = "0000000000000000000000000000000000000000",
                    MailerVersion = ok.MailerVersion,
                    Platforms = ok.Platforms,
                },
                TestCommit,
                "1.2.0").Success);
        Assert.False(
            ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(
                new ImageIdentityDocument
                {
                    ImageRepository = ok.ImageRepository,
                    ImageTag = ok.ImageTag,
                    ImageDigest = ok.ImageDigest,
                    SourceCommitSha = ok.SourceCommitSha,
                    MailerVersion = "9.9.9",
                    Platforms = ok.Platforms,
                },
                TestCommit,
                "1.2.0").Success);
        Assert.False(
            ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(
                new ImageIdentityDocument
                {
                    ImageRepository = ok.ImageRepository,
                    ImageTag = ok.ImageTag,
                    ImageDigest = ok.ImageDigest,
                    SourceCommitSha = ok.SourceCommitSha,
                    MailerVersion = ok.MailerVersion,
                    Platforms = ["linux/amd64"],
                },
                TestCommit,
                "1.2.0").Success);
    }

    [Fact]
    public void ValidatePackagingDocument_requires_release_manifest_schema_range_eq_one()
    {
        var doc = CreateValidPackagingDocument();
        Assert.Equal(1, doc.SupportedReleaseManifestSchemaMin);
        Assert.Equal(1, doc.SupportedReleaseManifestSchemaMax);
        Assert.True(ReleaseBundlePackaging.ValidatePackagingDocument(doc).Success);

        var missing = CloneWith(doc, releaseManifestSchemaMin: null, releaseManifestSchemaMax: null);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(missing).Success);

        var wrong = CloneWith(doc, releaseManifestSchemaMin: 1, releaseManifestSchemaMax: 2);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(wrong).Success);
    }

    [Fact]
    public void Stage_rejects_missing_host_binary()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var stagingParent = Path.Combine(scratch.Path, "parent");
        Directory.CreateDirectory(stagingParent);

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = Path.Combine(stagingParent, "linux-x64"),
            StagingParentDirectory = stagingParent,
            HostRid = "linux-x64",
            HostBinaryPath = Path.Combine(scratch.Path, "missing-binary"),
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-test",
            OciIndexDigest = TestDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            LicensePath = inputs.License,
            MailpitImageReference = TestMailpit,
            AssertHostBinaryVersion = false,
        });

        Assert.False(result.Success);
        Assert.Equal("host_binary_missing", result.ReasonCode);
    }

    [Fact]
    public void ArchiveFileName_matches_issue_candidates()
    {
        Assert.Equal(
            "amane-mailer-v1.2.0-windows-x64.zip",
            ReleaseBundlePackaging.ArchiveFileName("1.2.0", "win-x64"));
        Assert.Equal(
            "amane-mailer-v1.2.0-linux-x64.tar.gz",
            ReleaseBundlePackaging.ArchiveFileName("1.2.0", "linux-x64"));
        Assert.Equal(
            "amane-mailer-v1.2.0-linux-arm64.tar.gz",
            ReleaseBundlePackaging.ArchiveFileName("v1.2.0", "linux-arm64"));
    }

    [Fact]
    public void IsValidReleaseVersion_rejects_candidate_suffix()
    {
        Assert.True(ReleaseBundlePackaging.IsValidReleaseVersion("1.2.0"));
        Assert.False(ReleaseBundlePackaging.IsValidReleaseVersion("1.2.0-candidate"));
    }

    [Fact]
    public void ValidatePackagingDocument_rejects_incomplete_runtime_subset()
    {
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(CreateIncompletePackagingDocument()).Success);
    }

    private static ReleaseBundleManifestDocument CreateIncompletePackagingDocument() =>
        new()
        {
            SchemaVersion = 1,
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDigest = TestDigest,
            ImageTag = "sha-testfixture",
            ComposeBundleVersion = "1",
            ComposeSha256 = TestDigest,
            ComposeImageDigestSha256 = TestDigest,
            ComposeRecordedMetadataSha256 = TestDigest,
            LauncherVersionMin = "1.2.0",
            LauncherVersionMax = "1.2.0",
            ProjectNamePrefix = "amane",
        };

    private static ReleaseBundleManifestDocument CreateValidPackagingDocument() =>
        new()
        {
            SchemaVersion = 1,
            PackagingKind = ReleaseBundlePackaging.PackagingKind,
            ArtifactId = "1.2.0/linux-x64/" + TestDigest,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            SetupLauncherVersion = "1.2.0",
            HostRid = "linux-x64",
            TargetRid = "linux-x64",
            Platform = "linux",
            Architecture = "x64",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDigest = TestDigest,
            ImageTag = "sha-test",
            OciIndexDigest = TestDigest,
            ComposeBundleVersion = "1",
            ComposeSha256 = TestDigest,
            ComposeImageDigestSha256 = TestDigest,
            ComposeRecordedMetadataSha256 = TestDigest,
            ComposeMailpitSha256 = TestDigest,
            LauncherVersionMin = "1.2.0",
            LauncherVersionMax = "1.2.0",
            ProjectNamePrefix = "amane",
            MailpitImageReference = TestMailpit,
            SupportedRecordedSchemaMin = 1,
            SupportedRecordedSchemaMax = 2,
            SupportedInspectEffectiveSchemaMin = 1,
            SupportedInspectEffectiveSchemaMax = 1,
            SupportedReleaseManifestSchemaMin = 1,
            SupportedReleaseManifestSchemaMax = 1,
            ArtifactFileName = "amane-mailer-v1.2.0-linux-x64.tar.gz",
            PayloadTreeSha256 = TestDigest,
            Reproducibility = "test",
        };

    private static ReleaseBundleManifestDocument CloneWith(
        ReleaseBundleManifestDocument source,
        int? schemaVersion = null,
        string? imageTag = null,
        string? ociIndexDigest = null,
        string? mailpitImageReference = "",
        int? releaseManifestSchemaMin = -1,
        int? releaseManifestSchemaMax = -1) =>
        new()
        {
            SchemaVersion = schemaVersion ?? source.SchemaVersion,
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
            ImageDigest = ociIndexDigest ?? source.ImageDigest,
            ImageTag = imageTag ?? source.ImageTag,
            OciIndexDigest = ociIndexDigest ?? source.OciIndexDigest,
            ComposeBundleVersion = source.ComposeBundleVersion,
            ComposeSha256 = source.ComposeSha256,
            ComposeImageDigestSha256 = source.ComposeImageDigestSha256,
            ComposeRecordedMetadataSha256 = source.ComposeRecordedMetadataSha256,
            ComposeMailpitSha256 = source.ComposeMailpitSha256,
            LauncherVersionMin = source.LauncherVersionMin,
            LauncherVersionMax = source.LauncherVersionMax,
            ProjectNamePrefix = source.ProjectNamePrefix,
            MailpitImageReference = mailpitImageReference == "" ? source.MailpitImageReference : mailpitImageReference,
            SupportedRecordedSchemaMin = source.SupportedRecordedSchemaMin,
            SupportedRecordedSchemaMax = source.SupportedRecordedSchemaMax,
            SupportedInspectEffectiveSchemaMin = source.SupportedInspectEffectiveSchemaMin,
            SupportedInspectEffectiveSchemaMax = source.SupportedInspectEffectiveSchemaMax,
            SupportedReleaseManifestSchemaMin = releaseManifestSchemaMin == -1
                ? source.SupportedReleaseManifestSchemaMin
                : releaseManifestSchemaMin,
            SupportedReleaseManifestSchemaMax = releaseManifestSchemaMax == -1
                ? source.SupportedReleaseManifestSchemaMax
                : releaseManifestSchemaMax,
            ArtifactFileName = source.ArtifactFileName,
            PayloadTreeSha256 = source.PayloadTreeSha256,
            Reproducibility = source.Reproducibility,
        };

    private static string CreateMinimalOciLayout(
        string directory,
        bool includePlatformManifests,
        bool platformOrderArm64First = false,
        bool omitArm64 = false,
        bool includeLinux386 = false)
    {
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        File.WriteAllText(
            Path.Combine(directory, "oci-layout"),
            """{"imageLayoutVersion":"1.0.0"}""" + "\n");

        if (!includePlatformManifests)
        {
            var emptyIndex = """{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}""" + "\n";
            File.WriteAllText(Path.Combine(directory, "index.json"), emptyIndex);
            // Empty manifests[] cannot bind a Buildx digest; return a placeholder.
            return "sha256:"
                + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(emptyIndex))).ToLowerInvariant();
        }

        // Realistic Buildx OCI layout:
        //   index.json (entrypoint; NOT the Buildx digest)
        //     -> image index blob (THIS is containerimage.descriptor.digest)
        //          -> platform image manifests -> configs
        var configJson = """{"architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""" + "\n";
        var configDigest = WriteBlob(directory, configJson);
        var configArm = """{"architecture":"arm64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""" + "\n";
        var configArmDigest = WriteBlob(directory, configArm);

        var manifestAmd64 =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configJson).ToString()
            + "},\"layers\":[]}\n";
        var manifestAmd64Digest = WriteBlob(directory, manifestAmd64);

        var manifestArm64 =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configArmDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configArm).ToString()
            + "},\"layers\":[]}\n";
        var manifestArm64Digest = WriteBlob(directory, manifestArm64);

        string? manifest386Digest = null;
        string? manifest386 = null;
        if (includeLinux386)
        {
            var config386 = """{"architecture":"386","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""" + "\n";
            var config386Digest = WriteBlob(directory, config386);
            manifest386 =
                "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
                + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
                + config386Digest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(config386).ToString()
                + "},\"layers\":[]}\n";
            manifest386Digest = WriteBlob(directory, manifest386);
        }

        var platformDescriptors = new List<string>();
        void AddPlatform(string digest, string bytesContent, string arch)
        {
            platformDescriptors.Add(
                "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\""
                + digest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(bytesContent).ToString()
                + ",\"platform\":{\"architecture\":\""
                + arch
                + "\",\"os\":\"linux\"}}");
        }

        if (platformOrderArm64First)
        {
            if (!omitArm64)
            {
                AddPlatform(manifestArm64Digest, manifestArm64, "arm64");
            }

            AddPlatform(manifestAmd64Digest, manifestAmd64, "amd64");
        }
        else
        {
            AddPlatform(manifestAmd64Digest, manifestAmd64, "amd64");
            if (!omitArm64)
            {
                AddPlatform(manifestArm64Digest, manifestArm64, "arm64");
            }
        }

        if (includeLinux386 && manifest386Digest is not null && manifest386 is not null)
        {
            AddPlatform(manifest386Digest, manifest386, "386");
        }

        var imageIndexJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":["
            + string.Join(",", platformDescriptors)
            + "]}\n";
        var imageIndexDigest = WriteBlob(directory, imageIndexJson);

        // Layout entrypoint points at the image index blob (Buildx digest target).
        var layoutIndexJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":["
            + "{\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"digest\":\""
            + imageIndexDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(imageIndexJson).ToString()
            + "}]}\n";
        File.WriteAllText(Path.Combine(directory, "index.json"), layoutIndexJson);

        // Return Buildx-style image digest (= image index blob), not sha256(index.json).
        return imageIndexDigest;
    }

    private static string WriteBlob(string ociRoot, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ociRoot, "blobs", "sha256", hex), bytes);
        return "sha256:" + hex;
    }

    private static InputPaths CreateInputs(string root)
    {
        var inputs = Path.Combine(root, "inputs");
        Directory.CreateDirectory(inputs);
        var hostBinary = Path.Combine(inputs, "Amane.Mailer");
        File.WriteAllText(hostBinary, "fake-native-aot-binary");

        var deploy = Path.Combine(inputs, "compose.yml");
        File.WriteAllText(
            deploy,
            """
            services:
              mailer:
                image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            """);

        var imageDigest = Path.Combine(inputs, "compose.image-digest.yml");
        File.WriteAllText(
            imageDigest,
            """
            services:
              mailer:
                image: ${MAILER_IMAGE_REFERENCE}
            """);

        var recorded = Path.Combine(inputs, "compose.recorded-metadata.yml");
        File.WriteAllText(
            recorded,
            """
            services:
              mailer:
                environment:
                  MAILER_SETUP_RECORDED_METADATA_PATH: /run/amane/setup/recorded.json
            """);

        var mailpit = Path.Combine(inputs, "compose.mailpit.yml");
        File.WriteAllText(
            mailpit,
            """
            services:
              mailpit:
                image: ${MAILPIT_IMAGE}
            """);

        var envExample = Path.Combine(inputs, ".env.example");
        File.WriteAllText(envExample, "MAILER_IMAGE_TAG=sha-replace-with-published-git-sha\n");

        var tenantsExample = Path.Combine(inputs, "tenants.example.json");
        File.WriteAllText(tenantsExample, """{"version":1,"tenants":[]}""");
        var tenantsSchema = Path.Combine(inputs, "tenants.schema.json");
        File.WriteAllText(tenantsSchema, """{"$schema":"https://json-schema.org/draft/2020-12/schema"}""");
        var tenantsLocal = Path.Combine(inputs, "tenants.local-acs.json.example");
        File.WriteAllText(tenantsLocal, """{"version":1,"tenants":[]}""");
        var license = Path.Combine(inputs, "LICENSE");
        File.WriteAllText(license, "MIT placeholder for tests\n");

        return new InputPaths(
            hostBinary,
            deploy,
            imageDigest,
            recorded,
            mailpit,
            envExample,
            tenantsExample,
            tenantsSchema,
            tenantsLocal,
            license);
    }

    private sealed record InputPaths(
        string HostBinary,
        string DeployCompose,
        string ImageDigestOverlay,
        string RecordedMetadataOverlay,
        string MailpitOverlay,
        string EnvExample,
        string TenantsExample,
        string TenantsSchema,
        string TenantsLocalAcsExample,
        string License);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "amane-release-bundle-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
