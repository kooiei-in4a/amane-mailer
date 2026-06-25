namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminDeadLetterListRow(
    Guid Id,
    Guid TenantId,
    string SourceService,
    Guid MailRequestId,
    string RecipientEmail,
    string Subject,
    string? LastErrorMessage,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset CompletedAt);
