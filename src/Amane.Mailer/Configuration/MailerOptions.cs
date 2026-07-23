namespace Amane.Mailer.Configuration;

public sealed record MailerOptions
{
    public string ProviderOverride { get; init; } = string.Empty;

    public string MailpitSmtpHost { get; init; } = "mailpit";

    public int MailpitSmtpPort { get; init; } = 1025;

    public bool MailpitUseSsl { get; init; }

    public string AcsConnectionString { get; init; } = string.Empty;

    public static MailerOptions Load(IConfiguration configuration)
    {
        return new()
        {
            ProviderOverride = configuration["MAILER_PROVIDER"]
                ?? configuration["Mailer:Provider"]
                ?? string.Empty,
            MailpitSmtpHost = configuration["Mailer:Mailpit:SmtpHost"]
                ?? configuration["MAILPIT_SMTP_HOST"]
                ?? "mailpit",
            MailpitSmtpPort = configuration.GetValue(
                "Mailer:Mailpit:SmtpPort",
                configuration.GetValue("MAILPIT_SMTP_PORT", 1025)),
            MailpitUseSsl = configuration.GetValue(
                "Mailer:Mailpit:UseSsl",
                configuration.GetValue("MAILPIT_SMTP_USE_SSL", false)),
            AcsConnectionString = ResolveAcsConnectionString(configuration),
        };
    }

    public string ResolveProvider(MailerTenant tenant) =>
        string.IsNullOrWhiteSpace(ProviderOverride)
            ? tenant.Provider
            : ProviderOverride;

    /// <summary>
    /// Fail closed at process startup for unknown effective providers and for ACS live-sending
    /// tenants that lack a connection string. Matches offline
    /// <c>scripts/validate-tenant-config.mjs</c> ACS policy (<c>live_sending=true</c> only).
    /// This check is startup-only; <c>/readyz</c> does not re-validate providers.
    /// </summary>
    public void ValidateEffectiveProviders(IReadOnlyList<MailerTenant> tenants)
    {
        if (!string.IsNullOrWhiteSpace(ProviderOverride) && !IsKnownProvider(ProviderOverride))
        {
            throw new InvalidOperationException(
                "MAILER_PROVIDER / Mailer:Provider must be 'mailpit' or 'acs' when set "
                + $"(got '{ProviderOverride}').");
        }

        foreach (var tenant in tenants)
        {
            var provider = ResolveProvider(tenant);
            if (!IsKnownProvider(provider))
            {
                throw new InvalidOperationException(
                    $"tenant '{tenant.Name}' effective provider must be 'mailpit' or 'acs' "
                    + $"(got '{provider}').");
            }

            if (provider.Equals("acs", StringComparison.Ordinal)
                && tenant.LiveSending
                && string.IsNullOrWhiteSpace(AcsConnectionString))
            {
                throw new InvalidOperationException(
                    $"ACS connection string is required when tenant '{tenant.Name}' uses "
                    + "effective provider 'acs' with live_sending=true. Configure "
                    + "ACS_CONNECTION_STRING_FILE or ACS_CONNECTION_STRING.");
            }
        }
    }

    private static bool IsKnownProvider(string provider) =>
        provider.Equals("mailpit", StringComparison.Ordinal)
        || provider.Equals("acs", StringComparison.Ordinal);

    /// <summary>
    /// Staging/Production deploy (<c>infra/deploy/compose.yml</c>) wires
    /// <c>ACS_CONNECTION_STRING_FILE</c> (see <see cref="Operations.AdminProviderRegisterAcsCommand"/>
    /// and the Compose boundary test guarding this) and sets
    /// <c>MAILER_REQUIRE_ACS_SECRET_FILE=true</c> on the <c>mailer</c> service only. When that flag
    /// is set, a missing/empty secret file fails closed — the bare <c>ACS_CONNECTION_STRING</c>
    /// environment variable is never used as a fallback there, even if present in the process
    /// environment. Without the flag (local Mailpit compose, and the local ACS drill
    /// <c>infra/deploy/drills/mail-05a-acs-drill.sh</c>, which injects
    /// <c>ACS_CONNECTION_STRING</c> via its own compose override and unsets it on exit), the bare
    /// env var remains a valid fallback, matching existing drill behavior unchanged.
    /// </summary>
    private static string ResolveAcsConnectionString(IConfiguration configuration)
    {
        var fileValue = ReadAcsConnectionStringFile(configuration);
        if (!string.IsNullOrEmpty(fileValue))
        {
            return fileValue;
        }

        var requiresSecretFile = configuration.GetValue("MAILER_REQUIRE_ACS_SECRET_FILE", false);
        if (requiresSecretFile)
        {
            return string.Empty;
        }

        return configuration["ACS_CONNECTION_STRING"] ?? string.Empty;
    }

    private static string ReadAcsConnectionStringFile(IConfiguration configuration)
    {
        var filePath = configuration["ACS_CONNECTION_STRING_FILE"];
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return string.Empty;
        }

        return File.ReadAllText(filePath).Trim();
    }
}
