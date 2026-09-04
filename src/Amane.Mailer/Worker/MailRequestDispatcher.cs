using Amane.Mailer.Attachments.Provider;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Worker;

public sealed class MailRequestDispatcher
{
    private readonly MailRequestRepository _repository;
    private readonly MailerTenantRegistry _tenants;
    private readonly MailerOptions _mailerOptions;
    private readonly MailerWorkerOptions _workerOptions;
    private readonly IMailDeliveryProvider _deliveryProvider;
    private readonly DeliveryEventEnqueuer _deliveryEventEnqueuer;
    private readonly MailerRuntimeMetrics _runtimeMetrics;
    private readonly AttachmentSpool _attachmentSpool;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MailRequestWorker> _logger;

    public MailRequestDispatcher(
        MailRequestRepository repository,
        MailerTenantRegistry tenants,
        MailerOptions mailerOptions,
        MailerWorkerOptions workerOptions,
        IMailDeliveryProvider deliveryProvider,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        MailerRuntimeMetrics runtimeMetrics,
        AttachmentSpool attachmentSpool,
        TimeProvider timeProvider,
        ILogger<MailRequestWorker> logger)
    {
        _repository = repository;
        _tenants = tenants;
        _mailerOptions = mailerOptions;
        _workerOptions = workerOptions;
        _deliveryProvider = deliveryProvider;
        _deliveryEventEnqueuer = deliveryEventEnqueuer;
        _runtimeMetrics = runtimeMetrics;
        _attachmentSpool = attachmentSpool;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task DispatchAsync(MailRequestRow row, CancellationToken stoppingToken)
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

        var providerName = _mailerOptions.ResolveProvider(tenant);

        // Provider invocation boundary + suppression precheck (ADR 0023 D-03/D-04/D-05,
        // Issue #546): a durable Started evidence row commits, and all canonical recipients move
        // to Pending, before any provider call. A suppression hit converges the whole request to
        // Failed/DefinitelyNotSubmitted with zero provider invocations, all in one fenced SQLite
        // transaction. Every reachable path (fresh dispatch, expired-lease reclaim, startup
        // recovery, periodic sweep) goes through TryClaimOneAsync and lands here, so checking
        // existing evidence first -- before touching the provider -- implements recovery without
        // a separate reconciliation pass, mirroring SendAttachmentRequestAndFinalizeAsync's ADR
        // 0022 evidence-first design below.
        var invocation = await _repository.TryPreparePlainProviderInvocationAsync(
            row.Id,
            row.TenantId,
            providerName,
            row.LockToken,
            row.AttemptCount,
            stoppingToken);

        switch (invocation.Outcome)
        {
            case PlainProviderInvocationOutcome.FenceFailed:
                // Lease expired or lock token superseded before we could act: leave Processing
                // for lease reclaim -- never call the provider under a claim we no longer hold.
                return;

            case PlainProviderInvocationOutcome.Suppressed:
                // Terminal Failed/DefinitelyNotSubmitted already committed atomically with the
                // suppression check itself; zero provider invocations (Issue #546 maintainer
                // decision). Nothing further to finalize here.
                _logger.LogWarning(
                    "Mail request {MailRequestId} failed terminally before provider invocation because at least one recipient is suppressed. RequestId={RequestId}; TenantId={TenantId}",
                    row.MailRequestId,
                    row.Id,
                    row.TenantId);
                await _deliveryEventEnqueuer.TryEnqueueAfterCommitAsync(row.Id);
                return;

            case PlainProviderInvocationOutcome.ExistingEvidence:
                await ConvergeFromPlainEvidenceAsync(row, invocation.ExistingEvidence!, startedAt);
                return;

            case PlainProviderInvocationOutcome.Started:
                break;
        }

        var recipients = invocation.Recipients!;
        var job = BuildMailSendJob(row, recipients, attachments: null);

        MailDeliveryResult result;
        try
        {
            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            sendTimeout.CancelAfter(_workerOptions.SendTimeout);
            result = await _deliveryProvider.SendAsync(job, tenant, providerName, sendTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Cannot disprove provider acceptance after a timeout -- Unknown, not retryable
            // (ADR 0023 D-04: timeout/network/protocol loss must never be classified as
            // DefinitelyNotSubmitted or DefinitelyRejected on retryability alone).
            result = MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.SendTimeout,
                $"Mail delivery exceeded {_workerOptions.SendTimeoutSeconds} seconds.",
                retryable: true);
        }

        var completedAt = _timeProvider.GetUtcNow();

        // Shared tri-state classifier (Succeeded/DefinitiveFailed/Ambiguous) already used for
        // attachment requests (ADR 0022 D-08): identical MailDeliveryResult inputs must classify
        // identically regardless of whether the request carries attachments. See Issue #546:
        // ACS's exact-operation-ID-reuse 400 (from #525/PR #529) must not be extrapolated into a
        // general "retries are safe" rule -- this classifier already keys off the actual ACS/SMTP
        // error code and retryability, not an assumption about retries.
        var outcome = AttachmentProviderResultClassifier.Classify(result);
        await FinalizePlainDeliveryResultAsync(row, providerName, result, outcome, startedAt, completedAt);
    }

