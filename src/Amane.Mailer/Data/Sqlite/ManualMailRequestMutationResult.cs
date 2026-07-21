namespace Amane.Mailer.Data.Sqlite;

public enum ManualMailRequestMutationStatus
{
    Succeeded,
    NotFound,
    InvalidState,
    LockHeld,
}

public sealed record ManualMailRequestMutationResult(ManualMailRequestMutationStatus Status);

public sealed record ConsumerMailRequestMutationResult(
    ManualMailRequestMutationStatus Status,
    Guid? InternalRequestId = null);
