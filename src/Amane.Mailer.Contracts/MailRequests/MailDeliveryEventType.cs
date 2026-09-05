namespace Amane.Mailer.Contracts.MailRequests;

internal static class MailDeliveryEventType
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled";

    /// <summary>See <see cref="MailRequestStatus.DeliveryUnknown"/> (ADR 0022 D-08).</summary>
    public const string DeliveryUnknown = "delivery_unknown";
}
