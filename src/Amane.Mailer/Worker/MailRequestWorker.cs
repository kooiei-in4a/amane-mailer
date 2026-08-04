using Amane.Mailer.Attachments.Provider;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Worker;

public sealed class MailRequestWorker : BackgroundService
{
    private readonly IMailRequestQueue _queue;
    private readonly MailRequestRepository _repository;
    private readonly MailSuppressionRepository _suppressions;
    private readonly MailerTenantRegistry _tenants;
    private readonly MailerOptions _mailerOptions;
    private readonly MailerWorkerOptions _workerOptions;
    private readonly MailerHealthcheckOptions _healthcheckOptions;
    private readonly IMailDeliveryProvider _deliveryProvider;
    private readonly ExpiredProcessingReaper _expiredProcessingReaper;
    private readonly DeliveryEventEnqueuer _deliveryEventEnqueuer;
    private readonly WorkerServiceStatus _serviceStatus;
    private readonly MailerRuntimeMetrics _runtimeMetrics;
    private readonly AttachmentSpool _attachmentSpool;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MailRequestWorker> _logger;
    private readonly InflightTracker _inflightTracker = new();
    private readonly SemaphoreSlim _sendConcurrency;

    public MailRequestWorker(
        IMailRequestQueue queue,
        MailRequestRepository repository,
        MailSuppressionRepository suppressions,
        MailerTenantRegistry tenants,
        MailerOptions mailerOptions,
        MailerWorkerOptions workerOptions,
        MailerHealthcheckOptions healthcheckOptions,
        IMailDeliveryProvider deliveryProvider,
        ExpiredProcessingReaper expiredProcessingReaper,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        WorkerServiceStatus serviceStatus,
        MailerRuntimeMetrics runtimeMetrics,
        AttachmentSpool attachmentSpool,
        TimeProvider timeProvider,
        ILogger<MailRequestWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _suppressions = suppressions;
        _tenants = tenants;
        _mailerOptions = mailerOptions;
        _workerOptions = workerOptions;
        _healthcheckOptions = healthcheckOptions;
        _deliveryProvider = deliveryProvider;
        _expiredProcessingReaper = expiredProcessingReaper;
        _deliveryEventEnqueuer = deliveryEventEnqueuer;
        _serviceStatus = serviceStatus;
        _runtimeMetrics = runtimeMetrics;
        _attachmentSpool = attachmentSpool;
        _timeProvider = timeProvider;
        _logger = logger;
        _sendConcurrency = new SemaphoreSlim(_workerOptions.MaxSendConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WriteHeartbeatAsync(stoppingToken);
        _serviceStatus.SetWorkerRunning(true);
        try
        {
            await StartupRecoveryAsync(stoppingToken);
            await WorkLoopAsync(stoppingToken);
        }
        finally
        {
            _serviceStatus.SetWorkerRunning(false);
            await _inflightTracker.WaitForZeroAsync(
                _workerOptions.ShutdownDrainTimeout,
                _timeProvider,
                CancellationToken.None);

            if (_inflightTracker.InflightCount > 0)
            {
                _logger.LogWarning(
                    "Shutdown grace period elapsed with {InflightCount} in-flight mail deliveries still active.",
                    _inflightTracker.InflightCount);
            }
        }
    }

    private async Task StartupRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _expiredProcessingReaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, stoppingToken);

            if (await _repository.HasDispatchableWorkAsync(now, stoppingToken))
            {
                if (!_queue.TrySignalWorkAvailable())
                {
                    _logger.LogWarning("WorkAvailable channel is full during startup recovery.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SqliteDatabaseExceptionLogging.LogError(
                _logger,
                ex,
                "Mailer worker startup recovery failed due to SQLite storage full (SQLITE_FULL).",
                "Mailer worker startup recovery failed.");
        }
    }

