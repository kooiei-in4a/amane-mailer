namespace Amane.Mailer.Configuration;

/// <summary>
/// Shared ACS credential resolution for runtime MailerOptions and setup inspect-effective
/// (ADR 0021 D-05). Value is for internal use only; inspect exposes source/status only.
/// </summary>
public enum MailerAcsCredentialSource
{
    Missing = 0,
    File = 1,
    Environment = 2,
}

public readonly record struct MailerAcsCredentialResolution(
    string Value,
    MailerAcsCredentialSource Source,
    bool RequiredFile);

public static class MailerAcsCredential
{
    public static MailerAcsCredentialResolution Resolve(IConfiguration configuration)
    {
        var requiredFile = configuration.GetValue("MAILER_REQUIRE_ACS_SECRET_FILE", false);
        var filePath = configuration["ACS_CONNECTION_STRING_FILE"];
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var fileValue = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrEmpty(fileValue))
            {
                return new MailerAcsCredentialResolution(
                    fileValue,
                    MailerAcsCredentialSource.File,
                    requiredFile);
            }
        }

        if (requiredFile)
        {
            return new MailerAcsCredentialResolution(
                string.Empty,
                MailerAcsCredentialSource.Missing,
                requiredFile);
        }

        var envValue = configuration["ACS_CONNECTION_STRING"] ?? string.Empty;
        if (!string.IsNullOrEmpty(envValue))
        {
            return new MailerAcsCredentialResolution(
                envValue,
                MailerAcsCredentialSource.Environment,
                requiredFile);
        }

        return new MailerAcsCredentialResolution(
            string.Empty,
            MailerAcsCredentialSource.Missing,
            requiredFile);
    }
}
