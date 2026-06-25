namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class MailRequestRow
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string SourceService { get; init; }

    public Guid MailRequestId { get; init; }

    public required string Subject { get; init; }

    public string? HtmlBody { get; init; }

    public string? TextBody { get; init; }

    public string? ReplyTo { get; init; }

    public required string RecipientEmail { get; init; }

    public string? RecipientDisplayName { get; init; }

    public MailRequestState Status { get; init; }

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public Guid LockToken { get; init; }

    public DateTimeOffset LockExpiresAt { get; init; }
}
