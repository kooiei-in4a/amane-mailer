using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Json;

namespace Amane.Mailer.Configuration;

public sealed class MailerTenantRegistry
{
    private readonly IReadOnlyList<MailerTenant> _tenants;
    private readonly IReadOnlyDictionary<Guid, MailerTenant> _tenantsById;
    private readonly IReadOnlyDictionary<Guid, string> _tokensByTenantId;

    private MailerTenantRegistry(
        IReadOnlyList<MailerTenant> tenants,
        IReadOnlyDictionary<Guid, MailerTenant> tenantsById,
        IReadOnlyDictionary<Guid, string> tokensByTenantId)
    {
        _tenants = tenants;
        _tenantsById = tenantsById;
        _tokensByTenantId = tokensByTenantId;
    }

    public static MailerTenantRegistry Load(IConfiguration configuration)
    {
        var tenantsPath = configuration["Mailer:TenantsPath"]
            ?? configuration["MAILER_TENANTS_PATH"]
            ?? Path.Combine(AppContext.BaseDirectory, "config", "mailer", "tenants.example.json");

        if (!File.Exists(tenantsPath))
        {
            throw new InvalidOperationException($"Mailer tenant configuration file does not exist: {tenantsPath}");
        }

        var tenantFile = JsonSerializer.Deserialize(
            File.ReadAllText(tenantsPath),
            MailerJsonContext.Default.MailerTenantsFile)
            ?? throw new InvalidOperationException("Mailer tenant configuration file is empty.");

        tenantFile.Validate();

        var tenantsById = new Dictionary<Guid, MailerTenant>();
        var tokensByTenantId = new Dictionary<Guid, string>();

        foreach (var tenant in tenantFile.Tenants)
        {
            tenant.Validate();

            if (!tenantsById.TryAdd(tenant.TenantId, tenant))
            {
                throw new InvalidOperationException($"Duplicate tenant_id: {tenant.TenantId}");
            }

            var token = configuration[tenant.TokenEnv]
                ?? Environment.GetEnvironmentVariable(tenant.TokenEnv);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{tenant.TokenEnv}' must be set for tenant '{tenant.Name}'.");
            }

            tokensByTenantId.Add(tenant.TenantId, token);
        }

        return new MailerTenantRegistry(
            tenantsById.Values
                .OrderBy(tenant => tenant.Name, StringComparer.Ordinal)
                .ToArray(),
            tenantsById,
            tokensByTenantId);
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
