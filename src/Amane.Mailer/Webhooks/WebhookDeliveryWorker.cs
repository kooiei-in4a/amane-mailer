using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
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

    internal const string PayloadInvalidErrorCode = "WEBHOOK_PAYLOAD_INVALID";
    internal const string NotConfiguredErrorCode = "WEBHOOK_NOT_CONFIGURED";

    internal const string FailureStageClaim = "claim";
    internal const string FailureStageDeserialize = "deserialize";
    internal const string FailureStageResolveConfig = "resolve_config";
    internal const string FailureStageDeliver = "deliver";
    internal const string FailureStageFinalize = "finalize";

    /// <summary>
    /// Bounded delay after an isolated claim/process failure to avoid busy-looping on
    /// immediately-reproducible faults. Not a public configuration knob (#389).
    /// </summary>
    internal static readonly TimeSpan IsolatedFailureBackoff = TimeSpan.FromSeconds(1);

    private readonly InflightTracker _inflightTracker = new();

    /// <summary>
    /// Optional claim/finalize store override for isolation tests.
    /// </summary>
    internal IWebhookDeliveryWorkStore? WorkStoreOverride { get; set; }

    /// <summary>
    /// Optional tenant webhook config lookup override for isolation tests.
    /// </summary>
    internal IWebhookTenantConfigLookup? TenantConfigLookupOverride { get; set; }

    private IWebhookDeliveryWorkStore WorkStore => WorkStoreOverride ?? repository;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stopping cancels WaitToReadAsync / claim loops so no new work is claimed.
            // In-flight HTTP delivery uses DeliveryTimeout (not linked to stoppingToken),
            // matching MailRequestWorker, and is drained in finally below.
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wake bound follows InitialDelaySeconds so scheduled retries are polled without
                // busy-spinning, and fixtures with 1s delays stay within their existing timeouts.
                // Prefer this over querying the earliest next_attempt_at each idle cycle (#402).
                bool hasWork;
                try
                {
                    using var wakeTimeout = new CancellationTokenSource(ComputeIdleWakeTimeout());
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        wakeTimeout.Token);
                    hasWork = await queue.Reader.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Idle wake timeout — poll for due RetryScheduled rows even without a signal.
                    hasWork = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasWork)
                {
                    break;
                }

                // Capacity-1 DropWrite channel retains an unread item after TrySignalWorkAvailable.
                // Drain like MailRequestWorker so WaitToReadAsync can block again (#402).
                while (queue.Reader.TryRead(out _))
                {
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var claimed = await TryProcessNextEventAsync(stoppingToken);
                        if (!claimed)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Claim-stage failures are logged inside TryProcessNextEventAsync before
                        // rethrow. Process-stage failures are logged and swallowed there, so this
                        // catch is primarily claim isolation + unexpected escapes.
                        if (ex is not IsolatedWebhookFailureException)
                        {
                            LogIsolatedFailure(ex, FailureStageClaim, row: null);
                        }

                        await DelayIsolatedFailureBackoffAsync(stoppingToken);
                        // Return to the idle wait (wake timeout / Sweep signal) instead of
                        // re-entering claim at 1 Hz for a persistent DB outage (#402 / #389 AC7).
                        break;
                    }
                }
            }
        }
        finally
        {
            await _inflightTracker.WaitForZeroAsync(
                webhookOptions.ShutdownDrainTimeout,
                timeProvider,
                CancellationToken.None);

            if (_inflightTracker.InflightCount > 0)
            {
                logger.LogWarning(
                    "Shutdown grace period elapsed with {InflightCount} in-flight webhook deliveries still active.",
                    _inflightTracker.InflightCount);
            }
        }
    }

    /// <summary>
    /// Upper bound for idle <see cref="ChannelReader{T}.WaitToReadAsync"/> when no signal arrives.
    /// Derived from existing retry configuration — not a new public knob (#402).
    /// </summary>
    internal TimeSpan ComputeIdleWakeTimeout() =>
        TimeSpan.FromSeconds(Math.Max(1, webhookOptions.InitialDelaySeconds));

    /// <summary>
    /// Claims at most one event and processes it. Returns <see langword="false"/> when the
    /// queue has no claimable work. Isolated process failures are logged and do not fault the
    /// worker; claim failures are logged and rethrown as <see cref="IsolatedWebhookFailureException"/>
    /// so the caller can apply bounded backoff.
    /// </summary>
    private async Task<bool> TryProcessNextEventAsync(CancellationToken stoppingToken)
    {
        Models.DeliveryEventRow? row;
        try
        {
            var now = timeProvider.GetUtcNow();
            row = await WorkStore.TryClaimOneAsync(now, webhookOptions.LeaseDuration, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIsolatedFailure(ex, FailureStageClaim, row: null);
            throw new IsolatedWebhookFailureException(ex);
        }

        if (row is null)
        {
            return false;
        }

        using var inflight = _inflightTracker.Enter();
        await ProcessClaimedEventIsolatedAsync(row, stoppingToken);
        return true;
    }

    private async Task ProcessClaimedEventIsolatedAsync(
        Models.DeliveryEventRow row,
        CancellationToken stoppingToken)
    {
        var stage = FailureStageResolveConfig;
        try
        {
            await DeliverClaimedEventAsync(row, stoppingToken, stageTracker: value => stage = value);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIsolatedFailure(ex, stage, row);
            await DelayIsolatedFailureBackoffAsync(stoppingToken);
        }
    }

    internal async Task DeliverClaimedEventAsync(
        Models.DeliveryEventRow row,
        CancellationToken stoppingToken) =>
        await DeliverClaimedEventAsync(row, stoppingToken, stageTracker: null);

    private async Task DeliverClaimedEventAsync(
        Models.DeliveryEventRow row,
        CancellationToken stoppingToken,
        Action<string>? stageTracker)
    {
        stageTracker?.Invoke(FailureStageResolveConfig);
        var tenant = ResolveTenant(row.TenantId);
        var secret = ResolveWebhookSecret(row.TenantId);
        if (tenant?.Webhook is null || secret is null)
        {
            await FinalizeTerminalFailureAsync(
                row,
                NotConfiguredErrorCode,
                retryable: false,
                FinalizeSkipReasonWebhookNotConfigured,
                stoppingToken,
                stageTracker);
            return;
        }

        stageTracker?.Invoke(FailureStageDeserialize);
        MailDeliveryEventPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                row.PayloadJson,
                MailerContractsJsonContext.Default.MailDeliveryEventPayload);
        }
        catch (JsonException)
        {
            // Invalid JSON must not escape ExecuteAsync. Do not log exception text — it may
            // contain payload fragments. Converge with the null-result terminal path.
            payload = null;
        }

        if (payload is null)
        {
            await FinalizeTerminalFailureAsync(
                row,
                PayloadInvalidErrorCode,
                retryable: false,
                FinalizeSkipReasonPayloadInvalid,
                stoppingToken,
                stageTracker);
            return;
        }

        stageTracker?.Invoke(FailureStageDeliver);
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
            // deliveryTimeout is intentionally not linked to stoppingToken (#245).
            // An OCE here means DeliveryTimeout elapsed, not host shutdown.
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

        stageTracker?.Invoke(FailureStageFinalize);
        using var finalizeTimeout = new CancellationTokenSource(webhookOptions.FinalizeTimeout);
        var finalized = await WorkStore.FinalizeAsync(
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
        CancellationToken stoppingToken,
        Action<string>? stageTracker = null)
    {
        var completedAt = timeProvider.GetUtcNow();
        var outcome = retryable && row.AttemptCount < row.MaxAttempts
            ? DeliveryEventFinalizeOutcome.RetryScheduled
            : DeliveryEventFinalizeOutcome.DeadLettered;
        DateTimeOffset? nextAttemptAt = outcome == DeliveryEventFinalizeOutcome.RetryScheduled
            ? webhookOptions.ComputeNextAttemptAt(row.AttemptCount, completedAt)
            : null;

        stageTracker?.Invoke(FailureStageFinalize);
        using var finalizeTimeout = new CancellationTokenSource(webhookOptions.FinalizeTimeout);
        var finalized = await WorkStore.FinalizeAsync(
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

    private MailerTenant? ResolveTenant(Guid tenantId) =>
        TenantConfigLookupOverride is { } lookup
            ? lookup.Find(tenantId)
            : tenantRegistry.Find(tenantId);

    private string? ResolveWebhookSecret(Guid tenantId) =>
        TenantConfigLookupOverride is { } lookup
            ? lookup.GetWebhookSecret(tenantId)
            : tenantRegistry.GetWebhookSecret(tenantId);

    private async Task DelayIsolatedFailureBackoffAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(IsolatedFailureBackoff, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private void LogIsolatedFailure(
        Exception exception,
        string stage,
        Models.DeliveryEventRow? row)
    {
        // Structured fields are PII-free by construction (identifiers + fixed stage names).
        // The exception object is handed to the logger only for SQLite faults, whose text is
        // DB-level. Logging providers render exceptions via ToString(), so passing an arbitrary
        // exception would leak the webhook URL, payload fragments, or provider text into logs
        // regardless of the message template (#389). Unclassified failures therefore record the
        // exception type name only — stage plus type is enough to route an investigation.
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        if (SqliteDatabaseExceptionClassifier.IsDatabaseException(exception))
        {
            SqliteDatabaseExceptionLogging.LogError(
                logger,
                exception,
                "Webhook delivery worker failed due to SQLite storage full (SQLITE_FULL). Stage={Stage}; ExceptionType={ExceptionType}; EventId={EventId}; TenantId={TenantId}; MailRequestId={MailRequestId}; AttemptNumber={AttemptNumber}",
                "Webhook delivery worker failed on a database operation. Stage={Stage}; ExceptionType={ExceptionType}; EventId={EventId}; TenantId={TenantId}; MailRequestId={MailRequestId}; AttemptNumber={AttemptNumber}",
                stage,
                exceptionType,
                row?.Id,
                row?.TenantId,
                row?.MailRequestId,
                row?.AttemptCount);
            return;
        }

        logger.LogError(
            "Webhook delivery worker failed while processing an event. Stage={Stage}; ExceptionType={ExceptionType}; EventId={EventId}; TenantId={TenantId}; MailRequestId={MailRequestId}; AttemptNumber={AttemptNumber}",
            stage,
            exceptionType,
            row?.Id,
            row?.TenantId,
            row?.MailRequestId,
            row?.AttemptCount);
    }

    /// <summary>
    /// Marker used so the outer drain loop does not double-log claim failures.
    /// </summary>
    private sealed class IsolatedWebhookFailureException(Exception inner) : Exception(null, inner);
}
