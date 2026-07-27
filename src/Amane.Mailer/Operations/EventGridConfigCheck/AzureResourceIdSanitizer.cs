using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Operations.EventGridConfigCheck;

/// <summary>
/// Minimal sanitization for Azure resource IDs and CLI error text.
/// Never returns access tokens, connection strings, or raw CLI dumps.
/// </summary>
public static partial class AzureResourceIdSanitizer
{
    private static readonly Regex SubscriptionGuid = SubscriptionGuidPattern();
    private static readonly Regex BearerLike = BearerLikePattern();
    private static readonly Regex ConnectionStringLike = ConnectionStringLikePattern();
    private static readonly Regex EmailLike = EmailLikePattern();

    public static string SanitizeResourceId(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return "(none)";
        }

        var trimmed = resourceId.Trim();
        trimmed = SubscriptionGuid.Replace(trimmed, "/subscriptions/***/");
        return Truncate(trimmed, 180);
    }


    public static string SanitizeSubscription(string? subscription)
    {
        if (string.IsNullOrWhiteSpace(subscription))
        {
            return "(none)";
        }

        var trimmed = subscription.Trim();
        if (Guid.TryParse(trimmed, out _))
        {
            return "***";
        }

        return Truncate(trimmed, 64);
    }

    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "(none)";
        }

        return Truncate(name.Trim(), 80);
    }

    public static string ClassifyCliFailure(AzureCliRunResult result)
    {
        if (!result.Started)
        {
            return "Azure CLI is not available on PATH.";
        }

        if (result.TimedOut)
        {
            return "Azure CLI query timed out.";
        }

        var combined = $"{result.StandardOutput}\n{result.StandardError}";
        if (LooksLikeAuthFailure(combined))
        {
            return "Azure CLI authentication is missing or expired (run az login).";
        }

        if (LooksLikeNotFound(combined))
        {
            return "Target Azure resource was not found or is not visible with current permissions.";
        }

        if (LooksLikeForbidden(combined))
        {
            return "Azure CLI lacks permission to read the target resource.";
        }

        return "Azure CLI read query failed (details omitted).";
    }

    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sanitized = BearerLike.Replace(text, "Bearer ***");
        sanitized = ConnectionStringLike.Replace(sanitized, "$1=***");
        sanitized = EmailLike.Replace(sanitized, "***@***");
        sanitized = SubscriptionGuid.Replace(sanitized, "/subscriptions/***/");
        return Truncate(sanitized, 120);
    }

    private static bool LooksLikeAuthFailure(string text) =>
        ContainsAny(text,
            "az login",
            "please run 'az login'",
            "aadsts",
            "authentication failed",
            "not logged in",
            "token is expired",
            "refresh token");

    private static bool LooksLikeNotFound(string text) =>
        ContainsAny(text,
            "resourcenotfound",
            "resource not found",
            "could not be found",
            "notfound",
            "(404)",
            " status: 404");

    private static bool LooksLikeForbidden(string text) =>
        ContainsAny(text,
            "authorizationfailed",
            "authorization failed",
            "forbidden",
            "(403)",
            " status: 403",
            "does not have authorization");

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..max] + "…";
    }

    [GeneratedRegex(@"(/subscriptions/)[0-9a-fA-F-]{36}(/)", RegexOptions.CultureInvariant)]
    private static partial Regex SubscriptionGuidPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerLikePattern();

    [GeneratedRegex(@"(AccessKey|SharedAccessKey|AccountKey|Signature)=[^\s;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringLikePattern();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailLikePattern();
}
