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
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "libe_sqlite3.so")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory!, "e_sqlite3.dll")));
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
        Assert.True(
            File.Exists(Path.Combine(
                result.OutputDirectory!,
                ReleaseBundlePackaging.NativeSqliteSidecarFileName(hostRid))));

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
    public void ValidateOciLayoutDirectory_accepts_explicit_single_platform_manifest_mode()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "single-amd64");
        var digest = CreateSinglePlatformOciLayout(oci, "amd64");
        var metadata = new OciDescriptor
        {
            MediaType = "application/vnd.oci.image.manifest.v1+json",
            Digest = digest,
            Size = new FileInfo(Path.Combine(oci, "blobs", "sha256", digest["sha256:".Length..])).Length,
        };

        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                oci,
                digest,
                ["linux/amd64"],
                metadata,
                allowSinglePlatformImageManifest: true).Success);
        Assert.False(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                oci,
                digest,
                ["linux/amd64"],
                metadata).Success);
    }

    [Fact]
    public void AssembleOciLayouts_writes_final_identity_metadata_and_deterministic_platform_order()
    {
        using var scratch = new TempDir();
        var amd64Root = Path.Combine(scratch.Path, "amd64");
        var arm64Root = Path.Combine(scratch.Path, "arm64");
        var amd64Digest = CreateSinglePlatformOciLayout(amd64Root, "amd64");
        var arm64Digest = CreateSinglePlatformOciLayout(arm64Root, "arm64");
        var amd64Metadata = WriteBuildxMetadata(amd64Root, amd64Digest);
        var arm64Metadata = WriteBuildxMetadata(arm64Root, arm64Digest);
        var output = Path.Combine(scratch.Path, "assembled");

        var result = ReleaseBundlePackaging.AssembleOciLayouts(
            new ReleaseBundlePackaging.OciAssemblyRequest
            {
                Amd64LayoutDirectory = amd64Root,
                Amd64MetadataPath = amd64Metadata,
                Arm64LayoutDirectory = arm64Root,
                Arm64MetadataPath = arm64Metadata,
                OutputDirectory = output,
                ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
                ImageTag = "sha-" + TestCommit,
                SourceCommitSha = TestCommit,
                MailerVersion = "1.2.0",
            },
            out var finalDigest);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(finalDigest);
        Assert.True(File.Exists(Path.Combine(output, "oci", "index.json")));
        Assert.True(File.Exists(Path.Combine(output, "buildx-metadata.json")));
        Assert.Equal(finalDigest + "\n", File.ReadAllText(Path.Combine(output, "oci-index.digest")));

        var root = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(output, "oci", "index.json")),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var finalIndex = JsonSerializer.Deserialize(
            File.ReadAllBytes(
                Path.Combine(output, "oci", "blobs", "sha256", finalDigest!["sha256:".Length..])),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        Assert.Equal(finalDigest, root.Manifests![0].Digest);
        Assert.Equal("amd64", finalIndex.Manifests![0].Platform!.Architecture);
        Assert.Equal("arm64", finalIndex.Manifests[1].Platform!.Architecture);

        var identity = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(output, "image-identity.json")),
            ReleaseBundleJsonContext.Default.ImageIdentityDocument)!;
        Assert.Equal(["linux/amd64", "linux/arm64"], identity.Platforms!);
        Assert.True(
            ReleaseBundlePackaging.ValidateOciLayoutDirectory(
                Path.Combine(output, "oci"),
                finalDigest,
                ReleaseBundlePackaging.RequiredOciPlatforms,
                new OciDescriptor
                {
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Digest = finalDigest,
                    Size = new FileInfo(
                        Path.Combine(output, "oci", "blobs", "sha256", finalDigest!["sha256:".Length..])).Length,
                }).Success);
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
    public void ValidateOciLayoutDirectory_accepts_exact_amd64_and_arm64_cardinality()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "exact-platforms");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true, includeLayerBlob: true);
        Assert.True(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest).Success);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_duplicate_amd64_distinct_digests()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "dup-amd64-distinct");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            duplicateAmd64DistinctDigest: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_duplicate", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_duplicate_arm64_distinct_digests()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "dup-arm64-distinct");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            duplicateArm64DistinctDigest: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_duplicate", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_same_digest_referenced_twice_as_amd64()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "dup-amd64-same-digest");
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            duplicateAmd64SameDigest: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_digest_duplicate", result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_same_digest_with_conflicting_platform_annotations()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "conflict-annotation");
        // amd64 manifest listed twice: once as amd64, once as arm64 (omit real arm64 blob entry).
        var digest = CreateMinimalOciLayout(
            oci,
            includePlatformManifests: true,
            omitArm64: true,
            conflictingPlatformAnnotationSameDigest: true);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.Equal("oci_platform_annotation_conflict", result.ReasonCode);
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
        Assert.Equal("oci_bound_not_image_index", result.ReasonCode);
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
    public void AssertImageDigestMatchesMetadata_rejects_mismatch()
    {
        var ok = ReleaseBundlePackaging.AssertImageDigestMatchesMetadata(TestDigest, TestDigest);
        Assert.True(ok.Success);

        var other =
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var mismatch = ReleaseBundlePackaging.AssertImageDigestMatchesMetadata(TestDigest, other);
        Assert.False(mismatch.Success);
        Assert.Equal("buildx_image_digest_mismatch", mismatch.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_sibling_platform_outside_bound_subtree()
    {
        using var scratch = new TempDir();

        // bound subtree = amd64 only; top-level sibling supplies arm64
        var amdOnly = Path.Combine(scratch.Path, "amd-only-sibling-arm");
        var amdDigest = CreateLayoutWithTopLevelSiblingPlatform(
            amdOnly,
            boundPlatforms: ["amd64"],
            siblingPlatform: "arm64");
        var amdResult = ReleaseBundlePackaging.ValidateOciLayoutDirectory(amdOnly, amdDigest);
        Assert.False(amdResult.Success);
        Assert.Equal("oci_layout_sibling_manifests", amdResult.ReasonCode);

        // bound subtree = arm64 only; top-level sibling supplies amd64
        var armOnly = Path.Combine(scratch.Path, "arm-only-sibling-amd");
        var armDigest = CreateLayoutWithTopLevelSiblingPlatform(
            armOnly,
            boundPlatforms: ["arm64"],
            siblingPlatform: "amd64");
        var armResult = ReleaseBundlePackaging.ValidateOciLayoutDirectory(armOnly, armDigest);
        Assert.False(armResult.Success);
        Assert.Equal("oci_layout_sibling_manifests", armResult.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_bound_single_manifest_with_sibling_platform()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "bound-manifest-sibling");
        var digest = CreateBoundSingleManifestWithSibling(oci);
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        // Sibling reject fires first; bound-not-index would also apply without siblings.
        Assert.True(
            result.ReasonCode is "oci_layout_sibling_manifests" or "oci_bound_not_image_index",
            result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_arbitrary_blob_platform_annotation()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "arbitrary-platform");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true, omitArm64: true);
        digest = InjectBogusPlatformDescriptorIntoBoundIndex(
            oci,
            digest,
            mediaType: "application/octet-stream",
            architecture: "arm64");
        var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest);
        Assert.False(result.Success);
        Assert.True(
            result.ReasonCode is "oci_platform_manifest_media_type_invalid"
                or "oci_descriptor_media_type_unknown",
            result.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_config_descriptor_platform_annotation()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "config-platform");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var fail = MutateFirstConfigDescriptor(oci, digest, addPlatformArm64: true, clearMediaType: false);
        Assert.False(fail.Success);
        Assert.Equal("oci_platform_on_non_manifest", fail.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_missing_or_unknown_media_types_for_platforms()
    {
        using var scratch = new TempDir();
        var missingMedia = Path.Combine(scratch.Path, "missing-media");
        var missingDigest = CreateMinimalOciLayout(missingMedia, includePlatformManifests: true, omitArm64: true);
        missingDigest = InjectBogusPlatformDescriptorIntoBoundIndex(
            missingMedia,
            missingDigest,
            mediaType: null,
            architecture: "arm64");
        var missing = ReleaseBundlePackaging.ValidateOciLayoutDirectory(missingMedia, missingDigest);
        Assert.False(missing.Success);
        Assert.Equal("oci_descriptor_media_type_missing", missing.ReasonCode);

        var unknownMedia = Path.Combine(scratch.Path, "unknown-media");
        var unknownDigest = CreateMinimalOciLayout(unknownMedia, includePlatformManifests: true, omitArm64: true);
        unknownDigest = InjectBogusPlatformDescriptorIntoBoundIndex(
            unknownMedia,
            unknownDigest,
            mediaType: "application/vnd.example.unknown",
            architecture: "arm64");
        var unknown = ReleaseBundlePackaging.ValidateOciLayoutDirectory(unknownMedia, unknownDigest);
        Assert.False(unknown.Success);
        Assert.Equal("oci_descriptor_media_type_unknown", unknown.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_nested_descriptor_size_mismatches()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "nested-size");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var fail = MutateFirstPlatformManifestSize(oci, digest, delta: 17);
        Assert.False(fail.Success);
        Assert.Equal("oci_descriptor_size_mismatch", fail.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_config_size_mismatch()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "config-size");
        var digest = CreateMinimalOciLayout(oci, includePlatformManifests: true);
        var fail = MutateFirstConfigDescriptor(oci, digest, addPlatformArm64: false, clearMediaType: false, sizeDelta: 5);
        Assert.False(fail.Success);
        Assert.Equal("oci_descriptor_size_mismatch", fail.ReasonCode);
    }

    [Fact]
    public void ValidateOciLayoutDirectory_rejects_layer_size_and_media_type_mismatches()
    {
        using var scratch = new TempDir();
        var okOci = Path.Combine(scratch.Path, "layer-ok");
        var okDigest = CreateMinimalOciLayout(okOci, includePlatformManifests: true, includeLayerBlob: true);
        Assert.True(ReleaseBundlePackaging.ValidateOciLayoutDirectory(okOci, okDigest).Success);

        var sizeOci = Path.Combine(scratch.Path, "layer-size");
        var sizeDigest = CreateMinimalOciLayout(sizeOci, includePlatformManifests: true, includeLayerBlob: true);
        var sizeFail = MutateFirstLayerDescriptor(sizeOci, sizeDigest, sizeDelta: 9, unknownMediaType: false);
        Assert.False(sizeFail.Success);
        Assert.Equal("oci_descriptor_size_mismatch", sizeFail.ReasonCode);

        var mediaOci = Path.Combine(scratch.Path, "layer-media");
        var mediaDigest = CreateMinimalOciLayout(mediaOci, includePlatformManifests: true, includeLayerBlob: true);
        var mediaFail = MutateFirstLayerDescriptor(mediaOci, mediaDigest, sizeDelta: 0, unknownMediaType: true);
        Assert.False(mediaFail.Success);
        Assert.True(
            mediaFail.ReasonCode is "oci_layer_media_type_invalid" or "oci_descriptor_media_type_unknown",
            mediaFail.ReasonCode);
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
    public void Stage_rejects_missing_native_sqlite_sidecar()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        File.Delete(Path.Combine(Path.GetDirectoryName(inputs.HostBinary)!, "libe_sqlite3.so"));
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

        Assert.False(result.Success);
        Assert.Equal("host_native_sqlite_missing", result.ReasonCode);
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

    private static string CreateSinglePlatformOciLayout(string directory, string architecture)
    {
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        File.WriteAllText(
            Path.Combine(directory, "oci-layout"),
            "{\"imageLayoutVersion\":\"1.0.0\"}\n");

        var configJson =
            "{\"architecture\":\"" + architecture + "\",\"os\":\"linux\",\"rootfs\":{\"type\":\"layers\",\"diff_ids\":[]}}\n";
        var configDigest = WriteBlob(directory, configJson);
        var manifestJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configJson)
            + "},\"layers\":[]}\n";
        var manifestDigest = WriteBlob(directory, manifestJson);
        var layout = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    MediaType = "application/vnd.oci.image.manifest.v1+json",
                    Digest = manifestDigest,
                    Size = Encoding.UTF8.GetByteCount(manifestJson),
                    Platform = new OciPlatform { Os = "linux", Architecture = architecture },
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(directory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(layout, ReleaseBundleJsonContext.Default.OciIndexDocument));
        return manifestDigest;
    }

    private static string WriteBuildxMetadata(string layoutRoot, string digest)
    {
        var blobPath = Path.Combine(layoutRoot, "blobs", "sha256", digest["sha256:".Length..]);
        var metadataPath = Path.GetFullPath(layoutRoot) + ".buildx-metadata.json";
        var metadata = new BuildxMetadataDocument
        {
            ContainerImageDescriptor = new OciDescriptor
            {
                MediaType = "application/vnd.oci.image.manifest.v1+json",
                Digest = digest,
                Size = new FileInfo(blobPath).Length,
            },
            ContainerImageDigest = digest,
        };
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(metadata, ReleaseBundleJsonContext.Default.BuildxMetadataDocument) + "\n");
        return metadataPath;
    }

    private static string CreateMinimalOciLayout(
        string directory,
        bool includePlatformManifests,
        bool platformOrderArm64First = false,
        bool omitArm64 = false,
        bool includeLinux386 = false,
        bool includeLayerBlob = false,
        bool duplicateAmd64DistinctDigest = false,
        bool duplicateArm64DistinctDigest = false,
        bool duplicateAmd64SameDigest = false,
        bool conflictingPlatformAnnotationSameDigest = false)
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
        //          -> platform image manifests -> configs (+ optional layers)
        string? layerDigest = null;
        string layersJson = "[]";
        if (includeLayerBlob)
        {
            var layerBytes = "fake-layer-bytes\n";
            layerDigest = WriteBlob(directory, layerBytes);
            layersJson =
                "[{\"mediaType\":\"application/vnd.oci.image.layer.v1.tar\",\"digest\":\""
                + layerDigest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(layerBytes).ToString()
                + "}]";
        }

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
            + "},\"layers\":"
            + layersJson
            + "}\n";
        var manifestAmd64Digest = WriteBlob(directory, manifestAmd64);

        // Arm64 reuses the same optional layer blob when present (shared digest is allowed).
        var manifestArm64 =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configArmDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configArm).ToString()
            + "},\"layers\":"
            + layersJson
            + "}\n";
        var manifestArm64Digest = WriteBlob(directory, manifestArm64);

        string? manifestAmd64Alt = null;
        string? manifestAmd64AltDigest = null;
        if (duplicateAmd64DistinctDigest)
        {
            var configAmdAlt =
                """{"architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]},"config":{"Env":["X=1"]}}"""
                + "\n";
            var configAmdAltDigest = WriteBlob(directory, configAmdAlt);
            manifestAmd64Alt =
                "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
                + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
                + configAmdAltDigest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(configAmdAlt).ToString()
                + "},\"layers\":"
                + layersJson
                + "}\n";
            manifestAmd64AltDigest = WriteBlob(directory, manifestAmd64Alt);
        }

        string? manifestArm64Alt = null;
        string? manifestArm64AltDigest = null;
        if (duplicateArm64DistinctDigest)
        {
            var configArmAlt =
                """{"architecture":"arm64","os":"linux","rootfs":{"type":"layers","diff_ids":[]},"config":{"Env":["Y=1"]}}"""
                + "\n";
            var configArmAltDigest = WriteBlob(directory, configArmAlt);
            manifestArm64Alt =
                "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
                + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
                + configArmAltDigest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(configArmAlt).ToString()
                + "},\"layers\":"
                + layersJson
                + "}\n";
            manifestArm64AltDigest = WriteBlob(directory, manifestArm64Alt);
        }

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

        if (duplicateAmd64DistinctDigest
            && manifestAmd64AltDigest is not null
            && manifestAmd64Alt is not null)
        {
            AddPlatform(manifestAmd64AltDigest, manifestAmd64Alt, "amd64");
        }

        if (duplicateArm64DistinctDigest
            && manifestArm64AltDigest is not null
            && manifestArm64Alt is not null)
        {
            AddPlatform(manifestArm64AltDigest, manifestArm64Alt, "arm64");
        }

        if (duplicateAmd64SameDigest)
        {
            AddPlatform(manifestAmd64Digest, manifestAmd64, "amd64");
        }

        if (conflictingPlatformAnnotationSameDigest)
        {
            // Same amd64 manifest digest also claimed as arm64 (omit real arm64 when present).
            AddPlatform(manifestAmd64Digest, manifestAmd64, "arm64");
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

    /// <summary>
    /// Bound image-index has only the listed platforms; a top-level sibling manifest
    /// supplies another platform outside the Buildx digest subtree.
    /// </summary>
    private static string CreateLayoutWithTopLevelSiblingPlatform(
        string directory,
        string[] boundPlatforms,
        string siblingPlatform)
    {
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        File.WriteAllText(
            Path.Combine(directory, "oci-layout"),
            """{"imageLayoutVersion":"1.0.0"}""" + "\n");

        void MakeManifest(string arch, out string digest, out string json)
        {
            var configJson =
                "{\"architecture\":\"" + arch + "\",\"os\":\"linux\",\"rootfs\":{\"type\":\"layers\",\"diff_ids\":[]}}\n";
            var configDigest = WriteBlob(directory, configJson);
            json =
                "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
                + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
                + configDigest
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(configJson).ToString()
                + "},\"layers\":[]}\n";
            digest = WriteBlob(directory, json);
        }

        var platformDescriptors = new List<string>();
        foreach (var arch in boundPlatforms)
        {
            MakeManifest(arch, out var dig, out var json);
            platformDescriptors.Add(
                "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\""
                + dig
                + "\",\"size\":"
                + Encoding.UTF8.GetByteCount(json).ToString()
                + ",\"platform\":{\"architecture\":\""
                + arch
                + "\",\"os\":\"linux\"}}");
        }

        var imageIndexJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":["
            + string.Join(",", platformDescriptors)
            + "]}\n";
        var imageIndexDigest = WriteBlob(directory, imageIndexJson);

        MakeManifest(siblingPlatform, out var siblingDig, out var siblingJson);
        var layoutIndexJson =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":["
            + "{\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"digest\":\""
            + imageIndexDigest
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(imageIndexJson).ToString()
            + "},"
            + "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\""
            + siblingDig
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(siblingJson).ToString()
            + ",\"platform\":{\"architecture\":\""
            + siblingPlatform
            + "\",\"os\":\"linux\"}}]}\n";
        File.WriteAllText(Path.Combine(directory, "index.json"), layoutIndexJson);
        return imageIndexDigest;
    }

    private static string CreateBoundSingleManifestWithSibling(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        File.WriteAllText(
            Path.Combine(directory, "oci-layout"),
            """{"imageLayoutVersion":"1.0.0"}""" + "\n");

        var configAmd = """{"architecture":"amd64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""" + "\n";
        var configAmdDig = WriteBlob(directory, configAmd);
        var manifestAmd =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configAmdDig
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configAmd).ToString()
            + "},\"layers\":[]}\n";
        var manifestAmdDig = WriteBlob(directory, manifestAmd);

        var configArm = """{"architecture":"arm64","os":"linux","rootfs":{"type":"layers","diff_ids":[]}}""" + "\n";
        var configArmDig = WriteBlob(directory, configArm);
        var manifestArm =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\","
            + "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\",\"digest\":\""
            + configArmDig
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(configArm).ToString()
            + "},\"layers\":[]}\n";
        var manifestArmDig = WriteBlob(directory, manifestArm);

        var layout =
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":["
            + "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\""
            + manifestAmdDig
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(manifestAmd).ToString()
            + ",\"platform\":{\"architecture\":\"amd64\",\"os\":\"linux\"}},"
            + "{\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\",\"digest\":\""
            + manifestArmDig
            + "\",\"size\":"
            + Encoding.UTF8.GetByteCount(manifestArm).ToString()
            + ",\"platform\":{\"architecture\":\"arm64\",\"os\":\"linux\"}}]}\n";
        File.WriteAllText(Path.Combine(directory, "index.json"), layout);
        return manifestAmdDig;
    }

    private static string InjectBogusPlatformDescriptorIntoBoundIndex(
        string ociRoot,
        string boundDigest,
        string? mediaType,
        string architecture)
    {
        var bogusJson = "{\"kind\":\"not-a-manifest\"}\n";
        var bogusDigest = WriteBlob(ociRoot, bogusJson);
        var indexBlobPath = Path.Combine(ociRoot, "blobs", "sha256", boundDigest["sha256:".Length..]);
        var nested = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexBlobPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var descriptors = nested.Manifests!.ToList();
        descriptors.Add(new OciDescriptor
        {
            Digest = bogusDigest,
            MediaType = mediaType,
            Size = Encoding.UTF8.GetByteCount(bogusJson),
            Platform = new OciPlatform { Os = "linux", Architecture = architecture },
        });
        var rewritten = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests = descriptors.ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            rewritten,
            ReleaseBundleJsonContext.Default.OciIndexDocument);
        var newBoundDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ociRoot, "blobs", "sha256", newBoundDigest["sha256:".Length..]), bytes);
        File.Delete(indexBlobPath);
        var layout = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newBoundDigest,
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Size = bytes.LongLength,
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(ociRoot, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(layout, ReleaseBundleJsonContext.Default.OciIndexDocument));
        return newBoundDigest;
    }

    private static ReleaseBundlePackaging.PackagingValidationResult MutateFirstPlatformManifestSize(
        string ociRoot,
        string boundDigest,
        long delta)
    {
        var indexBlobPath = Path.Combine(ociRoot, "blobs", "sha256", boundDigest["sha256:".Length..]);
        var nested = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexBlobPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var first = nested.Manifests![0];
        nested = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = nested.MediaType,
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = first.Digest,
                    MediaType = first.MediaType,
                    Size = (first.Size ?? 0) + delta,
                    Platform = first.Platform,
                },
                .. nested.Manifests.Skip(1),
            ],
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            nested,
            ReleaseBundleJsonContext.Default.OciIndexDocument);
        var newBound = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ociRoot, "blobs", "sha256", newBound["sha256:".Length..]), bytes);
        File.Delete(indexBlobPath);
        var layout = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newBound,
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Size = bytes.LongLength,
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(ociRoot, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(layout, ReleaseBundleJsonContext.Default.OciIndexDocument));
        return ReleaseBundlePackaging.ValidateOciLayoutDirectory(ociRoot, newBound);
    }

    private static ReleaseBundlePackaging.PackagingValidationResult MutateFirstConfigDescriptor(
        string ociRoot,
        string boundDigest,
        bool addPlatformArm64,
        bool clearMediaType,
        long sizeDelta = 0)
    {
        var indexBlobPath = Path.Combine(ociRoot, "blobs", "sha256", boundDigest["sha256:".Length..]);
        var nested = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexBlobPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var first = nested.Manifests![0];
        var manifestPath = Path.Combine(ociRoot, "blobs", "sha256", first.Digest!["sha256:".Length..]);
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllBytes(manifestPath),
            ReleaseBundleJsonContext.Default.OciManifestDocument)!;
        var config = manifest.Config!;
        var mutatedConfig = new OciDescriptor
        {
            Digest = config.Digest,
            MediaType = clearMediaType ? null : config.MediaType,
            Size = (config.Size ?? 0) + sizeDelta,
            Platform = addPlatformArm64
                ? new OciPlatform { Os = "linux", Architecture = "arm64" }
                : config.Platform,
        };
        var mutatedManifest = new OciManifestDocument
        {
            SchemaVersion = 2,
            MediaType = manifest.MediaType,
            Config = mutatedConfig,
            Layers = manifest.Layers,
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            mutatedManifest,
            ReleaseBundleJsonContext.Default.OciManifestDocument);
        var newManifestDigest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        File.WriteAllBytes(
            Path.Combine(ociRoot, "blobs", "sha256", newManifestDigest["sha256:".Length..]),
            manifestBytes);
        File.Delete(manifestPath);

        nested = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = nested.MediaType,
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newManifestDigest,
                    MediaType = first.MediaType,
                    Size = manifestBytes.LongLength,
                    Platform = first.Platform,
                },
                .. nested.Manifests.Skip(1),
            ],
        };
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
            nested,
            ReleaseBundleJsonContext.Default.OciIndexDocument);
        var newBound = "sha256:" + Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ociRoot, "blobs", "sha256", newBound["sha256:".Length..]), indexBytes);
        File.Delete(indexBlobPath);
        var layout = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newBound,
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Size = indexBytes.LongLength,
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(ociRoot, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(layout, ReleaseBundleJsonContext.Default.OciIndexDocument));
        return ReleaseBundlePackaging.ValidateOciLayoutDirectory(ociRoot, newBound);
    }

    private static ReleaseBundlePackaging.PackagingValidationResult MutateFirstLayerDescriptor(
        string ociRoot,
        string boundDigest,
        long sizeDelta,
        bool unknownMediaType)
    {
        var indexBlobPath = Path.Combine(ociRoot, "blobs", "sha256", boundDigest["sha256:".Length..]);
        var nested = JsonSerializer.Deserialize(
            File.ReadAllBytes(indexBlobPath),
            ReleaseBundleJsonContext.Default.OciIndexDocument)!;
        var first = nested.Manifests![0];
        var manifestPath = Path.Combine(ociRoot, "blobs", "sha256", first.Digest!["sha256:".Length..]);
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllBytes(manifestPath),
            ReleaseBundleJsonContext.Default.OciManifestDocument)!;
        Assert.NotNull(manifest.Layers);
        Assert.NotEmpty(manifest.Layers);

        var layer = manifest.Layers[0];
        var mutatedLayer = new OciDescriptor
        {
            Digest = layer.Digest,
            MediaType = unknownMediaType ? "application/vnd.example.not-a-layer" : layer.MediaType,
            Size = (layer.Size ?? 0) + sizeDelta,
            Platform = layer.Platform,
        };
        var mutatedManifest = new OciManifestDocument
        {
            SchemaVersion = 2,
            MediaType = manifest.MediaType,
            Config = manifest.Config,
            Layers = [mutatedLayer, .. manifest.Layers.Skip(1)],
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            mutatedManifest,
            ReleaseBundleJsonContext.Default.OciManifestDocument);
        var newManifestDigest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        File.WriteAllBytes(
            Path.Combine(ociRoot, "blobs", "sha256", newManifestDigest["sha256:".Length..]),
            manifestBytes);
        File.Delete(manifestPath);

        nested = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = nested.MediaType,
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newManifestDigest,
                    MediaType = first.MediaType,
                    Size = manifestBytes.LongLength,
                    Platform = first.Platform,
                },
                .. nested.Manifests.Skip(1),
            ],
        };
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
            nested,
            ReleaseBundleJsonContext.Default.OciIndexDocument);
        var newBound = "sha256:" + Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(ociRoot, "blobs", "sha256", newBound["sha256:".Length..]), indexBytes);
        File.Delete(indexBlobPath);
        var layout = new OciIndexDocument
        {
            SchemaVersion = 2,
            MediaType = "application/vnd.oci.image.index.v1+json",
            Manifests =
            [
                new OciDescriptor
                {
                    Digest = newBound,
                    MediaType = "application/vnd.oci.image.index.v1+json",
                    Size = indexBytes.LongLength,
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(ociRoot, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(layout, ReleaseBundleJsonContext.Default.OciIndexDocument));
        return ReleaseBundlePackaging.ValidateOciLayoutDirectory(ociRoot, newBound);
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
        File.WriteAllText(Path.Combine(inputs, "libe_sqlite3.so"), "fake-sqlite-native-linux");
        File.WriteAllText(Path.Combine(inputs, "e_sqlite3.dll"), "fake-sqlite-native-windows");

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
