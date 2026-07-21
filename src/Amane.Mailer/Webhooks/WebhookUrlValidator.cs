using System.Net;
using System.Net.Sockets;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookUrlValidator
{
    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
    };

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

        if (IsBlockedLiteralIp(uri.Host, out var literalReason))
        {
            return WebhookUrlValidationResult.Invalid(literalReason);
        }

        if (webhook.AllowedHostSuffixes is { Count: > 0 }
            && !IsHostAllowedBySuffix(uri.Host, webhook.AllowedHostSuffixes))
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_HOST_NOT_ALLOWED");
        }

        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            if (IsBlockedIpAddress(literalAddress))
            {
                return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_IP_BLOCKED");
            }

            return WebhookUrlValidationResult.Valid(uri);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_DNS_FAILED");
        }

        if (addresses.Length == 0)
        {
            return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_DNS_EMPTY");
        }

        foreach (var address in addresses)
        {
            if (IsBlockedIpAddress(address))
            {
                return WebhookUrlValidationResult.Invalid("WEBHOOK_URL_IP_BLOCKED");
            }
        }

        return WebhookUrlValidationResult.Valid(uri);
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

    private static bool IsBlockedLiteralIp(string host, out string reason)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            reason = string.Empty;
            return false;
        }

        if (IsBlockedIpAddress(address))
        {
            reason = "WEBHOOK_URL_IP_BLOCKED";
            return true;
        }

        reason = string.Empty;
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
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 168 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return bytes[0] == 0xFC || bytes[0] == 0xFD;
        }

        return true;
    }
}

public sealed record WebhookUrlValidationResult(bool IsValid, Uri? Uri, string? ErrorCode)
{
    public static WebhookUrlValidationResult Valid(Uri uri) => new(true, uri, null);

    public static WebhookUrlValidationResult Invalid(string errorCode) => new(false, null, errorCode);
}
