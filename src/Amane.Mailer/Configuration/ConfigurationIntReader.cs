using System.Globalization;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Strict integer configuration reader for operational settings.
/// Missing keys keep defaults; empty / whitespace / malformed / out-of-range values fail fast.
/// </summary>
internal static class ConfigurationIntReader
{
    public static int Read(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minInclusive,
        int maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (minInclusive > maxInclusive)
        {
            throw new ArgumentException(
                $"minInclusive ({minInclusive}) must be <= maxInclusive ({maxInclusive}).",
                nameof(minInclusive));
        }

        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return ParseInRange(raw, key, minInclusive, maxInclusive);
    }

    public static int? ReadOptional(
        IConfiguration configuration,
        string key,
        int minInclusive,
        int maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (minInclusive > maxInclusive)
        {
            throw new ArgumentException(
                $"minInclusive ({minInclusive}) must be <= maxInclusive ({maxInclusive}).",
                nameof(minInclusive));
        }

        var raw = configuration[key];
        if (raw is null)
        {
            return null;
        }

        return ParseInRange(raw, key, minInclusive, maxInclusive);
    }

    public static int ParseInRange(string raw, string key, int minInclusive, int maxInclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(FormatRangeMessage(key, minInclusive, maxInclusive));
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minInclusive
            || parsed > maxInclusive)
        {
            throw new InvalidOperationException(FormatRangeMessage(key, minInclusive, maxInclusive));
        }

        return parsed;
    }

    private static string FormatRangeMessage(string key, int minInclusive, int maxInclusive) =>
        $"{key} must be an integer between {minInclusive} and {maxInclusive} (inclusive).";
}
