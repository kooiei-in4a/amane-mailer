using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Identity;
using Amane.Mailer.Json;

namespace Amane.Mailer.Configuration;

public sealed class MailerTenantRegistry
{
    private readonly IReadOnlyList<MailerTenant> _tenants;
    private readonly IReadOnlyDictionary<Guid, MailerTenant> _tenantsById;
    private readonly IReadOnlyDictionary<Guid, string> _tokensByTenantId;
    private readonly IReadOnlyDictionary<Guid, string> _webhookSecretsByTenantId;

    private MailerTenantRegistry(
        IReadOnlyList<MailerTenant> tenants,
        IReadOnlyDictionary<Guid, MailerTenant> tenantsById,
        IReadOnlyDictionary<Guid, string> tokensByTenantId,
        IReadOnlyDictionary<Guid, string> webhookSecretsByTenantId)
    {
        _tenants = tenants;
        _tenantsById = tenantsById;
        _tokensByTenantId = tokensByTenantId;
        _webhookSecretsByTenantId = webhookSecretsByTenantId;
    }

    public static string ResolveTenantsPath(IConfiguration configuration) =>
        configuration["Mailer:TenantsPath"]
            ?? configuration["MAILER_TENANTS_PATH"]
            ?? Path.Combine(AppContext.BaseDirectory, "config", "mailer", "tenants.example.json");

    public static MailerTenantsFile LoadTenantsFile(string tenantsPath)
    {
        if (!File.Exists(tenantsPath))
        {
            throw new InvalidOperationException($"Mailer tenant configuration file does not exist: {tenantsPath}");
        }

        var tenantFile = JsonSerializer.Deserialize(
            File.ReadAllText(tenantsPath),
            MailerJsonContext.Default.MailerTenantsFile)
            ?? throw new InvalidOperationException("Mailer tenant configuration file is empty.");

        tenantFile = tenantFile.WithJsonDefaultsApplied();
        tenantFile.Validate();
        foreach (var tenant in tenantFile.Tenants)
        {
            tenant.Validate();
        }

        return tenantFile;
    }

    public static MailerTenantRegistry Load(IConfiguration configuration, string environmentName)
    {
        var tenantsPath = ResolveTenantsPath(configuration);
        var tenantFile = LoadTenantsFile(tenantsPath);
        return LoadFromTenantsFile(configuration, environmentName, tenantsPath, tenantFile);
    }

    public static MailerTenantRegistry LoadFromTenantsFile(
        IConfiguration configuration,
        string environmentName,
        string tenantsPath,
        MailerTenantsFile tenantFile)
    {
        _ = tenantsPath;
        var tenantsById = new Dictionary<Guid, MailerTenant>();
        var tokensByTenantId = new Dictionary<Guid, string>();
        var webhookSecretsByTenantId = new Dictionary<Guid, string>();

        foreach (var tenant in tenantFile.Tenants)
        {
            if (!tenantsById.TryAdd(tenant.TenantId, tenant))
            {
                throw new InvalidOperationException($"Duplicate tenant_id: {tenant.TenantId}");
            }

            var token = configuration[tenant.TokenEnv]
                ?? Environment.GetEnvironmentVariable(tenant.TokenEnv);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new MailerConfigurationLoadException(
                    MailerConfigurationLoadFailureKind.TokenMissing,
                    $"Environment variable '{tenant.TokenEnv}' must be set for tenant '{tenant.Name}'.");
            }

            if (MailerMetricsOptions.RequiresStrictTenantTokenValidation(environmentName)
                && ConfigurationPlaceholderDetector.LooksLikePlaceholder(token))
            {
                throw new MailerConfigurationLoadException(
                    MailerConfigurationLoadFailureKind.TokenMissing,
                    $"Environment variable '{tenant.TokenEnv}' contains a known placeholder value for tenant '{tenant.Name}'.");
            }

            tokensByTenantId.Add(tenant.TenantId, token);

            if (tenant.Webhook is not null)
            {
                var webhookSecret = configuration[tenant.Webhook.SecretEnv]
                    ?? Environment.GetEnvironmentVariable(tenant.Webhook.SecretEnv);

                if (string.IsNullOrWhiteSpace(webhookSecret))
                {
                    throw new MailerConfigurationLoadException(
                        MailerConfigurationLoadFailureKind.WebhookSecretMissing,
                        $"Environment variable '{tenant.Webhook.SecretEnv}' must be set for tenant '{tenant.Name}' webhook delivery.");
                }

                webhookSecretsByTenantId.Add(tenant.TenantId, webhookSecret);
            }
        }

        return new MailerTenantRegistry(
            tenantsById.Values
                .OrderBy(tenant => tenant.Name, StringComparer.Ordinal)
                .ToArray(),
            tenantsById,
            tokensByTenantId,
            webhookSecretsByTenantId);
    }

    /// <summary>
    /// Narrow compatibility registry for an initialized v2 instance that has no legacy
    /// tenants.json. API authentication and sender identity remain DB-owned; this template only
    /// supplies the retained dispatcher DTO shape.
    /// </summary>
    public static MailerTenantRegistry CreateManaged(InstanceRuntimeState state)
    {
        if (!string.Equals(state.ProviderType, "acs", StringComparison.Ordinal))
        {
            throw new MailerConfigurationLoadException(
                MailerConfigurationLoadFailureKind.ProviderInvalid,
                "Managed instance provider state is invalid.");
        }

        const string provider = "acs";
        var tenant = new MailerTenant
        {
            TenantId = V2PersistenceCompatibility.SuppressionScopeId,
            Name = "managed-instance",
            SourceServices = [V2PersistenceCompatibility.SourceService],
            DefaultFrom = new MailerAddress
            {
                Email = "noreply@example.invalid",
                DisplayName = "Amane Mailer",
            },
            TokenEnv = "MANAGED_API_KEY",
            Provider = provider,
            LiveSending = state.LiveSending,
            MetadataMaxBytes = MailerTenant.DefaultMetadataMaxBytes,
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 300,
            },
        };

        return new MailerTenantRegistry(
            [tenant],
            new Dictionary<Guid, MailerTenant> { [tenant.TenantId] = tenant },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>());
    }

    public MailerTenant? Authorize(Guid tenantId, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)
            || !_tenantsById.TryGetValue(tenantId, out var tenant)
            || !_tokensByTenantId.TryGetValue(tenantId, out var expectedToken))
        {
            return null;
        }

        return ConstantTimeEquals(expectedToken, bearerToken) ? tenant : null;
    }

    public MailerTenant? Find(Guid tenantId) =>
        _tenantsById.GetValueOrDefault(tenantId);

    public string? GetWebhookSecret(Guid tenantId) =>
        _webhookSecretsByTenantId.GetValueOrDefault(tenantId);

    public IReadOnlyList<MailerTenant> ListTenants() => _tenants;

    private static bool ConstantTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedHash = SHA256.HashData(expectedBytes);
        var actualHash = SHA256.HashData(actualBytes);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
