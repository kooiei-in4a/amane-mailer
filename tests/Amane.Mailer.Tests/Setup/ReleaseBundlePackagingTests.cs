using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class ReleaseBundlePackagingTests
{
    private static readonly string TestDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string TestCommit =
        "abcdef0123456789abcdef0123456789abcdef01";

    [Fact]
    public void Stage_writes_manifest_checksums_and_examples_without_secrets()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var ociDigest = CreateMinimalOciLayout(Path.Combine(scratch.Path, "oci-src"));

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = Path.Combine(scratch.Path, "staged"),
            HostRid = "linux-x64",
            HostBinaryPath = inputs.HostBinary,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-" + TestCommit,
            OciIndexDigest = ociDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
            MailpitImageReference = "axllent/mailpit@" + TestDigest,
            OciLayoutSourceDirectory = Path.Combine(scratch.Path, "oci-src"),
        });

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest!.SchemaVersion);
        Assert.Equal(ReleaseBundlePackaging.PackagingKind, result.Manifest.PackagingKind);
        Assert.Equal(ociDigest, result.Manifest.OciIndexDigest);
        Assert.Equal(ociDigest, result.Manifest.ImageDigest);
        Assert.Equal("linux-x64", result.Manifest.HostRid);
        Assert.Equal(1, result.Manifest.SupportedRecordedSchemaMin);
        Assert.Equal(2, result.Manifest.SupportedRecordedSchemaMax);
        Assert.Equal(1, result.Manifest.SupportedInspectEffectiveSchemaMin);
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.ArtifactSha256));
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.Reproducibility));

        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "Amane.Mailer")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "release-bundle-manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "SHA256SUMS")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "README-SETUP.md")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, ".env.example")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "oci", "oci-layout")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory!, "config", "mailer", "tenants.example.json")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory!, ".env")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory!, "tenants.json")));

        var packaging = ReleaseBundlePackaging.ValidatePackagingDocument(result.Manifest);
        Assert.True(packaging.Success, packaging.Message);

        var inventory = ReleaseBundlePackaging.ToInventory(result.Manifest);
        Assert.Null(inventory.ValidateShape());
    }

    [Fact]
    public void ValidatePackagingDocument_rejects_latest_and_digest_mismatch()
    {
        var ok = CreateValidPackagingDocument();
        var latest = CloneWith(ok, imageTag: "latest");
        var mismatch = CloneWith(ok, ociIndexDigest: TestDigest[..^1] + "0");

        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(latest).Success);
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(mismatch).Success);
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
    public void ValidateOciLayoutDirectory_requires_oci_layout_marker_and_matching_digest()
    {
        using var scratch = new TempDir();
        var oci = Path.Combine(scratch.Path, "oci");
        var digest = CreateMinimalOciLayout(oci);

        Assert.True(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest).Success);
        Assert.False(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, TestDigest).Success);

        File.Delete(Path.Combine(oci, "oci-layout"));
        Assert.False(ReleaseBundlePackaging.ValidateOciLayoutDirectory(oci, digest).Success);
    }

    [Fact]
    public void Stage_rejects_missing_host_binary()
    {
        using var scratch = new TempDir();
        var inputs = CreateInputs(scratch.Path);
        var ociDigest = CreateMinimalOciLayout(Path.Combine(scratch.Path, "oci-src"));

        var result = ReleaseBundlePackaging.Stage(new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = Path.Combine(scratch.Path, "staged"),
            HostRid = "linux-x64",
            HostBinaryPath = Path.Combine(scratch.Path, "missing-binary"),
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            LauncherVersion = "1.2.0",
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageDisplayTag = "sha-test",
            OciIndexDigest = ociDigest,
            DeployComposePath = inputs.DeployCompose,
            ImageDigestOverlayPath = inputs.ImageDigestOverlay,
            RecordedMetadataOverlayPath = inputs.RecordedMetadataOverlay,
            MailpitOverlayPath = inputs.MailpitOverlay,
            EnvExamplePath = inputs.EnvExample,
            TenantsExamplePath = inputs.TenantsExample,
            TenantsSchemaPath = inputs.TenantsSchema,
            TenantsLocalAcsExamplePath = inputs.TenantsLocalAcsExample,
        });

        Assert.False(result.Success);
        Assert.Equal("host_binary_missing", result.ReasonCode);
    }

    [Fact]
    public void TryParseArguments_requires_core_flags()
    {
        Assert.False(
            SetupStageReleaseBundleCommand.TryParseArguments(
                ["setup", "stage-release-bundle", "--rid", "linux-x64"],
                out _,
                out var usageError));
        Assert.Contains("Missing required", usageError, StringComparison.Ordinal);
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
    public void Runtime_inventory_ignores_missing_packaging_fields()
    {
        // Existing #449 consumers may omit additive packaging fields; schemaVersion stays 1.
        var document = new ReleaseBundleManifestDocument
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

        var inventory = ReleaseBundlePackaging.ToInventory(document);
        Assert.Null(inventory.ValidateShape());
        Assert.False(ReleaseBundlePackaging.ValidatePackagingDocument(document).Success);
    }

    private static ReleaseBundleManifestDocument CreateValidPackagingDocument() =>
        new()
        {
            SchemaVersion = 1,
            PackagingKind = ReleaseBundlePackaging.PackagingKind,
            SourceCommitSha = TestCommit,
            MailerVersion = "1.2.0",
            HostRid = "linux-x64",
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
            SupportedRecordedSchemaMin = 1,
            SupportedRecordedSchemaMax = 2,
            SupportedInspectEffectiveSchemaMin = 1,
            SupportedInspectEffectiveSchemaMax = 1,
            ArtifactFileName = "amane-mailer-v1.2.0-linux-x64.tar.gz",
            ArtifactSha256 = TestDigest,
            Reproducibility = "test",
        };

    private static ReleaseBundleManifestDocument CloneWith(
        ReleaseBundleManifestDocument source,
        int? schemaVersion = null,
        string? imageTag = null,
        string? ociIndexDigest = null) =>
        new()
        {
            SchemaVersion = schemaVersion ?? source.SchemaVersion,
            PackagingKind = source.PackagingKind,
            SourceCommitSha = source.SourceCommitSha,
            MailerVersion = source.MailerVersion,
            HostRid = source.HostRid,
            ImageRepository = source.ImageRepository,
            ImageDigest = source.ImageDigest,
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
            SupportedRecordedSchemaMin = source.SupportedRecordedSchemaMin,
            SupportedRecordedSchemaMax = source.SupportedRecordedSchemaMax,
            SupportedInspectEffectiveSchemaMin = source.SupportedInspectEffectiveSchemaMin,
            SupportedInspectEffectiveSchemaMax = source.SupportedInspectEffectiveSchemaMax,
            ArtifactFileName = source.ArtifactFileName,
            ArtifactSha256 = source.ArtifactSha256,
            Reproducibility = source.Reproducibility,
        };

    private static string CreateMinimalOciLayout(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "blobs", "sha256"));
        File.WriteAllText(
            Path.Combine(directory, "oci-layout"),
            """{"imageLayoutVersion":"1.0.0"}""" + "\n");
        var indexJson = """{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}""" + "\n";
        File.WriteAllText(Path.Combine(directory, "index.json"), indexJson);
        var digest = "sha256:"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indexJson))).ToLowerInvariant();
        // Empty blob placeholder so layout shape matches B1 expectation.
        File.WriteAllText(Path.Combine(directory, "blobs", "sha256", digest["sha256:".Length..]), indexJson);
        return digest;
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

        return new InputPaths(
            hostBinary,
            deploy,
            imageDigest,
            recorded,
            mailpit,
            envExample,
            tenantsExample,
            tenantsSchema,
            tenantsLocal);
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
        string TenantsLocalAcsExample);

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
