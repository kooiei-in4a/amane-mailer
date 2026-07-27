namespace Amane.Mailer.Operations.EventGridConfigCheck;

public sealed record AzureCliRunResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public enum AzureCliQueryKind
{
    Version,
    AccountShow,
    ResourceShow,
    EventSubscriptionShow,
    StorageAccountShow,
    StorageQueueExists,
}

/// <summary>
/// Structured, allowlisted Azure CLI read-only query. Never accepts raw shell strings from callers.
/// </summary>
public sealed record AzureCliQuery(
    AzureCliQueryKind Kind,
    string Subscription,
    string? ResourceGroup = null,
    string? ResourceName = null,
    string? ResourceId = null,
    string? ResourceType = null,
    string? EventSubscriptionName = null,
    string? SourceResourceId = null,
    string? StorageAccountName = null,
    string? QueueName = null);
