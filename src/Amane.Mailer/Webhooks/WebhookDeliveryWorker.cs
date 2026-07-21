using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookDeliveryWorker(
    DeliveryEventRepository repository,
    WebhookDeliveryClient deliveryClient,
    MailerTenantRegistry tenantRegistry,
    MailerWebhookOptions webhookOptions,
    IWebhookDeliveryQueue queue,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await queue.Reader.WaitToReadAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = timeProvider.GetUtcNow();
                var row = await repository.TryClaimOneAsync(now, webhookOptions.LeaseDuration, stoppingToken);
                if (row is null)
                {
                    break;
                }

                await DeliverClaimedEventAsync(row, stoppingToken);
            }
        }
    }

    private async Task DeliverClaimedEventAsync(
        Models.DeliveryEventRow row,
        CancellationToken stoppingToken)
    {
        var tenant = tenantRegistry.Find(row.TenantId);
        var secret = tenantRegistry.GetWebhookSecret(row.TenantId);
        if (tenant?.Webhook is null || secret is null)
        {
            await FinalizeTerminalFailureAsync(
                row,
                "WEBHOOK_NOT_CONFIGURED",
                retryable: false,
                stoppingToken);
            return;
        }

        var payload = JsonSerializer.Deserialize(
            row.PayloadJson,
            MailerContractsJsonContext.Default.MailDeliveryEventPayload);
        if (payload is null)
        {
            await FinalizeTerminalFailureAsync(
                row,
                "WEBHOOK_PAYLOAD_INVALID",
                retryable: false,
                stoppingToken);
            return;
        }

        using var deliveryTimeout = new CancellationTokenSource(webhookOptions.DeliveryTimeout);
        WebhookDeliveryResult result;
        try
        {
            result = await deliveryClient.DeliverAsync(
                tenant,
                secret,
                payload,
                row.PayloadJson,
                deliveryTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = WebhookDeliveryResult.Failure("WEBHOOK_TIMEOUT", retryable: true);
        }

        var completedAt = timeProvider.GetUtcNow();
        DeliveryEventFinalizeOutcome outcome;
        DateTimeOffset? nextAttemptAt = null;
        if (result.Succeeded)
        {
            outcome = DeliveryEventFinalizeOutcome.Delivered;
        }
        else if (result.Retryable && row.AttemptCount < row.MaxAttempts)
        {
            outcome = DeliveryEventFinalizeOutcome.RetryScheduled;
            nextAttemptAt = webhookOptions.ComputeNextAttemptAt(row.AttemptCount, completedAt);
        }
        else
        {
            outcome = DeliveryEventFinalizeOutcome.DeadLettered;
        }

        using var finalizeTimeout = new CancellationTokenSource(webhookOptions.FinalizeTimeout);
        var finalized = await repository.FinalizeAsync(
            row.Id,
            row.LockToken,
            completedAt,
            outcome,
            nextAttemptAt,
            result.ErrorCode,
            finalizeTimeout.Token);

        if (!finalized)
        {
            logger.LogWarning(
                "Skipped webhook finalize for event {EventId} because the lock token expired or was superseded.",
                row.Id);
            return;
        }

        if (outcome == DeliveryEventFinalizeOutcome.DeadLettered)
        {
            logger.LogError(
                "Webhook event {EventId} for mail request {MailRequestId} was dead-lettered after attempt {AttemptNumber}. ErrorCode={ErrorCode}",
                row.Id,
                row.MailRequestId,
                row.AttemptCount,
                result.ErrorCode);
        }
        else if (outcome == DeliveryEventFinalizeOutcome.RetryScheduled)
        {
            queue.TrySignalWorkAvailable();
        }
    }

    private async Task FinalizeTerminalFailureAsync(
        Models.DeliveryEventRow row,
        string errorCode,
        bool retryable,
        CancellationToken stoppingToken)
    {
        var completedAt = timeProvider.GetUtcNow();
        var outcome = retryable && row.AttemptCount < row.MaxAttempts
            ? DeliveryEventFinalizeOutcome.RetryScheduled
            : DeliveryEventFinalizeOutcome.DeadLettered;
        DateTimeOffset? nextAttemptAt = outcome == DeliveryEventFinalizeOutcome.RetryScheduled
            ? webhookOptions.ComputeNextAttemptAt(row.AttemptCount, completedAt)
            : null;

        using var finalizeTimeout = new CancellationTokenSource(webhookOptions.FinalizeTimeout);
        await repository.FinalizeAsync(
            row.Id,
            row.LockToken,
            completedAt,
            outcome,
            nextAttemptAt,
            errorCode,
            finalizeTimeout.Token);
    }
}
