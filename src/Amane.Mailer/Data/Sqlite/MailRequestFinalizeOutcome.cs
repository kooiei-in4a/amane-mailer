namespace Amane.Mailer.Data.Sqlite;

public enum MailRequestFinalizeOutcome
{
    Delivered,
    RetryScheduled,
    Failed,
    DeadLettered,
}
