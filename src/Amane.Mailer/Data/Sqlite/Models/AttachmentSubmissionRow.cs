namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AttachmentSubmissionRow(
    Guid RequestId,
    AttachmentSubmissionState SubmissionState,
    string Provider,
    DateTimeOffset SubmissionStartedAt,
    Guid LockToken,
    string? ProviderMessageId,
    DateTimeOffset? CompletedAt);
