using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Operations;
using Amane.Mailer.Worker;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookDeliveryWorker(
    DeliveryEventRepository repository,
    WebhookDeliveryClient deliveryClient,
    MailerTenantRegistry tenantRegistry,
    MailerWebhookOptions webhookOptions,
    IWebhookDeliveryQueue queue,
    MailerRuntimeMetrics runtimeMetrics,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    internal const string FinalizeSkipReasonDeliveryResult = "delivery_result";
    internal const string FinalizeSkipReasonWebhookNotConfigured = "webhook_not_configured";
    internal const string FinalizeSkipReasonPayloadInvalid = "payload_invalid";

    private readonly InflightTracker _inflightTracker = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stopping cancels WaitToReadAsync / claim loops so no new work is claimed.
            // In-flight HTTP delivery uses DeliveryTimeout (not linked to stoppingToken),
            // matching MailRequestWorker, and is drained in finally below.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await queue.Reader.WaitToReadAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    var now = timeProvider.GetUtcNow();
                    var row = await repository.TryClaimOneAsync(now, webhookOptions.LeaseDuration, stoppingToken);
                    if (row is null)
                    {
                        break;
                    }

                    using var inflight = _inflightTracker.Enter();
                    await DeliverClaimedEventAsync(row, stoppingToken);
                }
            }
        }
        finally
        {
            await _inflightTracker.WaitForZeroAsync(webhookOptions.ShutdownDrainTimeout, CancellationToken.None);

            if (_inflightTracker.InflightCount > 0)
            {
                logger.LogWarning(
                    "Shutdown grace period elapsed with {InflightCount} in-flight webhook deliveries still active.",
                    _inflightTracker.InflightCount);
            }
        }
    }

    internal async Task DeliverClaimedEventAsync(
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
                FinalizeSkipReasonWebhookNotConfigured,
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
                FinalizeSkipReasonPayloadInvalid,
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

        if (!ObserveFinalizeResult(row, outcome, FinalizeSkipReasonDeliveryResult, finalized))
        {
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
        string finalizeSkipReason,
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
        var finalized = await repository.FinalizeAsync(
            row.Id,
            row.LockToken,
            completedAt,
            outcome,
            nextAttemptAt,
            errorCode,
            finalizeTimeout.Token);

        _ = ObserveFinalizeResult(row, outcome, finalizeSkipReason, finalized);
    }

    private bool ObserveFinalizeResult(
        Models.DeliveryEventRow row,
        DeliveryEventFinalizeOutcome outcome,
        string finalizeSkipReason,
        bool finalized)
    {
        if (finalized)
        {
            return true;
        }

        runtimeMetrics.RecordWebhookFinalizeSkipped();
        logger.LogWarning(
            "Skipped webhook finalize for event {EventId} because the lock token expired or was superseded. TenantId={TenantId}; MailRequestId={MailRequestId}; AttemptNumber={AttemptNumber}; FinalizeOutcome={FinalizeOutcome}; FinalizeSkipReason={FinalizeSkipReason}",
            row.Id,
            row.TenantId,
            row.MailRequestId,
            row.AttemptCount,
            outcome.ToString(),
            finalizeSkipReason);
        return false;
    }
}