    private async Task WorkLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasWork;
            try
            {
                using var heartbeatTimeout = new CancellationTokenSource(_healthcheckOptions.WorkerHeartbeatInterval);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, heartbeatTimeout.Token);
                hasWork = await _queue.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await WriteHeartbeatAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!hasWork)
                break;

            while (_queue.Reader.TryRead(out _)) { }

            await WriteHeartbeatAsync(stoppingToken);

            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SqliteDatabaseExceptionLogging.LogError(
                    _logger,
                    ex,
                    "Mailer worker drain loop failed due to SQLite storage full (SQLITE_FULL).",
                    "Mailer worker drain loop failed.");
            }
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
    drain:
        var batch = new List<MailRequestRow>(_workerOptions.BatchClaimSize);
        var now = _timeProvider.GetUtcNow();
        await _expiredProcessingReaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, stoppingToken);

        for (var i = 0; i < _workerOptions.BatchClaimSize; i++)
        {
            var lockToken = Guid.CreateVersion7(now);
            var claimed = await _repository.TryClaimOneAsync(
                now,
                _workerOptions.LeaseDuration,
                lockToken,
                stoppingToken);

            if (claimed is null)
            {
                break;
            }

            batch.Add(claimed);
        }

        if (batch.Count == 0)
        {
            return;
        }

        var sendTasks = batch.Select(row => DispatchClaimedAsync(row, stoppingToken));
        await Task.WhenAll(sendTasks);

        // Do not start another claim wave after shutdown began; semaphore waiters
        // already cancelled in DispatchClaimedAsync leave Processing for lease reclaim.
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (batch.Count == _workerOptions.BatchClaimSize)
        {
            await WriteHeartbeatAsync(stoppingToken);
            goto drain;
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _repository.UpsertHeartbeatAsync("worker", now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SqliteDatabaseExceptionLogging.LogWarning(
                _logger,
                ex,
                "Failed to update worker heartbeat due to SQLite storage full (SQLITE_FULL).",
                "Failed to update worker heartbeat.");
        }
    }

    private async Task DispatchClaimedAsync(MailRequestRow row, CancellationToken stoppingToken)
    {
        try
        {
            // Honor stoppingToken so later waves waiting on the semaphore do not
            // start new sends after shutdown begins (#271). In-flight sends still
            // use SendTimeout (not linked to stopping) and are drained in finally.
            await _sendConcurrency.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Claimed but never started: leave Processing for lease reclaim.
            return;
        }

        using var inflight = _inflightTracker.Enter();
        try
        {
            await SendAndFinalizeAsync(row, stoppingToken);
        }
        finally
        {
            _sendConcurrency.Release();
        }
    }

    private async Task SendAndFinalizeAsync(MailRequestRow row, CancellationToken stoppingToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var tenant = _tenants.Find(row.TenantId);
        if (tenant is null)
        {
            await FinalizeTerminalFailureAsync(
                row,
                startedAt,
                provider: "none",
                errorCode: "TENANT_NOT_CONFIGURED",
                errorMessage: "Tenant is not configured.");
            return;
        }

        // Attachment requests are an explicit ADR 0022 exception to the at-least-once model
        // below: request-unique submission evidence, at-most-once provider invocation, and
        // DeliveryUnknown on ambiguity. Ordinary (no-attachment) requests are entirely unchanged.
        if (row.AttachmentCount > 0)
        {
            await SendAttachmentRequestAndFinalizeAsync(row, tenant, startedAt, stoppingToken);
            return;
        }

        // A prior provider success may exist when finalize lost the lease race (#238).
        // Converge to Delivered without resending. Evidence superseded by Admin manual retry
        // (#268) is ignored so a new dispatch cycle still performs real sends.
        if (row.AttemptCount > 1)
        {
            var priorSuccess = await _repository.FindSuccessfulDeliveryAttemptAsync(row.Id, stoppingToken);
            if (priorSuccess is not null)
            {
                await ConvergeDeliveredFromPriorSuccessAsync(row, priorSuccess, startedAt);
                return;
            }
        }

        // Send-time suppression check (#303). Uses the same RecipientEmailNormalizer as
        // store (#301) and removal CLI (#400). Point lookup on UNIQUE (tenant_id, email).
        if (await _suppressions.ExistsAsync(row.TenantId, row.RecipientEmail, stoppingToken))
        {
            await FinalizeTerminalFailureAsync(
                row,
                startedAt,
                provider: "none",
                errorCode: MailDeliveryErrorCodes.RecipientSuppressed,
                errorMessage: "Recipient is on the suppression list.");
            return;
        }

        var providerName = _mailerOptions.ResolveProvider(tenant);
        var job = new MailSendJob(
            row.MailRequestId,
            row.SourceService,
            row.Subject,
            row.HtmlBody,
            row.TextBody,
            row.ReplyTo,
            row.RecipientEmail,
            row.RecipientDisplayName);

        MailDeliveryResult result;
        try
        {
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            sendTimeout.CancelAfter(_workerOptions.SendTimeout);
            result = await _deliveryProvider.SendAsync(job, tenant, providerName, sendTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.SendTimeout,
                $"Mail delivery exceeded {_workerOptions.SendTimeoutSeconds} seconds.",
                retryable: true);
        }

        var completedAt = _timeProvider.GetUtcNow();
        await FinalizeDeliveryResultAsync(row, tenant, providerName, result, startedAt, completedAt);
    }

    private async Task ConvergeDeliveredFromPriorSuccessAsync(
        MailRequestRow row,
        SuccessfulDeliveryAttempt priorSuccess,
        DateTimeOffset now)
    {
        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.TryMarkDeliveredAsync(
            row.Id,
            row.LockToken,
            now,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _runtimeMetrics.RecordFinalizeSkipped();
            _logger.LogWarning(
                "Skipped delivered converge for mail request {MailRequestId} with prior provider message id {ProviderMessageId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                priorSuccess.ProviderMessageId,
                row.Id,
                row.TenantId);
            return;
        }

        await _deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(row.Id, CancellationToken.None);
        _logger.LogInformation(
            "Converged mail request {MailRequestId} to Delivered from prior provider success without resending. RequestId={RequestId}; TenantId={TenantId}; PriorAttempt={PriorAttemptNumber}; Provider={Provider}; ProviderMessageId={ProviderMessageId}",
            row.MailRequestId,
            row.Id,
            row.TenantId,
            priorSuccess.AttemptNumber,
            priorSuccess.Provider,
            priorSuccess.ProviderMessageId);
    }

    /// <summary>
    /// Attachment-request send path (ADR 0022 D-08). Every reachable path (fresh dispatch,
    /// expired-lease reclaim, startup recovery, periodic sweep) goes through the ordinary claim
    /// mechanism and lands here, so checking submission evidence first -- before touching the
    /// spool or the provider -- is what implements "expired lease、startup recovery、periodic
    /// sweepはrequest単位のsubmission evidenceを先に確認する" without a separate reconciliation
    /// pass: a reclaimed row with existing evidence converges immediately; a reclaimed row with
    /// no evidence is provably pre-invocation and may start normally.
    /// </summary>
    private async Task SendAttachmentRequestAndFinalizeAsync(
        MailRequestRow row,
        MailerTenant tenant,
        DateTimeOffset startedAt,
        CancellationToken stoppingToken)
    {
        var evidence = await _repository.FindAttachmentSubmissionAsync(row.Id, stoppingToken);
        if (evidence is not null)
        {
            await ConvergeFromAttachmentEvidenceAsync(row, evidence, startedAt);
            return;
        }

        if (await _suppressions.ExistsAsync(row.TenantId, row.RecipientEmail, stoppingToken))
        {
            await FinalizeTerminalFailureAsync(
                row,
                startedAt,
                provider: "none",
                errorCode: MailDeliveryErrorCodes.RecipientSuppressed,
                errorMessage: "Recipient is on the suppression list.");
            return;
        }

        var attachmentRows = await _repository.ListAttachmentsAsync(row.Id, stoppingToken);
        var sendAttachments = new List<MailSendAttachment>(attachmentRows.Count);
        foreach (var attachment in attachmentRows)
        {
            var filePath = _attachmentSpool.GetCommittedFilePath(row.Id, attachment.SpoolKey);
            if (!File.Exists(filePath))
            {
                // DB row exists but the spool file is gone: never call the provider (ADR 0022 D-08).
                await FinalizeTerminalFailureAsync(
                    row,
                    startedAt,
                    provider: "none",
                    errorCode: MailDeliveryErrorCodes.AttachmentStorageMissing,
                    errorMessage: "Attachment spool file is missing.");
                return;
            }

            sendAttachments.Add(new MailSendAttachment(
                attachment.FileName,
                attachment.ContentType,
                attachment.ByteLength,
                filePath));
        }

        if (sendAttachments.Count == 0)
        {
            // attachment_count > 0 with no metadata rows should not happen; fail closed rather
            // than silently sending as if it were an ordinary request.
            await FinalizeTerminalFailureAsync(
                row,
                startedAt,
                provider: "none",
                errorCode: MailDeliveryErrorCodes.AttachmentStorageMissing,
                errorMessage: "Attachment metadata is missing.");
            return;
        }

        var providerName = _mailerOptions.ResolveProvider(tenant);

        // Defensive re-check with the same accept-time estimator (ADR 0022 D-02 step 2). Content
        // is immutable after acceptance, so this should already be within budget; exact
        // pre-serialization against the real provider request is a known gap (see report).
        var envelopeEstimate = AttachmentEnvelopeEstimator.EstimateUpperBound(new AttachmentEnvelopeInput(
            tenant.DefaultFrom.Email,
            row.RecipientEmail,
            row.RecipientDisplayName,
            row.Subject,
            row.TextBody,
            row.HtmlBody,
            row.ReplyTo,
            sendAttachments
                .Select(a => new AttachmentEnvelopeAttachment(a.FileName, a.ContentType, a.ByteLength))
                .ToArray()));
        if (envelopeEstimate > MailAttachmentLimits.MaxProviderEnvelopeBytes)
        {
            await FinalizeTerminalFailureAsync(
                row,
                startedAt,
                provider: providerName,
                errorCode: MailDeliveryErrorCodes.ProviderUnknown,
                errorMessage: "Provider envelope exceeds the configured limit at dispatch time.");
            return;
        }

        // Durable submission marker committed before any provider call (ADR 0022 D-08 provider
        // invocation boundary). TryInsertStartedAsync fails closed on a PK conflict, so a
        // concurrent/duplicate attempt never creates a second lifecycle.
        var started = await _repository.TryInsertAttachmentSubmissionStartedAsync(
            row.Id,
            providerName,
            row.LockToken,
            startedAt,
            stoppingToken);

        if (!started)
        {
            var raced = await _repository.FindAttachmentSubmissionAsync(row.Id, stoppingToken);
            if (raced is not null)
            {
                await ConvergeFromAttachmentEvidenceAsync(row, raced, startedAt);
            }

            return;
        }

        var job = new MailSendJob(
            row.MailRequestId,
            row.SourceService,
            row.Subject,
            row.HtmlBody,
            row.TextBody,
            row.ReplyTo,
            row.RecipientEmail,
            row.RecipientDisplayName,
            sendAttachments);

        MailDeliveryResult result;
        try
        {
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            sendTimeout.CancelAfter(_workerOptions.SendTimeout);
            result = await _deliveryProvider.SendAsync(job, tenant, providerName, sendTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Cannot disprove provider acceptance after a timeout -- Ambiguous, not retryable.
            result = MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.SendTimeout,
                $"Mail delivery exceeded {_workerOptions.SendTimeoutSeconds} seconds.",
                retryable: true);
        }

        var completedAt = _timeProvider.GetUtcNow();
        var outcome = AttachmentProviderResultClassifier.Classify(result);
        await FinalizeAttachmentResultAsync(row, providerName, result, outcome, startedAt, completedAt);
    }

    private async Task ConvergeFromAttachmentEvidenceAsync(
        MailRequestRow row,
        AttachmentSubmissionRow evidence,
        DateTimeOffset now)
    {
        // Recovery table (ADR 0022 D-08): Succeeded -> Delivered, DefinitiveFailed -> Failed,
        // Started-only or Unknown -> DeliveryUnknown. The provider is never re-invoked here.
        var requestState = evidence.SubmissionState switch
        {
            AttachmentSubmissionState.Succeeded => MailRequestState.Delivered,
            AttachmentSubmissionState.DefinitiveFailed => MailRequestState.Failed,
            _ => MailRequestState.DeliveryUnknown,
        };

        var isUnknownConvergence = requestState == MailRequestState.DeliveryUnknown;
        var errorMessage = isUnknownConvergence
            ? "Provider acceptance could not be confirmed during recovery."
            : null;

        var attempt = new MailAttemptInsert
        {
            RequestId = row.Id,
            AttemptNumber = row.AttemptCount,
            Provider = evidence.Provider,
            Status = requestState,
            ProviderMessageId = evidence.ProviderMessageId,
            ErrorCode = isUnknownConvergence ? MailDeliveryErrorCodes.DeliveryUnknown : null,
            ErrorMessage = errorMessage,
            Retryable = false,
            LockToken = row.LockToken,
            StartedAt = now,
            CompletedAt = now,
        };

        var submissionTerminalState = evidence.SubmissionState == AttachmentSubmissionState.Started
            ? AttachmentSubmissionState.Unknown
            : evidence.SubmissionState;

        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.FinalizeAttachmentSubmissionAsync(
            row.Id,
            row.LockToken,
            now,
            submissionTerminalState,
            evidence.ProviderMessageId,
            requestState,
            errorMessage,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped attachment evidence convergence for mail request {MailRequestId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                row.Id,
                row.TenantId);
            return;
        }

        await _deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(row.Id, CancellationToken.None);
        _logger.LogInformation(
            "Converged attachment mail request {MailRequestId} to {RequestState} from existing submission evidence ({SubmissionState}) without invoking the provider. RequestId={RequestId}; TenantId={TenantId}",
            row.MailRequestId,
            requestState,
            evidence.SubmissionState,
            row.Id,
            row.TenantId);
    }

    private async Task FinalizeAttachmentResultAsync(
        MailRequestRow row,
        string providerName,
        MailDeliveryResult result,
        AttachmentProviderOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var (submissionState, requestState) = outcome switch
        {
            AttachmentProviderOutcome.Succeeded =>
                (AttachmentSubmissionState.Succeeded, MailRequestState.Delivered),
            AttachmentProviderOutcome.DefinitiveFailed =>
                (AttachmentSubmissionState.DefinitiveFailed, MailRequestState.Failed),
            _ => (AttachmentSubmissionState.Unknown, MailRequestState.DeliveryUnknown),
        };

        var sanitizedError = result.ErrorMessage is null
            ? null
            : ProviderErrorSanitizer.Sanitize(result.ErrorMessage);
        var isUnknown = outcome == AttachmentProviderOutcome.Ambiguous;

        var attempt = new MailAttemptInsert
        {
            RequestId = row.Id,
            AttemptNumber = row.AttemptCount,
            Provider = providerName,
            Status = requestState,
            ProviderMessageId = result.ProviderMessageId,
            ErrorCode = isUnknown ? MailDeliveryErrorCodes.DeliveryUnknown : result.ErrorCode,
            ErrorMessage = sanitizedError,
            Retryable = false,
            LockToken = row.LockToken,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.FinalizeAttachmentSubmissionAsync(
            row.Id,
            row.LockToken,
            completedAt,
            submissionState,
            result.ProviderMessageId,
            requestState,
            sanitizedError,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped attachment finalize for mail request {MailRequestId} with provider message id {ProviderMessageId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                result.ProviderMessageId,
                row.Id,
                row.TenantId);
            return;
        }

        await _deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(row.Id, CancellationToken.None);

        if (requestState == MailRequestState.DeliveryUnknown)
        {
            _logger.LogWarning(
                "Attachment mail request {MailRequestId} converged to DeliveryUnknown after attempt {AttemptNumber} via provider {Provider}: provider acceptance could not be proven or disproven. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                row.AttemptCount,
                providerName,
                row.Id,
                row.TenantId);
        }
        else if (requestState == MailRequestState.Failed)
        {
            _logger.LogWarning(
                "Attachment mail request {MailRequestId} failed terminally after attempt {AttemptNumber} via provider {Provider}. RequestId={RequestId}; TenantId={TenantId}; ErrorCode={ErrorCode}",
                row.MailRequestId,
                row.AttemptCount,
                providerName,
                row.Id,
                row.TenantId,
                result.ErrorCode);
        }
    }

    private async Task FinalizeDeliveryResultAsync(
        MailRequestRow row,
        MailerTenant tenant,
        string providerName,
        MailDeliveryResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        MailRequestFinalizeOutcome outcome;
        DateTimeOffset? nextAttemptAt = null;
        MailRequestState attemptStatus;

        if (result.Succeeded)
        {
            outcome = MailRequestFinalizeOutcome.Delivered;
            attemptStatus = MailRequestState.Delivered;
        }
        else if (result.Retryable && row.AttemptCount < row.MaxAttempts)
        {
            outcome = MailRequestFinalizeOutcome.RetryScheduled;
            attemptStatus = MailRequestState.Failed;
            nextAttemptAt = ComputeNextAttemptAt(tenant, row.AttemptCount, completedAt);
        }
        else
        {
            outcome = result.Retryable
                ? MailRequestFinalizeOutcome.DeadLettered
                : MailRequestFinalizeOutcome.Failed;
            attemptStatus = outcome == MailRequestFinalizeOutcome.DeadLettered
                ? MailRequestState.DeadLettered
                : MailRequestState.Failed;
        }

        // Defense-in-depth: sanitize before persisting or logging even though
        // provider catch blocks already sanitize. This guards against callers
        // (e.g. test stubs) that pass raw messages into MailDeliveryResult.Failure.
        var sanitizedError = result.ErrorMessage is null
            ? null
            : ProviderErrorSanitizer.Sanitize(result.ErrorMessage);

        var attempt = new MailAttemptInsert
        {
            RequestId = row.Id,
            AttemptNumber = row.AttemptCount,
            Provider = providerName,
            Status = attemptStatus,
            ProviderMessageId = result.ProviderMessageId,
            ErrorCode = result.ErrorCode,
            ErrorMessage = sanitizedError,
            Retryable = result.Retryable,
            LockToken = row.LockToken,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.FinalizeAsync(
            row.Id,
            row.LockToken,
            completedAt,
            outcome,
            nextAttemptAt,
            sanitizedError,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped finalize for mail request {MailRequestId} with provider message id {ProviderMessageId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                result.ProviderMessageId,
                row.Id,
                row.TenantId);
            return;
        }

        if (outcome is MailRequestFinalizeOutcome.Delivered
            or MailRequestFinalizeOutcome.Failed
            or MailRequestFinalizeOutcome.DeadLettered)
        {
            await _deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(row.Id, CancellationToken.None);
        }

        if (outcome == MailRequestFinalizeOutcome.DeadLettered)
        {
            _logger.LogError(
                "Mail request {MailRequestId} was dead-lettered after attempt {AttemptNumber} via provider {Provider}. RequestId={RequestId}; TenantId={TenantId}; ErrorCode={ErrorCode}; ErrorMessage={ErrorMessage}",
                row.MailRequestId,
                row.AttemptCount,
                providerName,
                row.Id,
                row.TenantId,
                result.ErrorCode,
                sanitizedError);
        }
        else if (outcome == MailRequestFinalizeOutcome.Failed)
        {
            _logger.LogWarning(
                "Mail request {MailRequestId} failed terminally after attempt {AttemptNumber} via provider {Provider}. RequestId={RequestId}; TenantId={TenantId}; ErrorCode={ErrorCode}; ErrorMessage={ErrorMessage}",
                row.MailRequestId,
                row.AttemptCount,
                providerName,
                row.Id,
                row.TenantId,
                result.ErrorCode,
                sanitizedError);
        }
    }

    private async Task<bool> FinalizeTerminalFailureAsync(
        MailRequestRow row,
        DateTimeOffset now,
        string provider,
        string errorCode,
        string errorMessage)
    {
        var attempt = new MailAttemptInsert
        {
            RequestId = row.Id,
            AttemptNumber = row.AttemptCount,
            Provider = provider,
            Status = MailRequestState.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Retryable = false,
            LockToken = row.LockToken,
            StartedAt = now,
            CompletedAt = now,
        };

        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.FinalizeAsync(
            row.Id,
            row.LockToken,
            now,
            MailRequestFinalizeOutcome.Failed,
            nextAttemptAt: null,
            errorMessage,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped terminal failure finalize for mail request {MailRequestId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                row.Id,
                row.TenantId);
            return false;
        }

        // Count suppressed blocks from DB finalize success only — before best-effort webhook
        // enqueue, which must not gate the metric (#303 review).
        if (string.Equals(errorCode, MailDeliveryErrorCodes.RecipientSuppressed, StringComparison.Ordinal))
        {
            _runtimeMetrics.RecordSuppressedSend();
        }

        _logger.LogWarning(
            "Mail request {MailRequestId} failed terminally after attempt {AttemptNumber} via provider {Provider}. RequestId={RequestId}; TenantId={TenantId}; ErrorCode={ErrorCode}; ErrorMessage={ErrorMessage}",
            row.MailRequestId,
            row.AttemptCount,
            provider,
            row.Id,
            row.TenantId,
            errorCode,
            errorMessage);

        // Post-commit best-effort: must not throw or gate the finalize/metrics outcome (#303 review).
        await _deliveryEventEnqueuer.TryEnqueueAfterCommitAsync(row.Id);
        return true;
    }

    private static DateTimeOffset ComputeNextAttemptAt(
        MailerTenant tenant,
        int attemptCount,
        DateTimeOffset completedAt)
    {
        var delaySeconds = Math.Min(
            tenant.Retry.MaxDelaySeconds,
            tenant.Retry.InitialDelaySeconds * Math.Pow(2, Math.Max(0, attemptCount - 1)));
        return completedAt.AddSeconds(delaySeconds);
    }

    public override void Dispose()
    {
        _sendConcurrency.Dispose();
        base.Dispose();
    }
}
