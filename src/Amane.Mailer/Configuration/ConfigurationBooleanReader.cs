namespace Amane.Mailer.Configuration;

/// <summary>
/// Strict boolean configuration reader for operational settings.
/// Missing keys keep defaults / null; empty / whitespace / malformed values fail fast.
/// </summary>
internal static class ConfigurationBooleanReader
{
    public static bool Read(IConfiguration configuration, string key, bool defaultValue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return Parse(raw, key);
    }

    public static bool? ReadOptional(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var raw = configuration[key];
        if (raw is null)
        {
            return null;
        }

        return Parse(raw, key);
    }

    public static bool Read(
        IConfiguration configuration,
        bool defaultValue,
        string primaryKey,
        string fallbackKey)
    {
        if (ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                primaryKey,
                fallbackKey))
        {
            return Parse(raw, key);
        }

        return defaultValue;
    }

    public static bool? ReadOptional(
        IConfiguration configuration,
        string primaryKey,
        string fallbackKey)
    {
        if (ConfigurationKeyPresence.TryGetPresent(
                configuration,
                out var key,
                out var raw,
                primaryKey,
                fallbackKey))
        {
            return Parse(raw, key);
        }

        return null;
    }

    public static bool Parse(string raw, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw, out var parsed))
        {
            throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
        }

        return parsed;
    }
}