    private static MailSendJob BuildMailSendJob(
        MailRequestRow row,
        IReadOnlyList<MailRequestRecipientRow> recipients,
        IReadOnlyList<MailSendAttachment>? attachments) =>
        new(
            row.MailRequestId,
            row.SourceService,
            row.Subject,
            row.HtmlBody,
            row.TextBody,
            row.ReplyTo,
            To: SelectRole(recipients, MailRecipientRole.To),
            Cc: SelectRole(recipients, MailRecipientRole.Cc),
            Bcc: SelectRole(recipients, MailRecipientRole.Bcc),
            Attachments: attachments);

    private static IReadOnlyList<MailSendRecipient> SelectRole(
        IReadOnlyList<MailRequestRecipientRow> recipients,
        MailRecipientRole role)
    {
        List<MailSendRecipient>? mapped = null;
        foreach (var recipient in recipients)
        {
            if (recipient.Role != role)
            {
                continue;
            }

            (mapped ??= []).Add(new MailSendRecipient(recipient.Address, recipient.DisplayName));
        }

        return mapped ?? [];
    }

    /// <summary>
    /// Recovery table (ADR 0023 D-04, mirrors <see cref="ConvergeFromAttachmentEvidenceAsync"/>):
    /// Accepted -&gt; Delivered, DefinitelyRejected -&gt; Failed, Started-only or Unknown -&gt;
    /// DeliveryUnknown. The provider is never re-invoked here. A DefinitelyNotSubmitted read here
    /// would only ever be the suppression-terminal combination (Issue #546 maintainer decision),
    /// which always coincides with request Failed in the same transaction it was written in and
    /// is therefore never observed back in Processing/reclaimable in the first place.
    /// </summary>
    private async Task ConvergeFromPlainEvidenceAsync(
        MailRequestRow row,
        MailPlainSubmissionRow evidence,
        DateTimeOffset now)
    {
        var (requestState, recipientState) = evidence.EvidenceState switch
        {
            MailPlainSubmissionEvidenceState.Accepted =>
                (MailRequestState.Delivered, (MailRecipientDeliveryState?)null),
            MailPlainSubmissionEvidenceState.DefinitelyRejected =>
                (MailRequestState.Failed, MailRecipientDeliveryState.Failed),
            _ => (MailRequestState.DeliveryUnknown, MailRecipientDeliveryState.Unknown),
        };

        var isUnknownConvergence = requestState == MailRequestState.DeliveryUnknown;
        var errorMessage = isUnknownConvergence
            ? "Provider acceptance could not be confirmed during recovery."
            : null;

        var targetEvidenceState = evidence.EvidenceState == MailPlainSubmissionEvidenceState.Started
            ? MailPlainSubmissionEvidenceState.Unknown
            : evidence.EvidenceState;

        var attempt = new MailAttemptInsert
        {
            RequestId = row.Id,
            AttemptNumber = row.AttemptCount,
            Provider = evidence.Provider ?? "none",
            Status = requestState,
            ProviderMessageId = evidence.ProviderMessageId,
            ErrorCode = isUnknownConvergence ? MailDeliveryErrorCodes.DeliveryUnknown : null,
            ErrorMessage = errorMessage,
            Retryable = false,
            LockToken = row.LockToken,
            StartedAt = now,
            CompletedAt = now,
        };

        using var finalizeTimeout = new CancellationTokenSource(_workerOptions.FinalizeTimeout);
        var finalized = await _repository.FinalizePlainSubmissionAsync(
            row.Id,
            requestLockToken: row.LockToken,
            evidenceClaimToken: evidence.ClaimToken ?? row.LockToken,
            expectedEvidenceState: evidence.EvidenceState,
            now,
            targetEvidenceState,
            evidence.ProviderMessageId,
            requestState,
            recipientState,
            errorMessage,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped plain evidence convergence for mail request {MailRequestId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                row.Id,
                row.TenantId);
            return;
        }

        await _deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(row.Id, CancellationToken.None);
        _logger.LogInformation(
            "Converged mail request {MailRequestId} to {RequestState} from existing plain submission evidence ({EvidenceState}) without invoking the provider. RequestId={RequestId}; TenantId={TenantId}",
            row.MailRequestId,
            requestState,
            evidence.EvidenceState,
            row.Id,
            row.TenantId);
    }

