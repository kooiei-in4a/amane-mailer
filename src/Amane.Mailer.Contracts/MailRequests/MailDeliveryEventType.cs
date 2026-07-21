namespace Amane.Mailer.Contracts.MailRequests;

public static class MailDeliveryEventType
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled";
}
