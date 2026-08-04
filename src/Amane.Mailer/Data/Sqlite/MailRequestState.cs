namespace Amane.Mailer.Data.Sqlite;

public enum MailRequestState : byte
{
    Queued = 0,
    Processing = 1,
    Delivered = 2,
    Failed = 3,
    DeadLettered = 4,
    Cancelled = 5,

    /// <summary>
    /// Terminal state for attachment requests whose provider submission evidence committed
    /// (<c>Started</c>) but whose provider acceptance could not be proven or disproven
    /// (ADR 0022 D-08). Distinct from <see cref="Failed"/>: automatic and manual retry are both
    /// prohibited, and provider invocation is never repeated after this transition.
    /// </summary>
    DeliveryUnknown = 6,
}
