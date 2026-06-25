namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminMailRequestListRow(
    Guid Id,
    Guid TenantId,
    string SourceService,
    Guid MailRequestId,
    string RecipientEmail,
    string Subject,
    MailRequestState Status,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset UpdatedAt);
