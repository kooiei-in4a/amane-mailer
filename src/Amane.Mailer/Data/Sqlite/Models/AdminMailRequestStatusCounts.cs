namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminMailRequestStatusCounts(
    int Queued,
    int Processing,
    int Delivered,
    int Failed,
    int DeadLettered,
    int Cancelled,
    int DeliveryUnknown)
{
    public static AdminMailRequestStatusCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public int Total =>
        Queued + Processing + Delivered + Failed + DeadLettered + Cancelled + DeliveryUnknown;

    public int For(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => Queued,
            MailRequestState.Processing => Processing,
            MailRequestState.Delivered => Delivered,
            MailRequestState.Failed => Failed,
            MailRequestState.DeadLettered => DeadLettered,
            MailRequestState.Cancelled => Cancelled,
            MailRequestState.DeliveryUnknown => DeliveryUnknown,
            _ => 0,
        };
}
