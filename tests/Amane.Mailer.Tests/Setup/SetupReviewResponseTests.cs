using System.Text;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupReviewResponseTests
{
    [Fact]
    public void Integrity_seal_does_not_verify_when_copied_to_another_bundle_identity()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "bundle-a");
            var result = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, result.Code);

            var bundleRoot = SetupBundleLayout.BundleRoot(root, "bundle-a");
            var secretsEnv = File.ReadAllBytes(Path.Combine(bundleRoot, "env", "secrets.env"));
            var seal = File.ReadAllBytes(Path.Combine(bundleRoot, "metadata", "integrity.seal"));
            var key = File.ReadAllBytes(SetupBundleLayout.HostSealingKeyPath(root));
            var members = new List<(string, byte[])> { ("env/secrets.env", secretsEnv) };

            Assert.True(SetupIntegritySealer.TryVerifySeal(
                key,
                seal,
                "bundle-a",
                result.ConfigurationFingerprint!,
                SetupBundleLayout.RecordedSchemaVersion,
                members));

            Assert.False(SetupIntegritySealer.TryVerifySeal(
                key,
                seal,
                "bundle-b",
                result.ConfigurationFingerprint!,
                SetupBundleLayout.RecordedSchemaVersion,
                members));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Manual_markers_fail_closed_even_when_bundles_directory_exists()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bundles"));
            File.WriteAllText(Path.Combine(root, ".env"), "COMPOSE_PROJECT_NAME=manual");
            var result = new SetupCore(bundleIdFactory: static () => "x")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedConflictManual, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void DryRun_still_runs_conflict_preflight_for_duplicate_bundle_id()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "dup-dry");
            Assert.Equal(SetupResultCode.Succeeded, core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root)).Code);
            var dry = core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedBundleExists, dry.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Public_env_overrides_reject_workflow_owned_keys()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-ov-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["MAILER_BOUNCE_INGESTION"] = "queue",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Admin_enabled_typed_representation_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-adm-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            Admin = new SetupAdminBootstrapRepresentation { Enabled = true, AllowHttp = true },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Missing_sealing_key_with_existing_bundle_fails_closed()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var core = new SetupCore(bundleIdFactory: static () => "first");
            Assert.Equal(SetupResultCode.Succeeded, core.GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root)).Code);
            File.Delete(SetupBundleLayout.HostSealingKeyPath(root));

            var second = new SetupCore(bundleIdFactory: static () => "second")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyMissing, second.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void World_readable_sealing_key_is_rejected_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Unix mode verification is Linux-only.");
            return;
        }

        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            Directory.CreateDirectory(SetupBundleLayout.SealingDir(root));
            var keyPath = SetupBundleLayout.HostSealingKeyPath(root);
            File.WriteAllBytes(keyPath, new byte[SetupIntegritySealer.SealingKeyLength]);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            var result = new SetupCore(bundleIdFactory: static () => "perm")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root, dryRun: true));
            Assert.Equal(SetupResultCode.RejectedSealingKeyUnsafe, result.Code);
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
        }
    }
}
