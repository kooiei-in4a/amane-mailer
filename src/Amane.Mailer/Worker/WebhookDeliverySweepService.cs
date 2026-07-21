using Amane.Mailer.Configuration;
using Amane.Mailer.Webhooks;

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
                    webhookOptions.BatchClaimSize,
                    stoppingToken);

                var now = timeProvider.GetUtcNow();
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
}
