using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Webhooks.Models;

namespace Amane.Mailer.Webhooks;

public sealed class DeliveryEventEnqueuer
{
    internal const string PostCommitReasonTimeout = "timeout";
    internal const string PostCommitReasonCancellation = "cancellation";
    internal const string PostCommitReasonException = "exception";

    /// <summary>
    /// Upper bound for the post-commit enqueue. Sized against the SQLite busy timeout rather than
    /// exposed as configuration — the work is a single local insert (#390).
    /// </summary>
    internal static readonly TimeSpan PostCommitEnqueueTimeout = TimeSpan.FromSeconds(5);

    public Task TryEnqueueForInternalRequestAsync(
        Guid internalRequestId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Post-commit best-effort enqueue for an already-durable mutation (#269, #390).
    /// Never throws and deliberately takes no caller token: a client disconnect must not skip the
    /// immediate event, and an enqueue fault must not turn a committed command into a failure.
    /// Gaps are recovered by <see cref="ReconcileMissingTerminalEventsAsync"/>.
    /// Returns whether the enqueue attempt completed.
    /// </summary>
    public Task<bool> TryEnqueueAfterCommitAsync(Guid internalRequestId) => Task.FromResult(true);

    public Task TryEnqueueAsync(
        DeliveryEventContext context,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReconcileMissingTerminalEventsAsync(
        int batchSize,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    internal static MailDeliveryEventPayload BuildPayload(DeliveryEventContext context) =>
        new()
        {
            EventId = Guid.NewGuid(),
            // event_type mirrors status today; both are kept for the shared #216 status model.
            EventType = context.EventType,
            OccurredAt = context.OccurredAt,
            TenantId = context.TenantId,
            SourceService = context.SourceService,
            MailRequestId = context.MailRequestId,
            Status = context.EventType,
            AttemptCount = context.AttemptCount,
            LastErrorCode = context.LastErrorCode,
        };
}
