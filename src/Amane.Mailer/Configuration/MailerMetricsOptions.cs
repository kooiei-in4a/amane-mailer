using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
            Enabled = ConfigurationBooleanReader.Read(configuration, "Mailer:Metrics:Enabled", true),
            BearerToken = bearerToken,
        };
    }

    /// <summary>
    /// Fail closed when metrics are enabled outside Development: require a scrape bearer.
    /// WebApplicationFactory hosts use environment name <c>Testing</c> and keep the same
    /// optional-bearer path as Development so automated tests stay anonymous-capable.
    /// </summary>
    public void Validate(string environmentName)
    {
        if (!Enabled)
        {
            return;
        }

        if (AllowsOptionalBearer(environmentName))
        {
            return;
        }

        if (string.IsNullOrEmpty(BearerToken))
        {
            throw new InvalidOperationException(
                "Mailer:Metrics:BearerToken (or MAILER_METRICS_BEARER_TOKEN) must be set when "
                + "Mailer:Metrics:Enabled=true and the host environment is not Development. "
                + "Set a scrape bearer for Production/Staging, or disable metrics with "
                + "Mailer:Metrics:Enabled=false.");
        }
    }

    internal static bool AllowsOptionalBearer(string? environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
}
