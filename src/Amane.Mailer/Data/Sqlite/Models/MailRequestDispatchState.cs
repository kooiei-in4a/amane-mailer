namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class MailRequestDispatchState
{
    public Guid Id { get; init; }

    public MailRequestState Status { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? LastErrorMessage { get; init; }

    public Guid? LockToken { get; init; }
}
