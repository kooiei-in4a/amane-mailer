namespace Amane.Mailer.Data.Sqlite;

public enum MailPlainSubmissionEvidenceOrigin : byte
{
    Runtime = 0,
    LegacyBackfill = 1,
}
