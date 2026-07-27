namespace Amane.Mailer.Configuration;

/// <summary>
/// Detects placeholder-like configuration values without echoing them.
/// Mirrors <c>scripts/validate-tenant-config.mjs</c> placeholder rules.
/// </summary>
public static class ConfigurationPlaceholderDetector
{
    public static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return true;
        }

        if (normalized.StartsWith('<') && normalized.EndsWith('>'))
        {
            return true;
        }

        return normalized.StartsWith("replace-with", StringComparison.Ordinal)
            || normalized.Contains("replace_with", StringComparison.Ordinal)
            || normalized.Contains("placeholder", StringComparison.Ordinal)
            || normalized.Contains("change-me", StringComparison.Ordinal)
            || normalized.Contains("changeme", StringComparison.Ordinal)
            || normalized.Contains("todo", StringComparison.Ordinal)
            || normalized.Contains("your-token", StringComparison.Ordinal)
            || normalized.Contains("your-secret", StringComparison.Ordinal)
            || normalized.Contains("dummy-token", StringComparison.Ordinal)
            || normalized.Contains("example-token", StringComparison.Ordinal)
            || normalized.Contains("secret-here", StringComparison.Ordinal);
    }
}
