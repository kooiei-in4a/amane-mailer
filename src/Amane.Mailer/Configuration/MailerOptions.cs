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
