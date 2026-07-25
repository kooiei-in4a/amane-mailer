using System.Net;

namespace Amane.Mailer.Admin;

/// <summary>
/// Request-time local address allowlist for Admin routes (ADR 0013 D-03).
/// </summary>
internal static class AdminLocalAddressPolicy
{
    internal static bool IsAllowed(HttpContext context, MailerAdminOptions options) =>
        IsAllowed(context.Connection.LocalIpAddress, options.AllowedLocalAddress);

    internal static bool IsAllowed(IPAddress? requestLocalAddress, string configuredAllowedLocalAddress)
    {
        var localAddress = NormalizeIpAddress(requestLocalAddress);
        if (localAddress is null)
            return false;

        if (!IPAddress.TryParse(configuredAllowedLocalAddress, out var configuredAddress))
            return false;

        configuredAddress = NormalizeRequiredIpAddress(configuredAddress);
        if (configuredAddress.Equals(IPAddress.Any) || configuredAddress.Equals(IPAddress.IPv6Any))
            return true;

        if (IPAddress.IsLoopback(configuredAddress))
            return IPAddress.IsLoopback(localAddress);

        return configuredAddress.Equals(localAddress);
    }

    private static IPAddress NormalizeRequiredIpAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IPAddress? NormalizeIpAddress(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
}
