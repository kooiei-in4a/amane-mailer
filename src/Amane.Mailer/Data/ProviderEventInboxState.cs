namespace Amane.Mailer.Data;

/// <summary>
/// Inbox row lifecycle for <c>provider_event_inbox</c> (ADR 0020 D-01).
/// Mirrors delivery_events' four-value lease idiom with inbox-oriented names:
/// Processed replaces Delivered.
/// </summary>
public enum ProviderEventInboxState
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    DeadLettered = 3,
}

/// <summary>
/// Terminal disposition recorded when an inbox row reaches Processed or DeadLettered.
/// Discarded covers unparseable events finalized without retry; DeadLettered covers
/// max_attempts exhaustion (including expired lease at max attempts — #388).
/// </summary>
public static class ProviderEventInboxDisposition
{
    public const string Processed = "processed";
    public const string Discarded = "discarded";
    public const string DeadLettered = "dead_lettered";
}

public enum ProviderEventInboxFinalizeOutcome
{
    Processed,
    Discarded,
    RetryScheduled,
    DeadLettered,
}
