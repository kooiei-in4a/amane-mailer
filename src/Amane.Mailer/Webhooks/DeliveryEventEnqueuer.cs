using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Webhooks.Models;

namespace Amane.Mailer.Webhooks;

public sealed class DeliveryEventEnqueuer(
    MailerTenantRegistry tenantRegistry,
    DeliveryEventRepository repository,
    MailerWebhookOptions webhookOptions,
    IWebhookDeliveryQueue queue,
    TimeProvider timeProvider,
    ILogger<DeliveryEventEnqueuer> logger)
{
    internal const string PostCommitReasonTimeout = "timeout";
    internal const string PostCommitReasonCancellation = "cancellation";
    internal const string PostCommitReasonException = "exception";

    /// <summary>
    /// Upper bound for the post-commit enqueue. Sized against the SQLite busy timeout rather than
    /// exposed as configuration — the work is a single local insert (#390).
    /// </summary>
    internal static readonly TimeSpan PostCommitEnqueueTimeout = TimeSpan.FromSeconds(5);

    public async Task TryEnqueueForInternalRequestAsync(
        Guid internalRequestId,
        CancellationToken cancellationToken = default)
    {
        var context = await repository.FindContextByInternalRequestIdAsync(internalRequestId, cancellationToken);
        if (context is null)
        {
            return;
        }

        await TryEnqueueAsync(context, cancellationToken);
    }

    /// <summary>
    /// Post-commit best-effort enqueue for an already-durable mutation (#269, #390).
    /// Never throws and deliberately takes no caller token: a client disconnect must not skip the
    /// immediate event, and an enqueue fault must not turn a committed command into a failure.
    /// Gaps are recovered by <see cref="ReconcileMissingTerminalEventsAsync"/>.
    /// Returns whether the enqueue attempt completed.
    /// </summary>
    public async Task<bool> TryEnqueueAfterCommitAsync(Guid internalRequestId)
    {
        using var timeout = new CancellationTokenSource(PostCommitEnqueueTimeout);
        try
        {
            await TryEnqueueForInternalRequestAsync(internalRequestId, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            LogPostCommitEnqueueFailure(exception: null, PostCommitReasonTimeout, internalRequestId);
        }
        catch (OperationCanceledException ex)
        {
            LogPostCommitEnqueueFailure(ex, PostCommitReasonCancellation, internalRequestId);
        }
        catch (Exception ex)
        {
            LogPostCommitEnqueueFailure(ex, PostCommitReasonException, internalRequestId);
        }

        return false;
    }

    public async Task TryEnqueueAsync(
        DeliveryEventContext context,
        CancellationToken cancellationToken = default)
    {
        var tenant = tenantRegistry.Find(context.TenantId);
        if (tenant?.Webhook is null)
        {
            return;
        }

        var payload = BuildPayload(context);
        var inserted = await repository.TryInsertAsync(
            payload,
            webhookOptions.MaxAttempts,
            timeProvider.GetUtcNow(),
            cancellationToken);

        if (!inserted)
        {
            return;
        }

        if (!queue.TrySignalWorkAvailable())
        {
            logger.LogWarning(
                "Webhook delivery queue is full after enqueueing event {EventId} for mail request {MailRequestId}.",
                payload.EventId,
                payload.MailRequestId);
        }
    }

    public async Task ReconcileMissingTerminalEventsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var missingRequestIds = await repository.FindInternalRequestIdsMissingDeliveryEventsAsync(
            batchSize,
            cancellationToken);

        foreach (var requestId in missingRequestIds)
        {
            await TryEnqueueForInternalRequestAsync(requestId, cancellationToken);
        }
    }

    private void LogPostCommitEnqueueFailure(
        Exception? exception,
        string reason,
        Guid internalRequestId)
    {
        // Identifiers and a fixed reason only. The exception object reaches the logger only for
        // SQLite faults, whose text is DB-level: providers render exceptions via ToString(), so an
        // arbitrary exception could otherwise carry the webhook URL or payload text into logs.
        const string template =
            "Post-commit webhook enqueue failed for internal request {InternalRequestId}. "
            + "The mutation stays committed and reconciliation will recreate the event. "
            + "Reason={Reason}; ExceptionType={ExceptionType}";
        var exceptionType = exception?.GetType().FullName;

        if (exception is not null && SqliteDatabaseExceptionClassifier.IsDatabaseException(exception))
        {
            logger.LogWarning(exception, template, internalRequestId, reason, exceptionType);
            return;
        }

        logger.LogWarning(template, internalRequestId, reason, exceptionType);
    }

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
