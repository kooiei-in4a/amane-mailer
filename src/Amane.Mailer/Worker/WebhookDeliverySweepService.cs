using Amane.Mailer.Configuration;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Webhooks.Models;

namespace Amane.Mailer.Worker;

public sealed class WebhookDeliverySweepService(
    DeliveryEventRepository repository,
    DeliveryEventEnqueuer deliveryEventEnqueuer,
    MailerWebhookOptions webhookOptions,
    IWebhookDeliveryQueue queue,
    TimeProvider timeProvider,
    ILogger<WebhookDeliverySweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await deliveryEventEnqueuer.ReconcileMissingTerminalEventsAsync(
                    webhookOptions.ReconcileBatchSize,
                    stoppingToken);

                var now = timeProvider.GetUtcNow();
                await DeadLetterExpiredDeliveringAtMaxAttemptsAsync(now, stoppingToken);

                if (await repository.HasPendingWorkAsync(now, stoppingToken)
                    && !queue.TrySignalWorkAvailable())
                {
                    logger.LogDebug("Webhook delivery queue is full during sweep; worker will catch up.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook delivery sweep failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Drains expired Delivering events at max_attempts into DeadLettered.
    /// Batch loop mirrors <see cref="ExpiredProcessingReaper"/>; unlike mail, there is no
    /// post-dead-letter enqueue (webhook DeadLettered is terminal).
    /// </summary>
    internal async Task DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
                now,
                webhookOptions.ReconcileBatchSize,
                cancellationToken);

            foreach (var deliveryEvent in deadLettered)
            {
                LogExpiredDeliveringDeadLetter(deliveryEvent);
            }

            if (deadLettered.Count < webhookOptions.ReconcileBatchSize)
            {
                return;
            }
        }
    }

    private void LogExpiredDeliveringDeadLetter(ExpiredDeliveringDeadLetteredEvent deliveryEvent)
    {
        logger.LogError(
            "Webhook delivery event {EventId} was dead-lettered after its delivering lease expired at attempt {AttemptCount}. TenantId={TenantId}; MailRequestId={MailRequestId}; ErrorCode={ErrorCode}",
            deliveryEvent.Id,
            deliveryEvent.AttemptCount,
            deliveryEvent.TenantId,
            deliveryEvent.MailRequestId,
            deliveryEvent.ErrorCode);
    }
}
