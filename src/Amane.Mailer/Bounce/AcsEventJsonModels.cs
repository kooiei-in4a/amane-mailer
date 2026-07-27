namespace Amane.Mailer.Bounce;

/// <summary>
/// Source-generated JSON DTOs for ACS Event Grid delivery reports.
/// Intentionally independent of the Azure Event Grid SDK (Native AOT / trim).
/// </summary>
public sealed class AcsEventGridEventDto
{
    public string? Id { get; set; }

    public string? EventType { get; set; }

    public DateTimeOffset EventTime { get; set; }

    public AcsEmailDeliveryReportDataDto? Data { get; set; }
}

public sealed class AcsEmailDeliveryReportDataDto
{
    public string? MessageId { get; set; }

    public string? Status { get; set; }

    public string? Recipient { get; set; }

    public AcsDeliveryStatusDetailsDto? DeliveryStatusDetails { get; set; }
}

public sealed class AcsDeliveryStatusDetailsDto
{
    public string? StatusMessage { get; set; }
}
