namespace Amane.Mailer.Data.Sqlite;

public enum ManualMailRequestMutationStatus
{
    Succeeded,
    NotFound,
    InvalidState,
    LockHeld,
}

public sealed record ManualMailRequestMutationResult(ManualMailRequestMutationStatus Status);
