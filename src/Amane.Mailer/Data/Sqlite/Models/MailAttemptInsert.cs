namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class MailAttemptInsert
{
    public required Guid RequestId { get; init; }

    public required int AttemptNumber { get; init; }

    public required string Provider { get; init; }

    public required MailRequestState Status { get; init; }

    public string? ProviderMessageId { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public required bool Retryable { get; init; }

    public required Guid LockToken { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}
