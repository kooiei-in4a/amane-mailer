using System.Text.Json;
using Amane.Mailer.Json;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Shared runtime configuration load/validation used by ASP.NET startup and setup inspect-effective
/// (ADR 0021 D-05 sameness). Failures are classified without echoing secret values.
/// </summary>
public sealed class MailerConfigurationSnapshot
{
    public required string TenantsPath { get; init; }
    public required MailerTenantsFile TenantsFile { get; init; }
    public required MailerTenantRegistry Registry { get; init; }
    public required MailerOptions Options { get; init; }

    // Compatibility alias used by inspect/command code.
    // Keep numeric values aligned with MailerConfigurationLoadFailureKind.
    public enum LoadFailureKind
    {
        None = 0,
        TenantsMissing = 1,
        TenantsInvalid = 2,
        TokenMissing = 3,
        WebhookSecretMissing = 4,
        ProviderInvalid = 5,
        AcsCredentialMissing = 6,
        MailpitInvalid = 7,
    }

    public readonly record struct LoadResult(
        bool Succeeded,
        MailerConfigurationSnapshot? Snapshot,
        LoadFailureKind FailureKind);

    public static MailerConfigurationSnapshot Load(IConfiguration configuration, string environmentName)
    {
        var result = TryLoad(configuration, environmentName);
        if (result.Succeeded && result.Snapshot is not null)
        {
            return result.Snapshot;
        }

        throw new MailerConfigurationLoadException(
            (MailerConfigurationLoadFailureKind)result.FailureKind,
            FailureMessage(result.FailureKind));
    }

    public static LoadResult TryLoad(IConfiguration configuration, string environmentName)
    {
        string tenantsPath;
        MailerTenantsFile tenantsFile;
        try
        {
            tenantsPath = MailerTenantRegistry.ResolveTenantsPath(configuration);
            if (!File.Exists(tenantsPath))
            {
                return new LoadResult(false, null, LoadFailureKind.TenantsMissing);
            }

            tenantsFile = MailerTenantRegistry.LoadTenantsFile(tenantsPath);
        }
        catch (MailerConfigurationLoadException ex)
        {
            return new LoadResult(false, null, (LoadFailureKind)ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, LoadFailureKind.TenantsInvalid);
        }
        catch (JsonException)
        {
            return new LoadResult(false, null, LoadFailureKind.TenantsInvalid);
        }

        MailerTenantRegistry registry;
        try
        {
            registry = MailerTenantRegistry.LoadFromTenantsFile(
                configuration,
                environmentName,
                tenantsPath,
                tenantsFile);
        }
        catch (MailerConfigurationLoadException ex)
        {
            return new LoadResult(false, null, (LoadFailureKind)ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, LoadFailureKind.TenantsInvalid);
        }

        MailerOptions options;
        try
        {
            options = MailerOptions.Load(configuration);
            options.ValidateEffectiveProviders(registry.ListTenants());
        }
        catch (MailerConfigurationLoadException ex)
        {
            return new LoadResult(false, null, (LoadFailureKind)ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, LoadFailureKind.ProviderInvalid);
        }

        return new LoadResult(
            true,
            new MailerConfigurationSnapshot
            {
                TenantsPath = tenantsPath,
                TenantsFile = tenantsFile,
                Registry = registry,
                Options = options,
            },
            LoadFailureKind.None);
    }

    private static string FailureMessage(LoadFailureKind kind) => kind switch
    {
        LoadFailureKind.TenantsMissing => "Mailer tenant configuration file does not exist.",
        LoadFailureKind.TenantsInvalid => "Mailer tenant configuration is invalid.",
        LoadFailureKind.TokenMissing =>
            "A tenant authentication token is missing or contains a known placeholder value.",
        LoadFailureKind.WebhookSecretMissing => "A tenant webhook secret environment variable is missing.",
        LoadFailureKind.ProviderInvalid => "Mailer provider configuration is invalid.",
        LoadFailureKind.AcsCredentialMissing => "ACS connection string is required for live-sending ACS tenants.",
        LoadFailureKind.MailpitInvalid => "Mailpit SMTP configuration is invalid.",
        _ => "Mailer configuration load failed.",
    };
}
