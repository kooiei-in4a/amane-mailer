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

    public readonly record struct LoadResult(
        bool Succeeded,
        MailerConfigurationSnapshot? Snapshot,
        MailerConfigurationLoadFailureKind FailureKind);

    public static MailerConfigurationSnapshot Load(
        IConfiguration configuration,
        string environmentName) =>
        Load(configuration, environmentName, instanceState: null);

    public static MailerConfigurationSnapshot Load(
        IConfiguration configuration,
        string environmentName,
        InstanceRuntimeState? instanceState)
    {
        // A completed browser setup owns provider/sender state in SQLite and intentionally does
        // not depend on the legacy tenants.json. The retained registry is only a narrow DTO
        // bridge for the dispatcher; API identity remains in SenderRepository.
        if (instanceState?.IsInitialized == true && instanceState.HasInstanceOwner)
        {
            return CreateManagedSnapshot(configuration, instanceState);
        }

        var result = TryLoad(configuration, environmentName, instanceState);
        if (result.Succeeded && result.Snapshot is not null)
        {
            return result.Snapshot;
        }

        throw new MailerConfigurationLoadException(
            result.FailureKind,
            FailureMessage(result.FailureKind));
    }

    private static MailerConfigurationSnapshot CreateManagedSnapshot(
        IConfiguration configuration,
        InstanceRuntimeState instanceState)
    {
        var options = MailerOptions.Load(configuration, instanceState);
        var registry = MailerTenantRegistry.CreateManaged(instanceState);
        options.ValidateEffectiveProviders(registry.ListTenants());
        return new MailerConfigurationSnapshot
        {
            TenantsPath = MailerTenantRegistry.ResolveTenantsPath(configuration),
            TenantsFile = new MailerTenantsFile
            {
                Version = 1,
                Environment = "shared",
                Tenants = registry.ListTenants(),
            },
            Registry = registry,
            Options = options,
        };
    }

    public static LoadResult TryLoad(IConfiguration configuration, string environmentName) =>
        TryLoad(configuration, environmentName, instanceState: null);

    public static LoadResult TryLoad(
        IConfiguration configuration,
        string environmentName,
        InstanceRuntimeState? instanceState)
    {
        string tenantsPath;
        MailerTenantsFile tenantsFile;
        try
        {
            tenantsPath = MailerTenantRegistry.ResolveTenantsPath(configuration);
            if (!File.Exists(tenantsPath))
            {
                return new LoadResult(false, null, MailerConfigurationLoadFailureKind.TenantsMissing);
            }

            tenantsFile = MailerTenantRegistry.LoadTenantsFile(tenantsPath);
        }
        catch (MailerConfigurationLoadException ex)
        {
            return new LoadResult(false, null, ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, MailerConfigurationLoadFailureKind.TenantsInvalid);
        }
        catch (JsonException)
        {
            return new LoadResult(false, null, MailerConfigurationLoadFailureKind.TenantsInvalid);
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
            return new LoadResult(false, null, ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, MailerConfigurationLoadFailureKind.TenantsInvalid);
        }

        MailerOptions options;
        try
        {
            options = MailerOptions.Load(configuration, instanceState);
            options.ValidateEffectiveProviders(registry.ListTenants());
        }
        catch (MailerConfigurationLoadException ex)
        {
            return new LoadResult(false, null, ex.Kind);
        }
        catch (InvalidOperationException)
        {
            return new LoadResult(false, null, MailerConfigurationLoadFailureKind.ProviderInvalid);
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
            MailerConfigurationLoadFailureKind.None);
    }

    private static string FailureMessage(MailerConfigurationLoadFailureKind kind) => kind switch
    {
        MailerConfigurationLoadFailureKind.None => "Mailer configuration load failed.",
        MailerConfigurationLoadFailureKind.TenantsMissing => "Mailer tenant configuration file does not exist.",
        MailerConfigurationLoadFailureKind.TenantsInvalid => "Mailer tenant configuration is invalid.",
        MailerConfigurationLoadFailureKind.TokenMissing =>
            "A tenant authentication token is missing or contains a known placeholder value.",
        MailerConfigurationLoadFailureKind.WebhookSecretMissing => "A tenant webhook secret environment variable is missing.",
        MailerConfigurationLoadFailureKind.ProviderInvalid => "Mailer provider configuration is invalid.",
        MailerConfigurationLoadFailureKind.AcsCredentialMissing => "ACS connection string is required for live-sending ACS tenants.",
        MailerConfigurationLoadFailureKind.MailpitInvalid => "Mailpit SMTP configuration is invalid.",
    };
}
