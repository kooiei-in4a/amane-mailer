using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Redacts secret / PII / path canaries from internal Docker output before any classification helper
/// consumes it. Public SetupDockerResult never carries raw streams.
/// </summary>
public static class DockerOutputSanitizer
{
    private static readonly Regex EmailLike = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConnectionStringLike = new(
        @"(?i)(endpoint|accesskey|accountkey|sharedaccesssignature|password|secret|token)\s*=\s*[^;\s]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnixPathLike = new(
        @"(/[^\s:""']+)+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WindowsPathLike = new(
        @"[A-Za-z]:\\[^\s""']+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string SanitizeForInternalUse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var text = raw;
        text = ConnectionStringLike.Replace(text, "$1=[redacted]");
        text = EmailLike.Replace(text, "[redacted-email]");
        text = WindowsPathLike.Replace(text, "[redacted-path]");
        text = UnixPathLike.Replace(text, "[redacted-path]");
        return text;
    }

    public static bool ContainsCanary(string? raw, params string[] canaries)
    {
        if (string.IsNullOrEmpty(raw) || canaries.Length == 0)
        {
            return false;
        }

        foreach (var canary in canaries)
        {
            if (!string.IsNullOrEmpty(canary)
                && raw.Contains(canary, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void ZeroBuffer(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
    }
}
