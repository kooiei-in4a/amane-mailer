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

        // Mask secrets BEFORE collapsing whitespace so:
        // (a) quoted values with embedded spaces are handled correctly, and
        // (b) tokens split across a line break are fully masked.
        var text = raw;

        // Connection-string / credential assignments:
        //   endpoint=..., accesskey=..., token=..., api-key: ..., password="...", etc.
        text = CredentialAssignmentRegex().Replace(text, m => $"{m.Groups["key"].Value}={Mask}");

        // URL query strings can carry SAS signatures and tokens.
        text = UrlQueryRegex().Replace(text, m => $"{m.Groups["url"].Value}?{Mask}");

        // Bearer / authorization tokens.
        text = BearerRegex().Replace(text, $"Bearer {Mask}");

        // Recipient / sender email addresses — broad pattern to cover IDN/EAI.
        text = EmailRegex().Replace(text, Mask);

        // Collapse newlines and control whitespace to a single space.
        text = WhitespaceRegex().Replace(text, " ").Trim();

        // Masks always leave a token (e.g. "***"), so non-blank input cannot
        // collapse to an empty string here; blank input was handled above.
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength].TrimEnd() + "...";
        }

        return text;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    // Matches key=value or key: value, with optional quoting.
    // The unquoted value continuation `(?:[\r\n]+[^;,""'\s]+)*` catches tokens
    // split across a provider line break (e.g. long base64 access keys).
    [GeneratedRegex(
        @"\b(?<key>endpoint|accesskey|accountkey|sharedaccesskey|secret|secretkey|token|sastoken|password|pwd|sig|signature|apikey|api[-_]key|key)\s*[=:]\s*(?:""[^""]*""|'[^']*'|[^;,""'\s]+(?:[\r\n]+[^;,""'\s]+)*)",
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

    // Broad pattern: covers ASCII emails and IDN/EAI addresses (e.g. ユーザー@例え.テスト).
    // Requires at least one dot in the domain to reduce false positives.
    [GeneratedRegex(
        @"[^\s@<>""',;]+@[^\s@<>""',;.]+(?:\.[^\s@<>""',;]+)+",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
