namespace Amane.Mailer.Contracts.MailRequests;

public static class MailRequestStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string DeadLettered = "dead_lettered";
}
