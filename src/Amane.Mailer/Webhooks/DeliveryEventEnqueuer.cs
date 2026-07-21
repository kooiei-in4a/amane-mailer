using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
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

    internal static MailDeliveryEventPayload BuildPayload(DeliveryEventContext context) =>
        new()
        {
            EventId = Guid.NewGuid(),
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
