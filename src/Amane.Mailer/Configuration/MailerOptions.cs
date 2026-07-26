namespace Amane.Mailer.Configuration;

public sealed record MailerOptions
{
    public string ProviderOverride { get; init; } = string.Empty;

    public string MailpitSmtpHost { get; init; } = "mailpit";

    public int MailpitSmtpPort { get; init; } = 1025;

    public bool MailpitUseSsl { get; init; }

    public string AcsConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Present when <c>Mailer:Mailpit:SmtpHost</c> / <c>MAILPIT_SMTP_HOST</c> was set in configuration.
    /// Used only for startup error messages (never echoes the host value).
    /// </summary>
    internal string? MailpitSmtpHostConfiguredKey { get; init; }

    /// <summary>
    /// Deferred port parse error from Load. Thrown only when any tenant uses effective
    /// provider <c>mailpit</c>, so ACS-only deployments ignore unused Mailpit typos (#356).
    /// </summary>
    internal string? MailpitSmtpPortLoadError { get; init; }

    public static MailerOptions Load(IConfiguration configuration)
    {
        var (host, hostKey) = ResolveMailpitSmtpHost(configuration);
        var (port, portError) = ResolveMailpitSmtpPort(configuration);

        return new()
        {
            ProviderOverride = configuration["MAILER_PROVIDER"]
                ?? configuration["Mailer:Provider"]
                ?? string.Empty,
            MailpitSmtpHost = host,
            MailpitSmtpHostConfiguredKey = hostKey,
            MailpitSmtpPort = port,
            MailpitSmtpPortLoadError = portError,
            MailpitUseSsl = ConfigurationBooleanReader.Read(
                configuration,
                defaultValue: false,
                "Mailer:Mailpit:UseSsl",
                "MAILPIT_SMTP_USE_SSL"),
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
    /// When any tenant uses effective provider <c>mailpit</c>, also require a non-blank SMTP
    /// host and a port in 1–65535 (#356). Unused Mailpit host/port typos do not block ACS-only
    /// startups. This check is startup-only; <c>/readyz</c> does not re-validate providers.
    /// </summary>
    public void ValidateEffectiveProviders(IReadOnlyList<MailerTenant> tenants)
    {
        if (!string.IsNullOrWhiteSpace(ProviderOverride) && !IsKnownProvider(ProviderOverride))
        {
            throw new InvalidOperationException(
                "MAILER_PROVIDER / Mailer:Provider must be 'mailpit' or 'acs' when set "
                + $"(got '{ProviderOverride}').");
        }

        var mailpitInUse = false;

        foreach (var tenant in tenants)
        {
            var provider = ResolveProvider(tenant);
            if (!IsKnownProvider(provider))
            {
                throw new InvalidOperationException(
                    $"tenant '{tenant.Name}' effective provider must be 'mailpit' or 'acs' "
                    + $"(got '{provider}').");
            }

            if (provider.Equals("mailpit", StringComparison.Ordinal))
            {
                mailpitInUse = true;
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

        if (mailpitInUse)
        {
            ValidateMailpitSmtpSettings();
        }
    }

    private void ValidateMailpitSmtpSettings()
    {
        if (string.IsNullOrWhiteSpace(MailpitSmtpHost))
        {
            var key = MailpitSmtpHostConfiguredKey
                ?? "Mailer:Mailpit:SmtpHost / MAILPIT_SMTP_HOST";
            throw new InvalidOperationException(
                $"{key} must be a non-empty host name when any tenant uses "
                + "effective provider 'mailpit'.");
        }

        if (MailpitSmtpPortLoadError is not null)
        {
            throw new InvalidOperationException(MailpitSmtpPortLoadError);
        }

        if (MailpitSmtpPort < ConfigurationIntReader.MinPort
            || MailpitSmtpPort > ConfigurationIntReader.MaxPort)
        {
            throw new InvalidOperationException(
                "Mailer:Mailpit:SmtpPort / MAILPIT_SMTP_PORT must be an integer between "
                + $"{ConfigurationIntReader.MinPort} and {ConfigurationIntReader.MaxPort} "
                + "(inclusive) when any tenant uses effective provider 'mailpit'.");
        }
    }

    private static bool IsKnownProvider(string provider) =>
        provider.Equals("mailpit", StringComparison.Ordinal)
        || provider.Equals("acs", StringComparison.Ordinal);

    private static (string Host, string? ConfiguredKey) ResolveMailpitSmtpHost(
        IConfiguration configuration)
    {
        if (ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                "Mailer:Mailpit:SmtpHost",
                "MAILPIT_SMTP_HOST"))
        {
            return (raw, key);
        }

        return ("mailpit", null);
    }

    private static (int Port, string? LoadError) ResolveMailpitSmtpPort(
        IConfiguration configuration)
    {
        if (!ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                "Mailer:Mailpit:SmtpPort",
                "MAILPIT_SMTP_PORT"))
        {
            return (1025, null);
        }

        try
        {
            var port = ConfigurationIntReader.ParseInRange(
                raw,
                key,
                ConfigurationIntReader.MinPort,
                ConfigurationIntReader.MaxPort);
            return (port, null);
        }
        catch (InvalidOperationException ex)
        {
            // Defer until ValidateEffectiveProviders when mailpit is in use (#356).
            return (1025, ex.Message);
        }
    }

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
