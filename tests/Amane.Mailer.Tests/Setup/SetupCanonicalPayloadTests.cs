using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupCanonicalPayloadTests
{
    private static string PlaceholderRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-managed-placeholder"));

    [Fact]
    public void Canonical_payload_and_fingerprint_are_deterministic_for_same_non_secret_configuration()
    {
        var request = SetupTestFixtures.LocalMailpitRequest(PlaceholderRoot(), dryRun: true);
        var a = SetupConfigurationMaterializer.Materialize(request, "bundle-a", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var b = SetupConfigurationMaterializer.Materialize(request, "bundle-b", DateTimeOffset.Parse("2026-07-29T00:00:00Z"));

        Assert.Equal(a.CanonicalPayload, b.CanonicalPayload);
        Assert.Equal(a.ConfigurationFingerprint, b.ConfigurationFingerprint);
        Assert.StartsWith("sha256:", a.ConfigurationFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundle_id_and_created_at_are_excluded_from_fingerprint()
    {
        var request = SetupTestFixtures.LocalMailpitRequest(PlaceholderRoot(), dryRun: true);
        var first = SetupConfigurationMaterializer.Materialize(request, "id-1", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var second = SetupConfigurationMaterializer.Materialize(request, "id-2", DateTimeOffset.Parse("2026-12-31T23:59:59Z"));

        Assert.Equal(first.ConfigurationFingerprint, second.ConfigurationFingerprint);
        Assert.NotEqual(first.Recorded.BundleId, second.Recorded.BundleId);
        Assert.NotEqual(first.Recorded.CreatedAt, second.Recorded.CreatedAt);
    }

    [Fact]
    public void Secret_value_changes_do_not_change_non_secret_fingerprint()
    {
        var root = PlaceholderRoot();
        var first = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        var second = new SetupRequest
        {
            Mode = first.Mode,
            ManagedRootPath = first.ManagedRootPath,
            DryRun = true,
            Tenants = first.Tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "a-completely-different-secret-value",
            },
            MetricsBearerToken = "another-metrics-secret",
        };

        var a = SetupConfigurationMaterializer.Materialize(first, "b1", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var b = SetupConfigurationMaterializer.Materialize(second, "b1", DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        Assert.Equal(a.ConfigurationFingerprint, b.ConfigurationFingerprint);
    }
}
