using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

/// <summary>
/// Builds normalized non-secret compose.env, secret-valued secrets.env, tenants.json,
/// platform-sender.json, and Admin representation without executing Admin bootstrap.
/// </summary>
public static class SetupConfigurationMaterializer
{
    public sealed class MaterializedBundleContent
    {
        public required string BundleId { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required IReadOnlyDictionary<string, string> ComposeEnv { get; init; }
        public required IReadOnlyDictionary<string, string> SecretsEnv { get; init; }
        public required string TenantsJson { get; init; }
        public string? PlatformSenderJson { get; init; }
        public PlatformSenderFile? PlatformSender { get; init; }
        public byte[]? AcsConnectionStringBytes { get; init; }
        public required string ConfigurationFingerprint { get; init; }
        public required byte[] CanonicalPayload { get; init; }
        public required SetupRecordedMetadata Recorded { get; init; }
        public required bool AdminBootstrapRequested { get; init; }
    }

    public static MaterializedBundleContent Materialize(
        SetupRequest request,
        string bundleId,
        DateTimeOffset createdAt)
    {
        var compose = BuildDefaultComposeEnv(request, bundleId);
        foreach (var pair in request.PublicEnvOverrides)
        {
            compose[pair.Key] = pair.Value;
        }

        // Path bindings always point at this immutable bundle (relative to managed root semantics
        // are finalized by host adapter #449; Core records bundle-relative host path placeholders).
        compose["MAILER_TENANTS_HOST_PATH"] = $"bundles/{bundleId}/config/{SetupBundleLayout.TenantsFileName}";
        compose["MAILER_TENANTS_CONTAINER_PATH"] = SetupBundleLayout.ContainerTenantsPath;
        compose["MAILER_SETUP_RECORDED_METADATA_PATH"] = SetupBundleLayout.ContainerRecordedMetadataPath;

        if (request.Mode != SetupMode.LocalMailpit)
        {
            compose["MAILER_ACS_SECRET_HOST_PATH"] = $"bundles/{bundleId}/secrets";
            compose["MAILER_PLATFORM_SENDER_HOST_PATH"] = $"bundles/{bundleId}/config";
        }

        ApplyAdminRepresentation(compose, request.Admin);

        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.TokenSecrets)
        {
            secrets[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrEmpty(request.MetricsBearerToken))
        {
            secrets["MAILER_METRICS_BEARER_TOKEN"] = request.MetricsBearerToken;
        }

        // Admin password hash is intentionally omitted (#459 ownership).
        secrets.Remove("AMANE_ADMIN_PASSWORD_HASH");

        PlatformSenderFile? platformSender = null;
        string? platformSenderJson = null;
        byte[]? acsBytes = null;
        if (request.PlatformSender is not null && !string.IsNullOrEmpty(request.AcsConnectionString))
        {
            platformSender = SetupRequestValidator.BuildPlatformSender(request.PlatformSender);
            platformSenderJson = JsonSerializer.Serialize(platformSender, SetupJsonContext.Default.PlatformSenderFile);
            acsBytes = Encoding.UTF8.GetBytes(request.AcsConnectionString);
        }

        var tenantsJson = JsonSerializer.Serialize(request.Tenants, SetupJsonContext.Default.MailerTenantsFile);
        var adminRequested = request.Admin?.Enabled == true;

        // Fingerprint must exclude bundle id (ADR 0021 / #448). Written compose.env keeps real
        // bundle-specific path bindings; canonical form substitutes a stable placeholder.
        var fingerprintCompose = new SortedDictionary<string, string>(compose, StringComparer.Ordinal);
        foreach (var key in fingerprintCompose.Keys.ToList())
        {
            fingerprintCompose[key] = fingerprintCompose[key]
                .Replace($"bundles/{bundleId}/", "bundles/<bundle-id>/", StringComparison.Ordinal);
        }

        var canonical = SetupCanonicalPayload.Build(
            request.Mode,
            request.Tenants,
            fingerprintCompose,
            platformSender,
            adminRequested);
        var fingerprint = SetupCanonicalPayload.FingerprintSha256(canonical);

        var recorded = new SetupRecordedMetadata
        {
            SchemaVersion = SetupBundleLayout.RecordedSchemaVersion,
            BundleId = bundleId,
            ConfigurationFingerprint = fingerprint,
            Mode = SetupModeParser.ToWireValue(request.Mode),
            CreatedAt = createdAt.UtcDateTime.ToString("o"),
            ImageRepository = compose.GetValueOrDefault("MAILER_IMAGE_REPOSITORY"),
            ImageTag = compose.GetValueOrDefault("MAILER_IMAGE_TAG"),
            PlatformSenderPresent = platformSender is not null,
            AdminBootstrapRequested = adminRequested,
        };

        return new MaterializedBundleContent
        {
            BundleId = bundleId,
            CreatedAt = createdAt,
            ComposeEnv = compose,
            SecretsEnv = secrets,
            TenantsJson = tenantsJson,
            PlatformSenderJson = platformSenderJson,
            PlatformSender = platformSender,
            AcsConnectionStringBytes = acsBytes,
            ConfigurationFingerprint = fingerprint,
            CanonicalPayload = canonical,
            Recorded = recorded,
            AdminBootstrapRequested = adminRequested,
        };
    }

