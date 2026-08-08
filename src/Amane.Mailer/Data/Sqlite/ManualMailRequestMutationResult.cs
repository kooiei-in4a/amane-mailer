using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

public enum ManualMailRequestMutationStatus
{
    Succeeded,
    NotFound,
    InvalidState,
    LockHeld,

    /// <summary>
    /// The request carries canonical attachment metadata (ADR 0022 D-08): manual retry is
    /// prohibited from every terminal state, and manual cancel is prohibited once submission
    /// evidence exists. Maps to HTTP 409 with the fixed reason code
    /// <c>ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED</c>.
    /// </summary>
    AttachmentManualRetryNotSupported,
}

public sealed record ManualMailRequestMutationResult(ManualMailRequestMutationStatus Status);

public sealed record ConsumerMailRequestMutationResult(
    ManualMailRequestMutationStatus Status,
    Guid? InternalRequestId = null,
    MailRequestStatusRow? StatusSnapshot = null);
