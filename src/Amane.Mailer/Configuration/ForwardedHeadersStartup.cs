using Microsoft.AspNetCore.HttpOverrides;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Wires ASP.NET Core forwarded-header processing when operators enable the existing
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> compose contract (TLS-terminating reverse proxy).
/// </summary>
internal static class ForwardedHeadersStartup
{
    internal const string EnabledKey = "ASPNETCORE_FORWARDEDHEADERS_ENABLED";

    internal static bool IsEnabled(IConfiguration configuration) =>
        ConfigurationBooleanReader.Read(configuration, EnabledKey, defaultValue: false);

    /// <summary>
    /// Registers forwarded-header options. KnownIPNetworks/Proxies are cleared so a private-network
    /// approved reverse proxy (Docker Compose / Caddy / nginx) can supply <c>X-Forwarded-Proto</c>.
    /// Only enable behind that trusted boundary — never on a directly internet-exposed listener.
    /// </summary>
    internal static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    /// <summary>
    /// Must run before endpoints that consult <see cref="HttpRequest.IsHttps"/> (Admin cookies /
    /// antiforgery SecurePolicy.Always under Production HTTPS).
    /// </summary>
    internal static void UseIfEnabled(WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            return;
        }

        app.UseForwardedHeaders();
    }
}
