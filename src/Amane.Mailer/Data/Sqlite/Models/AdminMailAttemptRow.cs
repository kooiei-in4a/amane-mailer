namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminMailAttemptRow(
    int AttemptNumber,
    string Provider,
    int Status,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
