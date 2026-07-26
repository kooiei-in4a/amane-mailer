using Amane.Mailer.Webhooks.Models;

namespace Amane.Mailer.Webhooks;

/// <summary>
/// Claim/finalize surface used by <see cref="WebhookDeliveryWorker"/>.
/// Allows test doubles to inject claim/finalize faults without unsealing the repository.
/// </summary>
internal interface IWebhookDeliveryWorkStore
{
    Task<DeliveryEventRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> FinalizeAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        DeliveryEventFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorCode,
        CancellationToken cancellationToken = default);
}