    public static string FormatEnvFile(IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder();
        foreach (var pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(pair.Key);
            sb.Append('=');
            sb.Append(EscapeEnvValue(pair.Value));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> BuildDefaultComposeEnv(SetupRequest request, string bundleId)
    {
        var compose = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COMPOSE_PROJECT_NAME"] = "amane-mailer",
            ["MAILER_IMAGE_REPOSITORY"] = request.ImageRepository ?? "ghcr.io/kooiei-in4a/amane-mailer",
            ["MAILER_IMAGE_TAG"] = request.ImageTag ?? "replace-with-published-git-sha",
            ["MAILER_PULL_POLICY"] = "always",
            ["MAILER_MEM_LIMIT"] = "512m",
            ["MAILER_CPUS"] = "1.0",
            ["MAILER_STOP_GRACE_PERIOD"] = "120s",
            ["MAILER_HTTP_PORT"] = "8080",
            ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false",
            ["MAILER_NETWORK_NAME"] = "amane_mailer",
            ["MAILER_NETWORK_ALIAS"] = "mailer",
            ["MAILER_HEALTHCHECK_INTERVAL"] = "30s",
            ["MAILER_HEALTHCHECK_TIMEOUT"] = "5s",
            ["MAILER_HEALTHCHECK_RETRIES"] = "5",
            ["MAILER_HEALTHCHECK_START_PERIOD"] = "20s",
            ["LOG_MAX_SIZE"] = "10m",
            ["LOG_MAX_FILE"] = "5",
            ["MAILER_PROVIDER"] = string.Empty,
            ["MAILER_BOUNCE_INGESTION"] = "off",
            ["MAILER_BOUNCE_QUEUE_NAME"] = string.Empty,
            ["MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH"] = "./secrets/bounce-queue",
            ["MAILER_RETENTION_DAYS"] = "90",
            ["MAILER_RETENTION_SWEEP_INTERVAL_HOURS"] = "24",
            ["MAILER_METRICS_ENABLED"] = "true",
            ["AMANE_ADMIN_ENABLED"] = "false",
            ["AMANE_ADMIN_USERNAME"] = "admin",
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_ALLOW_HTTP"] = "false",
            ["AMANE_ADMIN_PII_LIST_MODE"] = "masked",
        };

        _ = bundleId;
        return compose;
    }

    private static void ApplyAdminRepresentation(
        IDictionary<string, string> compose,
        SetupAdminBootstrapRepresentation? admin)
    {
        if (admin is null)
        {
            return;
        }

        compose["AMANE_ADMIN_ENABLED"] = admin.Enabled ? "true" : "false";
        compose["AMANE_ADMIN_USERNAME"] = admin.Username;
        compose["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = admin.AllowedLocalAddress;
        compose["AMANE_ADMIN_ALLOW_HTTP"] = admin.AllowHttp ? "true" : "false";
        compose["AMANE_ADMIN_PII_LIST_MODE"] = admin.PiiListMode;
    }

    private static string EscapeEnvValue(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.IndexOfAny([' ', '#', '"', '\'', '\n', '\r', '\t']) >= 0)
        {
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
