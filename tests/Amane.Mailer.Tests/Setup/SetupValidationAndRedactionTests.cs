using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupValidationAndRedactionTests
{
    [Fact]
    public void Rejects_external_manual_only_keys_in_public_overrides()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-val-" + Guid.NewGuid().ToString("N")));
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
                ["MAILER_DATA_PATH"] = "./data",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Result_and_plan_do_not_include_secret_values()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var result = new SetupCore(bundleIdFactory: static () => "redact-001")
                .GenerateBundle(SetupTestFixtures.StagingAcsRequest(root, dryRun: true));
            var blob = System.Text.Json.JsonSerializer.Serialize(new
            {
                result.Code,
                result.BundleId,
                result.ConfigurationFingerprint,
                result.Message,
                Files = result.Plan?.Files.Select(f => new { f.RelativePath, f.Kind, f.ContentLength }),
            });
            Assert.DoesNotContain("SYNTHETICACCESSKEY", blob, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-staging-token", blob, StringComparison.Ordinal);
            Assert.DoesNotContain("accesskey=", blob, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Integrity_seal_is_not_the_configuration_fingerprint()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var result = new SetupCore(bundleIdFactory: static () => "sep-001")
                .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.Succeeded, result.Code);
            var seal = File.ReadAllBytes(Path.Combine(
                SetupBundleLayout.BundleRoot(root, result.BundleId!),
                "metadata",
                "integrity.seal"));
            var sealText = Convert.ToHexString(seal);
            Assert.DoesNotContain(
                result.ConfigurationFingerprint!.Replace("sha256:", string.Empty, StringComparison.Ordinal),
                sealText,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
