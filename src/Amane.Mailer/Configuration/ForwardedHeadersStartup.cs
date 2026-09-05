using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Wires ASP.NET Core forwarded-header processing when operators enable the existing
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> compose contract (TLS-terminating reverse proxy).
/// </summary>
internal static class ForwardedHeadersStartup
{
    internal const string EnabledKey = "ASPNETCORE_FORWARDEDHEADERS_ENABLED";
    internal const string TrustedProxiesKey = "MAILER_FORWARDED_HEADERS_TRUSTED_PROXIES";
    internal const string TrustedNetworksKey = "MAILER_FORWARDED_HEADERS_TRUSTED_NETWORKS";

    internal static bool IsEnabled(IConfiguration configuration) =>
        ConfigurationBooleanReader.Read(configuration, EnabledKey, defaultValue: false);

    /// <summary>
    /// Registers forwarded-header options. The ASP.NET Core default loopback trust remains in
    /// place, and operators may add explicit proxy IPs/CIDRs. Clearing the framework trust lists
    /// would turn an arbitrary client-supplied X-Forwarded-Proto into HTTPS.
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
            foreach (var proxy in ReadValues(configuration, TrustedProxiesKey))
            {
                if (!IPAddress.TryParse(proxy, out var address))
                {
                    throw new InvalidOperationException(
                        $"{TrustedProxiesKey} must contain only IP addresses.");
                }

                options.KnownProxies.Add(address);
            }

            foreach (var network in ReadValues(configuration, TrustedNetworksKey))
            {
                var separator = network.LastIndexOf('/');
                if (separator <= 0
                    || !IPAddress.TryParse(network[..separator], out var address)
                    || !int.TryParse(network[(separator + 1)..], out var prefixLength)
                    || prefixLength < 0
                    || prefixLength > address.GetAddressBytes().Length * 8)
                {
                    throw new InvalidOperationException(
                        $"{TrustedNetworksKey} must contain CIDR networks.");
                }

                options.KnownIPNetworks.Add(new System.Net.IPNetwork(address, prefixLength));
            }
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

    private static IEnumerable<string> ReadValues(
        IConfiguration configuration,
        string key) =>
        (configuration[key] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
