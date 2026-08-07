namespace Amane.Mailer.Data.Sqlite;

public enum MailPlainSubmissionEvidenceState : byte
{
    Started = 0,
    DefinitelyNotSubmitted = 1,
    Accepted = 2,
    DefinitelyRejected = 3,
    Unknown = 4,
}