    private async Task FinalizePlainDeliveryResultAsync(
        MailRequestRow row,
        string providerName,
        MailDeliveryResult result,
        AttachmentProviderOutcome outcome,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var (targetEvidenceState, requestState, recipientState) = outcome switch
        {
            AttachmentProviderOutcome.Succeeded =>
                (MailPlainSubmissionEvidenceState.Accepted, MailRequestState.Delivered, (MailRecipientDeliveryState?)null),
            AttachmentProviderOutcome.DefinitiveFailed =>
                (MailPlainSubmissionEvidenceState.DefinitelyRejected, MailRequestState.Failed, MailRecipientDeliveryState.Failed),
            _ => (MailPlainSubmissionEvidenceState.Unknown, MailRequestState.DeliveryUnknown, MailRecipientDeliveryState.Unknown),
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
        var finalized = await _repository.FinalizePlainSubmissionAsync(
            row.Id,
            requestLockToken: row.LockToken,
            evidenceClaimToken: row.LockToken,
            expectedEvidenceState: MailPlainSubmissionEvidenceState.Started,
            completedAt,
            targetEvidenceState,
            result.ProviderMessageId,
            requestState,
            recipientState,
            sanitizedError,
            attempt,
            finalizeTimeout.Token);

        if (!finalized)
        {
            _logger.LogWarning(
                "Skipped plain finalize for mail request {MailRequestId} with provider message id {ProviderMessageId} because the lock token expired or was superseded. RequestId={RequestId}; TenantId={TenantId}",
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
                "Mail request {MailRequestId} converged to DeliveryUnknown after attempt {AttemptNumber} via provider {Provider}: provider acceptance could not be proven or disproven. RequestId={RequestId}; TenantId={TenantId}",
                row.MailRequestId,
                row.AttemptCount,
                providerName,
                row.Id,
                row.TenantId);
        }
        else if (requestState == MailRequestState.Failed)
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

        // Canonical recipient suppression precheck (ADR 0023 D-03/D-05, Issue #546 review finding
        // F2): checks every To/Cc/Bcc recipient, not only the legacy single-To shadow column. A
        // suppression hit converges the whole request to Failed atomically inside the precheck
        // itself (mirroring the plain path's suppression branch); this method never calls
        // FinalizeTerminalFailureAsync for that case because the store already committed it.
        var suppressionPrecheck = await _repository.TryApplyAttachmentSuppressionPrecheckAsync(
            row.Id,
            row.TenantId,
            row.LockToken,
            row.AttemptCount,
            stoppingToken);

        switch (suppressionPrecheck.Outcome)
        {
            case AttachmentSuppressionPrecheckOutcome.FenceFailed:
                // Lease expired or lock token superseded before we could act: leave Processing
                // for lease reclaim -- never call the provider under a claim we no longer hold.
                return;

            case AttachmentSuppressionPrecheckOutcome.Suppressed:
                _logger.LogWarning(
                    "Mail request {MailRequestId} failed terminally before provider invocation because at least one recipient is suppressed. RequestId={RequestId}; TenantId={TenantId}",
                    row.MailRequestId,
                    row.Id,
                    row.TenantId);
                await _deliveryEventEnqueuer.TryEnqueueAfterCommitAsync(row.Id);

                // The suppression precheck already committed the terminal request transaction.
                // Clean up only after that commit; the reconciliation service remains the safety
                // net if this best-effort delete cannot remove the committed spool immediately.
                _attachmentSpool.TryDeleteCommitted(row.Id);
                _runtimeMetrics.RecordAttachmentSpoolCleanup("committed_prompt");
                return;

            case AttachmentSuppressionPrecheckOutcome.NotSuppressed:
                break;
        }

        var recipients = suppressionPrecheck.Recipients!;

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
            recipients
                .Select(r => new AttachmentEnvelopeRecipient(r.Address, r.DisplayName))
                .ToArray(),
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
        // invocation boundary). TryInsertStartedAsync fails closed on a PK conflict, an
        // expired lease, or a superseded lock token. The fencing timestamp is read inside the
        // store only after BEGIN IMMEDIATE has established write ownership -- a pre-call
        // GetUtcNow here would still be stale across SQLite busy wait on the write lock.
        var started = await _repository.TryInsertAttachmentSubmissionStartedAsync(
            row.Id,
            providerName,
            row.LockToken,
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

        // Canonical recipient mapping (ADR 0023 D-03, Issue #546 review finding F2): the shared
        // BuildMailSendJob helper maps every To/Cc/Bcc recipient row, matching the plain path.
        // The provider envelope is built from canonical recipients rather than the legacy shadow
        // columns.
        var job = BuildMailSendJob(row, recipients, sendAttachments);

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
            requestLockToken: row.LockToken,
            submissionLockToken: evidence.LockToken,
            now,
            expectedSubmissionState: evidence.SubmissionState,
            targetSubmissionState: submissionTerminalState,
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

        // Best-effort prompt cleanup after the durable terminal commit above (ADR 0022 D-08
        // spool lifecycle); AttachmentSpoolReconciliationService is the safety net on failure.
        _attachmentSpool.TryDeleteCommitted(row.Id);
        _runtimeMetrics.RecordAttachmentSpoolCleanup("committed_prompt");
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
            requestLockToken: row.LockToken,
            submissionLockToken: row.LockToken,
            completedAt,
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: submissionState,
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

        // Best-effort prompt cleanup after the durable terminal commit above (ADR 0022 D-08
        // spool lifecycle); AttachmentSpoolReconciliationService is the safety net on failure.
        _attachmentSpool.TryDeleteCommitted(row.Id);
        _runtimeMetrics.RecordAttachmentSpoolCleanup("committed_prompt");
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

        // Harmless no-op for ordinary (non-attachment) requests and for the already-missing
        // ATTACHMENT_STORAGE_MISSING case; prompt cleanup for every other attachment terminal
        // failure reached via this shared path (ADR 0022 D-08 spool lifecycle).
        if (row.AttachmentCount > 0)
        {
            _attachmentSpool.TryDeleteCommitted(row.Id);
            _runtimeMetrics.RecordAttachmentSpoolCleanup("committed_prompt");
        }

        return true;
    }

}
