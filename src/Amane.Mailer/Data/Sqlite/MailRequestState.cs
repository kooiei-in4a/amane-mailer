namespace Amane.Mailer.Data.Sqlite;

public enum MailRequestState : byte
{
    Queued = 0,
    Processing = 1,
    Delivered = 2,
    Failed = 3,
    DeadLettered = 4,
}
