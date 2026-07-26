using System.Globalization;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Strict integer configuration reader for operational settings.
/// Missing keys keep defaults; empty / whitespace / malformed / out-of-range values fail fast.
/// </summary>
internal static class ConfigurationIntReader
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public static int Read(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minInclusive,
        int maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureRangeOrdered(minInclusive, maxInclusive);

        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return ParseInRange(raw, key, minInclusive, maxInclusive);
    }

    public static int Read(
        IConfiguration configuration,
        int defaultValue,
        int minInclusive,
        int maxInclusive,
        string primaryKey,
        string fallbackKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureRangeOrdered(minInclusive, maxInclusive);

        if (ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                primaryKey,
                fallbackKey))
        {
            return ParseInRange(raw, key, minInclusive, maxInclusive);
        }

        return defaultValue;
    }

    public static int? ReadOptional(
        IConfiguration configuration,
        string key,
        int minInclusive,
        int maxInclusive)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureRangeOrdered(minInclusive, maxInclusive);

        var raw = configuration[key];
        if (raw is null)
        {
            return null;
        }

        return ParseInRange(raw, key, minInclusive, maxInclusive);
    }

    public static int? ReadOptional(
        IConfiguration configuration,
        int minInclusive,
        int maxInclusive,
        string primaryKey,
        string fallbackKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureRangeOrdered(minInclusive, maxInclusive);

        if (ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                primaryKey,
                fallbackKey))
        {
            return ParseInRange(raw, key, minInclusive, maxInclusive);
        }

        return null;
    }

    public static int ReadPort(
        IConfiguration configuration,
        string key,
        int defaultValue) =>
        Read(configuration, key, defaultValue, MinPort, MaxPort);

    public static int ReadPort(
        IConfiguration configuration,
        int defaultValue,
        string primaryKey,
        string fallbackKey) =>
        Read(configuration, defaultValue, MinPort, MaxPort, primaryKey, fallbackKey);

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

    private static void EnsureRangeOrdered(int minInclusive, int maxInclusive)
    {
        if (minInclusive > maxInclusive)
        {
            throw new ArgumentException(
                $"minInclusive ({minInclusive}) must be <= maxInclusive ({maxInclusive}).",
                nameof(minInclusive));
        }
    }

    private static string FormatRangeMessage(string key, int minInclusive, int maxInclusive) =>
        $"{key} must be an integer between {minInclusive} and {maxInclusive} (inclusive).";
}
