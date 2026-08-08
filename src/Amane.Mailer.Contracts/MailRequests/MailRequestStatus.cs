namespace Amane.Mailer.Contracts.MailRequests;

public static class MailRequestStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Terminal status for attachment requests whose provider acceptance could not be proven or
    /// disproven after a submission marker committed (ADR 0022 D-08). Automatic and manual retry
    /// are both prohibited; never treated as a synonym for <see cref="Failed"/>.
    /// </summary>
    public const string DeliveryUnknown = "delivery_unknown";
}
