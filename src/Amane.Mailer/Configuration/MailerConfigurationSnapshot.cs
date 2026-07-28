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

    public enum LoadFailureKind
    {
        None = 0,
        TenantsMissing,
        TenantsInvalid,
        TokenMissing,
        WebhookSecretMissing,
        ProviderInvalid,
        AcsCredentialMissing,
        MailpitInvalid,
    }

    public readonly record struct LoadResult(
        bool Succeeded,
        MailerConfigurationSnapshot? Snapshot,
        LoadFailureKind FailureKind);

    /// <summary>
    /// Loads and validates configuration exactly as runtime DI does for tenants + MailerOptions.
    /// Throws on failure (startup path).
    /// </summary>
    public static MailerConfigurationSnapshot Load(IConfiguration configuration)
    {
        var result = TryLoad(configuration);
        if (result.Succeeded && result.Snapshot is not null)
        {
            return result.Snapshot;
        }

        throw new InvalidOperationException(FailureMessage(result.FailureKind));
    }

    /// <summary>
    /// Same load/validation as <see cref="Load"/> but returns a classified failure for CLI inspection.
    /// </summary>
    public static LoadResult TryLoad(IConfiguration configuration)
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
            registry = MailerTenantRegistry.LoadFromTenantsFile(configuration, tenantsPath, tenantsFile);
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message;
            if (message.Contains("webhook", StringComparison.OrdinalIgnoreCase))
            {
                return new LoadResult(false, null, LoadFailureKind.WebhookSecretMissing);
            }

            if (message.Contains("must be set for tenant", StringComparison.Ordinal))
            {
                return new LoadResult(false, null, LoadFailureKind.TokenMissing);
            }

            return new LoadResult(false, null, LoadFailureKind.TenantsInvalid);
        }

        MailerOptions options;
        try
        {
            options = MailerOptions.Load(configuration);
            options.ValidateEffectiveProviders(registry.ListTenants());
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message;
            if (message.Contains("ACS connection string", StringComparison.Ordinal)
                || message.Contains("ACS_CONNECTION_STRING", StringComparison.Ordinal))
            {
                return new LoadResult(false, null, LoadFailureKind.AcsCredentialMissing);
            }

            if (message.Contains("mailpit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("MAILPIT_", StringComparison.Ordinal)
                || message.Contains("SmtpPort", StringComparison.Ordinal)
                || message.Contains("SmtpHost", StringComparison.Ordinal))
            {
                return new LoadResult(false, null, LoadFailureKind.MailpitInvalid);
            }

            if (message.Contains("provider", StringComparison.OrdinalIgnoreCase))
            {
                return new LoadResult(false, null, LoadFailureKind.ProviderInvalid);
            }

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
        LoadFailureKind.TokenMissing => "A tenant token environment variable is missing.",
        LoadFailureKind.WebhookSecretMissing => "A tenant webhook secret environment variable is missing.",
        LoadFailureKind.ProviderInvalid => "Mailer provider configuration is invalid.",
        LoadFailureKind.AcsCredentialMissing => "ACS connection string is required for live-sending ACS tenants.",
        LoadFailureKind.MailpitInvalid => "Mailpit SMTP configuration is invalid.",
        _ => "Mailer configuration load failed.",
    };
}
