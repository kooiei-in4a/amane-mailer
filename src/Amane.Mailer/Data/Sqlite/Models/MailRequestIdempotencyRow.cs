namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class MailRequestIdempotencyRow
{
    public Guid Id { get; init; }

    public required string PayloadHash { get; init; }

    public MailRequestState Status { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }
}
