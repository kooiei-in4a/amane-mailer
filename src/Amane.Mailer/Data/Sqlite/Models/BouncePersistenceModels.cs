using Amane.Mailer.Data;

namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class ProviderEventInboxInsert
{
    public required Guid Id { get; init; }

    public required string Provider { get; init; }

    public required string EventId { get; init; }

    public string? ProviderMessageId { get; init; }

    public string? DeliveryStatus { get; init; }

    public string? RecipientEmail { get; init; }

    public required int MaxAttempts { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class ProviderEventInboxRow
{
    public required Guid Id { get; init; }

    public required string Provider { get; init; }

    public required string EventId { get; init; }

    public string? ProviderMessageId { get; init; }

    public string? DeliveryStatus { get; init; }

    public string? RecipientEmail { get; init; }

    public ProviderEventInboxState Status { get; init; }

    public string? Disposition { get; init; }

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public Guid LockToken { get; init; }

    public DateTimeOffset LockExpiresAt { get; init; }
}

public sealed record ExpiredProcessingDeadLetteredInboxEvent(
    Guid Id,
    string Provider,
    string EventId,
    int AttemptCount,
    string ErrorCode);

public sealed class BounceEventInsert
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string SourceService { get; init; }

    public required Guid MailRequestId { get; init; }

    public required string Provider { get; init; }

    public required string ProviderEventId { get; init; }

    public required string ProviderMessageId { get; init; }

    public required string DeliveryStatus { get; init; }

    public string? StatusMessage { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class MailSuppressionInsert
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string RecipientEmail { get; init; }

    public required string Reason { get; init; }

    public Guid? SourceBounceEventId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
