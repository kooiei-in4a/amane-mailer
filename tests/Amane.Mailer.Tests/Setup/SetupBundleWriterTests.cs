using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupBundleWriterTests
{
    [Fact]
    public void GenerateBundle_writes_adr0021_layout_and_does_not_create_ACTIVE()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "20260728-fixed001");
            var result = core.GenerateBundle(SetupTestFixtures.StagingAcsRequest(root));

            Assert.Equal(SetupResultCode.Succeeded, result.Code);
            Assert.Equal("20260728-fixed001", result.BundleId);
            Assert.False(string.IsNullOrWhiteSpace(result.ConfigurationFingerprint));

            var bundleRoot = SetupBundleLayout.BundleRoot(root, result.BundleId!);
            Assert.True(File.Exists(Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "config", "tenants.json")));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "config", PlatformSenderFile.CanonicalFileName)));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "env", "compose.env")));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "env", "secrets.env")));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "secrets", AcsSecretFileNames.CanonicalFileName)));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "metadata", "recorded.json")));
            Assert.True(File.Exists(Path.Combine(bundleRoot, "metadata", "integrity.seal")));
            Assert.False(File.Exists(SetupBundleLayout.ActivePointerPath(root)));

            var recorded = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(bundleRoot, "metadata", "recorded.json")),
                SetupJsonContext.Default.SetupRecordedMetadata);
            Assert.NotNull(recorded);
            Assert.Equal(result.ConfigurationFingerprint, recorded!.ConfigurationFingerprint);
            Assert.DoesNotContain("SYNTHETICACCESSKEY", File.ReadAllText(Path.Combine(bundleRoot, "metadata", "recorded.json")), StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-staging-token", File.ReadAllText(Path.Combine(bundleRoot, "metadata", "recorded.json")), StringComparison.Ordinal);

            var secretsEnv = File.ReadAllText(Path.Combine(bundleRoot, "env", "secrets.env"));
            Assert.Contains("MAIL_SERVICE_TOKEN_STAGING=", secretsEnv, StringComparison.Ordinal);
            Assert.DoesNotContain("AMANE_ADMIN_PASSWORD_HASH=", secretsEnv, StringComparison.Ordinal);

            var sealingKey = File.ReadAllBytes(SetupBundleLayout.HostSealingKeyPath(root));
            var seal = File.ReadAllBytes(Path.Combine(bundleRoot, "metadata", "integrity.seal"));
            var secretMembers = new List<(string, byte[])>
            {
                ("env/secrets.env", Encoding.UTF8.GetBytes(secretsEnv)),
                (
                    "secrets/acs_connection_string",
                    File.ReadAllBytes(Path.Combine(bundleRoot, "secrets", AcsSecretFileNames.CanonicalFileName))),
            };
            Assert.True(SetupIntegritySealer.TryVerifySeal(sealingKey, seal, result.BundleId!, result.ConfigurationFingerprint!, SetupBundleLayout.RecordedSchemaVersion, secretMembers));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DryRun_does_not_write_files_and_omits_secret_derived_integrity_values()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "dryrun-0001");
            var result = core.GenerateBundle(SetupTestFixtures.StagingAcsRequest(root, dryRun: true));

            Assert.Equal(SetupResultCode.DryRunPlan, result.Code);
            Assert.NotNull(result.Plan);
            Assert.False(Directory.Exists(SetupBundleLayout.BundleRoot(root, "dryrun-0001")));
            Assert.DoesNotContain("SYNTHETICACCESSKEY", result.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.All(result.Plan!.Files, file =>
            {
                Assert.False(string.IsNullOrWhiteSpace(file.RelativePath));
                Assert.True(file.ContentLength >= 0);
            });
            Assert.Contains(result.Plan.Files, f => f.Kind == SetupPlannedFileKind.FileSecret);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Duplicate_bundle_id_is_rejected_without_overwrite()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "dup-0001");
            Assert.Equal(SetupResultCode.Succeeded, core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root)).Code);
            var second = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedBundleExists, second.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Manual_deployment_markers_without_bundles_fail_closed()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "tenants.json"), "{}");
            var core = new SetupCore(bundleIdFactory: static () => "manual-conflict");
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedConflictManual, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
