namespace Amane.Mailer.Data.Sqlite;

public enum MailRecipientDeliveryState : byte
{
    NotSent = 0,
    Pending = 1,
    Delivered = 2,
    Bounced = 3,
    Suppressed = 4,
    Failed = 5,
    Unknown = 6,
}
