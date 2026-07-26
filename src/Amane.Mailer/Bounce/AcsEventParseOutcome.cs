namespace Amane.Mailer.Bounce;

public enum AcsEventParseOutcome
{
    /// <summary>Non-success delivery report suitable for bounce ingestion.</summary>
    DeliveryReport,

    /// <summary>Wrong event type, success Delivered report, or empty batch — discard without retry.</summary>
    Ignored,

    /// <summary>JSON / required-field failure — finalize discarded / dead-letter; do not infinite-retry.</summary>
    Unparseable,
}

public sealed class AcsEventParseResult
{
    public required AcsEventParseOutcome Outcome { get; init; }

    public ProviderDeliveryReport? Report { get; init; }
}