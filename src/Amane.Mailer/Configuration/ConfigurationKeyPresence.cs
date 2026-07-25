namespace Amane.Mailer.Configuration;

/// <summary>
/// Resolves the first present configuration key in priority order.
/// A key is present when its value is non-null (including empty / whitespace).
/// </summary>
internal static class ConfigurationKeyPresence
{
    public static bool TryGetPresent(
        IConfiguration configuration,
        out string key,
        out string raw,
        params string[] keysInPriorityOrder)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(keysInPriorityOrder);
        if (keysInPriorityOrder.Length == 0)
        {
            throw new ArgumentException("At least one key is required.", nameof(keysInPriorityOrder));
        }

        foreach (var candidate in keysInPriorityOrder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

            var value = configuration[candidate];
            if (value is not null)
            {
                key = candidate;
                raw = value;
                return true;
            }
        }

        key = string.Empty;
        raw = string.Empty;
        return false;
    }
}
