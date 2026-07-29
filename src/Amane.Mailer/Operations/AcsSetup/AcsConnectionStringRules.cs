using System.Text.RegularExpressions;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Shared ACS connection-string shape check used by typed ACS operations and TTY adapters.
/// </summary>
public static partial class AcsConnectionStringRules
{
    private const int RegexMatchTimeoutMilliseconds = 250;

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();

    public static bool LooksLikeAcsConnectionString(string? value) =>
        !string.IsNullOrEmpty(value) && AcsConnectionStringRegex().IsMatch(value);
}
