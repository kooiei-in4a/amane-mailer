namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record MailRequestStatusRow(
    Guid MailRequestId,
    MailRequestState Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? DeliveredAt,
    string? LastErrorCode);
