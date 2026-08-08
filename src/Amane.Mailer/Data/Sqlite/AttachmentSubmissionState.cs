namespace Amane.Mailer.Data.Sqlite;

/// <summary>Provider submission evidence state for attachment requests (ADR 0022 D-08).</summary>
public enum AttachmentSubmissionState : byte
{
    Started = 0,
    Succeeded = 1,
    DefinitiveFailed = 2,
    Unknown = 3,
}
