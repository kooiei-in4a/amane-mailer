namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class ProviderQueueDeadLetterInsert
{
    public required Guid Id { get; init; }

    public required string Provider { get; init; }

    public required string QueueMessageId { get; init; }

    public required string FailureStage { get; init; }

    public required string LastErrorCode { get; init; }

    public required long DequeueCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
