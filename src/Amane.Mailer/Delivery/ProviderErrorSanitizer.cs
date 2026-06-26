using System.Text.RegularExpressions;

namespace Amane.Mailer.Delivery;

/// <summary>
/// Classifies and scrubs raw provider exception text before it is persisted to
/// the database, written to stdout logs, or shown in the Admin UI.
/// </summary>
/// <remarks>
/// ACS/Mailpit exceptions can embed connection strings, access keys, SAS tokens,
/// bearer credentials, URL query secrets, and recipient email addresses. The
/// stored <see cref="MailDeliveryResult.ErrorCode"/> stays intact so operators
/// can still classify the failure, while the message is reduced to a safe,
/// single-line, length-bounded summary. See issue #26.
/// </remarks>
public static partial class ProviderErrorSanitizer
{
    internal const int MaxLength = 256;
    private const string Mask = "***";
    private const string EmptyPlaceholder = "Provider returned an error with no message.";

    /// <summary>
    /// Returns a sanitized, single-line summary safe to store, log, and display.
    /// Idempotent: re-sanitizing an already-sanitized value yields the same value.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return EmptyPlaceholder;
        }

        // Collapse newlines and control whitespace so multi-line provider dumps
        // cannot smuggle secrets past a per-line reviewer or break log lines.
        var text = WhitespaceRegex().Replace(raw, " ").Trim();

        // Connection-string / credential assignments: endpoint=..., accesskey=..., token=..., password=...
        text = CredentialAssignmentRegex().Replace(text, m => $"{m.Groups["key"].Value}={Mask}");

        // URL query strings can carry SAS signatures and tokens.
        text = UrlQueryRegex().Replace(text, m => $"{m.Groups["url"].Value}?{Mask}");

        // Bearer / authorization tokens.
        text = BearerRegex().Replace(text, $"Bearer {Mask}");

        // Recipient / sender email addresses.
        text = EmailRegex().Replace(text, Mask);

        // Masks always leave a token (e.g. "***"), so non-blank input cannot
        // collapse to an empty string here; blank input was handled above.
        text = text.Trim();
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength].TrimEnd() + "...";
        }

        return text;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"\b(?<key>endpoint|accesskey|accountkey|sharedaccesskey|secret|secretkey|token|sastoken|password|pwd|sig|signature|apikey|api_key|key)\s*=\s*[^;,\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?<url>https?://[^\s?""'<>]+)\?[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex(
        @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
