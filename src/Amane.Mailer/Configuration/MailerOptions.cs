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
            AcsConnectionString = configuration["ACS_CONNECTION_STRING"] ?? string.Empty,
        };
    }

    public string ResolveProvider(MailerTenant tenant) =>
        string.IsNullOrWhiteSpace(ProviderOverride)
            ? tenant.Provider
            : ProviderOverride;
}
