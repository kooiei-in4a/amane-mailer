namespace Amane.Mailer.Operations.EventGridConfigCheck;

public enum EventGridConfigEnvironment
{
    Dev,
    Staging,
    Production,
}

public static class EventGridConfigEnvironmentParser
{
    public const string UsageHint = "dev, staging, production";

    public static bool TryParse(string? value, out EventGridConfigEnvironment environment)
    {
        environment = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "dev", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "development", StringComparison.OrdinalIgnoreCase))
        {
            environment = EventGridConfigEnvironment.Dev;
            return true;
        }

        if (string.Equals(value, "staging", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "stage", StringComparison.OrdinalIgnoreCase))
        {
            environment = EventGridConfigEnvironment.Staging;
            return true;
        }

        if (string.Equals(value, "production", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "prod", StringComparison.OrdinalIgnoreCase))
        {
            environment = EventGridConfigEnvironment.Production;
            return true;
        }

        return false;
    }

    public static string ToDisplay(EventGridConfigEnvironment environment) =>
        environment switch
        {
            EventGridConfigEnvironment.Dev => "dev",
            EventGridConfigEnvironment.Staging => "staging",
            EventGridConfigEnvironment.Production => "production",
            _ => environment.ToString().ToLowerInvariant(),
        };
}

public sealed class EventGridConfigCheckOptions
{
    public required string Subscription { get; init; }

    public required string ResourceGroup { get; init; }

    public string? AcsName { get; init; }

    public string? AcsResourceId { get; init; }

    public required string EventSubscriptionName { get; init; }

    public required string StorageAccountName { get; init; }

    public required string QueueName { get; init; }

    public required EventGridConfigEnvironment Environment { get; init; }

    public string ResolveAcsDisplayName() =>
        !string.IsNullOrWhiteSpace(AcsName)
            ? AcsName!
            : ExtractResourceName(AcsResourceId) ?? "(acs)";

    private static string? ExtractResourceName(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var trimmed = resourceId.Trim().TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
    }
}
