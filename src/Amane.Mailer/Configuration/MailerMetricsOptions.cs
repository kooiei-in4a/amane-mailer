using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Configuration;

public sealed record MailerMetricsOptions
{
    public bool Enabled { get; init; } = true;

    public string? BearerToken { get; init; }

    public static MailerMetricsOptions Load(IConfiguration configuration)
    {
        var bearerToken = configuration["Mailer:Metrics:BearerToken"];
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            bearerToken = configuration["MAILER_METRICS_BEARER_TOKEN"];
        }

        bearerToken = string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken.Trim();

        return new MailerMetricsOptions
        {
            Enabled = configuration.GetValue("Mailer:Metrics:Enabled", true),
            BearerToken = bearerToken,
        };
    }
}
