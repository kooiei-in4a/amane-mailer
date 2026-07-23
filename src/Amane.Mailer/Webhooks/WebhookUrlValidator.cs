using System.Net;
using System.Net.Sockets;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookUrlValidator
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAddressesAsync;

    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
    };

    public WebhookUrlValidator()
        : this(static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken))
    {
    }

    internal WebhookUrlValidator(Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAddressesAsync)
    {
        _resolveHostAddressesAsync = resolveHostAddressesAsync;
    }

    public async Task<WebhookUrlValidationResult> ValidateAsync(
        MailerWebhookConfig webhook,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out var uri))
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_INVALID");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_NOT_HTTPS");
        }

        if (uri.UserInfo.Length > 0)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_USERINFO_FORBIDDEN");
        }

        if (BlockedHostNames.Contains(uri.Host))
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_HOST_BLOCKED");
        }

        if (webhook.AllowedHostSuffixes is { Count: > 0 }
            && !IsHostAllowedBySuffix(uri.Host, webhook.AllowedHostSuffixes))
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_HOST_NOT_ALLOWED");
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            return IsBlockedIpAddress(literalAddress)
                ? WebhookUrlValidationResult.Invalid("WEBHOOK_URL_IP_BLOCKED")
                : WebhookUrlValidationResult.Valid(uri);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_DNS_FAILED");
        }

        if (addresses.Length == 0)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_DNS_EMPTY");
        }

        var connectAddress = SelectConnectAddress(addresses);
        if (connectAddress is null)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_IP_BLOCKED");
        }

        return WebhookUrlValidationResult.Valid(uri, connectAddress, uri.Host);
    }

    internal static IPAddress? SelectConnectAddress(IReadOnlyList<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (!IsBlockedIpAddress(address))
            {
                return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            }
        }

        return null;
    }

    private static bool IsHostAllowedBySuffix(string host, IReadOnlyList<string> allowedSuffixes)
    {
        foreach (var suffix in allowedSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsBlockedIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedIpv4Address(address.GetAddressBytes());
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            if (address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();

            // Unique local addresses (fc00::/7).
            if (bytes[0] is 0xFC or 0xFD)
            {
                return true;
            }

            // Deprecated IPv4-compatible IPv6 (::/96, excluding :: and ::1 already handled above).
            // No legitimate webhook target uses this form — fail closed.
            if (IsIpv4CompatibleIpv6Prefix(bytes))
            {
                return true;
            }

            // NAT64 well-known prefix 64:ff9b::/96 — block when the embedded IPv4 is blocked.
            if (IsNat64WellKnownPrefix(bytes))
            {
                return IsBlockedIpv4Address(bytes.AsSpan(12, 4));
            }

            // 6to4 2002::/16 — block when the embedded IPv4 is blocked.
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsBlockedIpv4Address(bytes.AsSpan(2, 4));
            }

            return false;
        }

        return true;
    }

    private static bool IsBlockedIpv4Address(ReadOnlySpan<byte> bytes)
    {
        return bytes[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] is >= 64 and <= 127 => true,
            // IPv4 multicast/reserved ranges are not useful webhook targets over TCP.
            >= 224 and <= 239 => true,
            >= 240 => true,
            _ => false,
        };
    }

    private static bool IsIpv4CompatibleIpv6Prefix(ReadOnlySpan<byte> bytes)
    {
        // ::/96 => 0000:0000:0000:0000:0000:0000:<ipv4>
        if (bytes.Length != 16)
        {
            return false;
        }

        for (var i = 0; i < 12; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNat64WellKnownPrefix(ReadOnlySpan<byte> bytes)
    {
        // 64:ff9b::/96 => 0064:ff9b:0000:0000:0000:0000:<ipv4>
        return bytes.Length == 16
            && bytes[0] == 0x00
            && bytes[1] == 0x64
            && bytes[2] == 0xFF
            && bytes[3] == 0x9B
            && bytes[4] == 0x00
            && bytes[5] == 0x00
            && bytes[6] == 0x00
            && bytes[7] == 0x00
            && bytes[8] == 0x00
            && bytes[9] == 0x00
            && bytes[10] == 0x00
            && bytes[11] == 0x00;
    }
}

public sealed record WebhookUrlValidationResult(
    bool IsValid,
    Uri? Uri,
    IPAddress? ConnectAddress,
    string? OriginalHost,
    string? ErrorCode)
{
    public static WebhookUrlValidationResult Valid(
        Uri uri,
        IPAddress? connectAddress = null,
        string? originalHost = null) =>
        new(true, uri, connectAddress, originalHost, null);

    public static WebhookUrlValidationResult Invalid(string errorCode) =>
        new(false, null, null, null, errorCode);
}
