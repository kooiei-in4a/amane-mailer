namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminBounceEventRow(
    Guid Id,
    string Provider,
    string ProviderEventId,
    string ProviderMessageId,
    string DeliveryStatus,
    string? StatusMessage,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);
