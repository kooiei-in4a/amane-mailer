namespace Amane.Mailer.Webhooks.Models;

public sealed record DeliveryEventRow
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string SourceService { get; init; }

    public required Guid MailRequestId { get; init; }

    public required string EventType { get; init; }

    public required string PayloadJson { get; init; }

    public DeliveryEventState Status { get; init; }

    public int AttemptCount { get; init; }

    public int MaxAttempts { get; init; }

    public Guid LockToken { get; init; }

    public DateTimeOffset LockExpiresAt { get; init; }
}

/// <summary>
/// Result of reclaiming an expired Delivering webhook event that already reached max_attempts.
/// Used for PII-free logging only; DeadLettered is terminal (do not enqueue further work).
/// </summary>
public sealed record ExpiredDeliveringDeadLetteredEvent(
    Guid Id,
    Guid TenantId,
    Guid MailRequestId,
    int AttemptCount,
    string ErrorCode);

public sealed record DeliveryEventContext(
    Guid TenantId,
    string SourceService,
    Guid MailRequestId,
    string EventType,
    int AttemptCount,
    string? LastErrorCode,
    DateTimeOffset OccurredAt);

public sealed record AdminWebhookDeadLetterListRow(
    Guid EventId,
    Guid TenantId,
    string SourceService,
    Guid MailRequestId,
    string EventType,
    int AttemptCount,
    string? LastErrorCode,
    DateTimeOffset CompletedAt);

public sealed record AdminWebhookDeadLetterListPage(
    IReadOnlyList<AdminWebhookDeadLetterListRow> Rows,
    string? NextCursor);

public sealed record AdminWebhookDeadLetterListQuery
{
    public IReadOnlySet<Guid>? AllowedTenantIds { get; init; }

    public DateTimeOffset? CursorCompletedAt { get; init; }

    public Guid? CursorId { get; init; }

    public int PageSize { get; init; } = 25;
}

public sealed record AdminWebhookDeadLetterCursor(DateTimeOffset CompletedAt, Guid Id)
{
    public static string Encode(DateTimeOffset completedAt, Guid id) =>
        Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{completedAt.UtcDateTime:O}|{id:D}"));

    public static bool TryDecode(string? cursor, out AdminWebhookDeadLetterCursor decoded)
    {
        decoded = default!;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = text.IndexOf('|');
            if (separator <= 0 || separator >= text.Length - 1)
            {
                return false;
            }

            var completedAt = DateTimeOffset.Parse(
                text[..separator],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            if (!Guid.TryParse(text[(separator + 1)..], out var id))
            {
                return false;
            }

            decoded = new AdminWebhookDeadLetterCursor(completedAt, id);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
