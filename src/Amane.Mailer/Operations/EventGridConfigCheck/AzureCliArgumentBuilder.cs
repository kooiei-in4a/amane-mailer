namespace Amane.Mailer.Operations.EventGridConfigCheck;

/// <summary>
/// Builds allowlisted <c>az</c> argument vectors for read-only queries only.
/// Never accepts raw shell strings; mutations are structurally impossible.
/// </summary>
internal static class AzureCliArgumentBuilder
{
    private static readonly string[] AllowedPrefixes =
    [
        "version --output json",
        "account show --subscription ",
        "resource show --ids ",
        "resource show --resource-group ",
        "eventgrid event-subscription show --name ",
        "storage account show --name ",
        "storage queue exists --name ",
    ];

    public static string Build(AzureCliQuery query)
    {
        var args = query.Kind switch
        {
            AzureCliQueryKind.Version => "version --output json",
            AzureCliQueryKind.AccountShow => BuildAccountShow(query),
            AzureCliQueryKind.ResourceShow => BuildResourceShow(query),
            AzureCliQueryKind.EventSubscriptionShow => BuildEventSubscriptionShow(query),
            AzureCliQueryKind.StorageAccountShow => BuildStorageAccountShow(query),
            AzureCliQueryKind.StorageQueueExists => BuildStorageQueueExists(query),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, "Unsupported query kind."),
        };

        EnsureAllowlisted(args);
        return args;
    }

    private static string BuildAccountShow(AzureCliQuery query)
    {
        RequireSubscription(query);
        return $"account show --subscription {Quote(query.Subscription)} --output json";
    }

    private static string BuildResourceShow(AzureCliQuery query)
    {
        RequireSubscription(query);
        if (!string.IsNullOrWhiteSpace(query.ResourceId))
        {
            return $"resource show --ids {Quote(query.ResourceId)} --subscription {Quote(query.Subscription)} --output json";
        }

        Require(query.ResourceGroup, nameof(query.ResourceGroup));
        Require(query.ResourceName, nameof(query.ResourceName));
        Require(query.ResourceType, nameof(query.ResourceType));
        return
            $"resource show --resource-group {Quote(query.ResourceGroup!)} --name {Quote(query.ResourceName!)} " +
            $"--resource-type {Quote(query.ResourceType!)} --subscription {Quote(query.Subscription)} --output json";
    }

    private static string BuildEventSubscriptionShow(AzureCliQuery query)
    {
        RequireSubscription(query);
        Require(query.EventSubscriptionName, nameof(query.EventSubscriptionName));
        Require(query.SourceResourceId, nameof(query.SourceResourceId));
        return
            $"eventgrid event-subscription show --name {Quote(query.EventSubscriptionName!)} " +
            $"--source-resource-id {Quote(query.SourceResourceId!)} " +
            $"--subscription {Quote(query.Subscription)} --output json";
    }

    private static string BuildStorageAccountShow(AzureCliQuery query)
    {
        RequireSubscription(query);
        Require(query.ResourceGroup, nameof(query.ResourceGroup));
        Require(query.StorageAccountName, nameof(query.StorageAccountName));
        return
            $"storage account show --name {Quote(query.StorageAccountName!)} " +
            $"--resource-group {Quote(query.ResourceGroup!)} --subscription {Quote(query.Subscription)} --output json";
    }

    private static string BuildStorageQueueExists(AzureCliQuery query)
    {
        RequireSubscription(query);
        Require(query.StorageAccountName, nameof(query.StorageAccountName));
        Require(query.QueueName, nameof(query.QueueName));
        return
            $"storage queue exists --name {Quote(query.QueueName!)} --account-name {Quote(query.StorageAccountName!)} " +
            $"--auth-mode login --subscription {Quote(query.Subscription)} --output json";
    }

    private static void EnsureAllowlisted(string args)
    {
        foreach (var prefix in AllowedPrefixes)
        {
            if (args.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException("Refusing to build a non-allowlisted Azure CLI invocation.");
    }

    private static void RequireSubscription(AzureCliQuery query) =>
        Require(query.Subscription, nameof(query.Subscription));

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required for this query.", name);
        }
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Argument contains unsupported characters.");
        }

        // Reject shell metacharacters that could break out of a single argv token.
        if (value.IndexOfAny(['&', '|', ';', '>', '<', '`', '$', '(', ')', '%', '^', '!']) >= 0)
        {
            throw new ArgumentException("Argument contains unsupported shell metacharacters.");
        }

        if (value.IndexOfAny([' ', '\t']) >= 0)
        {
            return $"\"{value}\"";
        }

        return value;
    }
}
