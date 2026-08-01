using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupFingerprintComposeNormalizerTests
{
    private const string BundleId = "20260801191256-abcdef01";

    [Fact]
    public void Absolute_windows_host_paths_normalize_to_bundle_relative_placeholders()
    {
        var compose = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAILER_TENANTS_HOST_PATH"] =
                $@"C:\lab\managed\bundles\{BundleId}\config\tenants.json",
            ["MAILER_ACS_SECRET_HOST_PATH"] =
                $@"C:\lab\managed\bundles\{BundleId}\secrets",
        };

        var normalized = SetupFingerprintComposeNormalizer.Normalize(compose, BundleId);

        Assert.Equal("bundles/<bundle-id>/config/tenants.json", normalized["MAILER_TENANTS_HOST_PATH"]);
        Assert.Equal("bundles/<bundle-id>/secrets", normalized["MAILER_ACS_SECRET_HOST_PATH"]);
    }

    [Fact]
    public void Managed_runtime_overlay_matches_materialized_fingerprint()
    {
        const string projectName = "amane-local-mailpit";
        const string imageReference = "ghcr.io/kooiei-in4a/amane-mailer@sha256:abc123";
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        var request = new SetupRequest
        {
            Mode = SetupMode.LocalMailpit,
            ManagedRootPath = @"C:\lab\managed",
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "local-mail-service-token",
            },
            ImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            ImageTag = "sha-9d11d2afad8b417f51a28b4a78496d0579626831",
            TrustedComposeProjectName = projectName,
            TrustedMailerImageReference = imageReference,
            MetricsBearerToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        var materialized = SetupConfigurationMaterializer.Materialize(
            request,
            BundleId,
            DateTimeOffset.Parse("2026-08-01T19:12:56Z"));

        var runtimeCompose = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ManagedEnvKeyCatalog.PublicNonSecretKeys)
        {
            if (materialized.ComposeEnv.TryGetValue(key, out var value))
            {
                runtimeCompose[key] = value;
            }
        }

        runtimeCompose["COMPOSE_PROJECT_NAME"] = projectName;
        runtimeCompose["MAILER_PULL_POLICY"] = "never";
        runtimeCompose["MAILER_IMAGE_REFERENCE"] = imageReference;
        runtimeCompose["MAILER_TENANTS_HOST_PATH"] =
            $@"C:\lab\managed\bundles\{BundleId}\config\tenants.json";
        runtimeCompose["MAILER_ACS_SECRET_HOST_PATH"] =
            $@"C:\lab\managed\bundles\{BundleId}\secrets";
        runtimeCompose["MAILER_PLATFORM_SENDER_HOST_PATH"] =
            $@"C:\lab\managed\bundles\{BundleId}\config";
        runtimeCompose["MAILER_SETUP_RECORDED_METADATA_HOST_PATH"] =
            $@"C:\lab\managed\bundles\{BundleId}\metadata\recorded.json";
        runtimeCompose["MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH"] =
            $@"C:\lab\managed\bundles\{BundleId}\secrets\bounce-queue";

        var normalized = SetupFingerprintComposeNormalizer.Normalize(runtimeCompose, BundleId);
        var effectiveFingerprint = SetupCanonicalPayload.FingerprintSha256(
            SetupCanonicalPayload.BuildForRecordedSchema(
                SetupMode.LocalMailpit,
                tenants,
                normalized,
                platformSender: null,
                adminBootstrapRequested: false,
                SetupBundleLayout.RecordedSchemaVersion));

        Assert.Equal(materialized.ConfigurationFingerprint, effectiveFingerprint);
    }
}
