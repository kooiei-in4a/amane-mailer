namespace Amane.Mailer.Bounce;

/// <summary>
/// Normalized ACS EmailDeliveryReportReceived fields (ADR 0020). Transport-agnostic.
/// </summary>
public sealed class ProviderDeliveryReport
{
    public required string EventId { get; init; }

    public required string MessageId { get; init; }

    /// <summary>
    /// ACS <c>data.status</c> (not <c>deliveryStatus</c>). See ADR 0020 F-2.
    /// </summary>
    public required string Status { get; init; }

    public string? Recipient { get; init; }

    /// <summary>
    /// Raw <c>deliveryStatusDetails.statusMessage</c>. Sanitize before persistence (#26 / D-08).
    /// </summary>
    public string? StatusMessage { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}
