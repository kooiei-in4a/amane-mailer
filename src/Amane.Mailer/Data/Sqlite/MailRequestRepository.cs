using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Data.Sqlite;

// Unsealed + virtual find/insert/status/cancel/reschedule: test seams for
// STORAGE_FULL (#244), post-commit status re-read independence (#327), and
// mail-request handler unit tests (#348). Prefer keeping other members
// non-virtual; do not treat this as a public extension point.
public class MailRequestRepository
{
    private readonly MailRequestClaimStore _claimStore;
    private readonly MailRequestAcceptStore _acceptStore;
    private readonly MailRequestConsumerMutations _consumerMutations;
    private readonly MailRequestAdminQueries _adminQueries;
    private readonly WorkerHeartbeatStore _heartbeatStore;
    private readonly MailRequestAttachmentStore _attachmentStore;
    private readonly MailAttachmentSubmissionStore _attachmentSubmissionStore;
    private readonly MailRequestRecipientStore _recipientStore;
    private readonly MailPlainSubmissionStore _plainSubmissionStore;

    internal const string OperatorCancelledLastErrorMessage =
        MailRequestConsumerMutations.OperatorCancelledLastErrorMessage;

    internal const string ConsumerCancelledLastErrorMessage =
        MailRequestConsumerMutations.ConsumerCancelledLastErrorMessage;

    internal const string SupersededByManualRetryErrorCode =
        MailRequestConsumerMutations.SupersededByManualRetryErrorCode;

    public MailRequestRepository(
        MailRequestClaimStore claimStore,
        MailRequestAcceptStore acceptStore,
        MailRequestConsumerMutations consumerMutations,
        MailRequestAdminQueries adminQueries,
        WorkerHeartbeatStore heartbeatStore,
        MailRequestAttachmentStore attachmentStore,
        MailAttachmentSubmissionStore attachmentSubmissionStore,
        MailRequestRecipientStore recipientStore,
        MailPlainSubmissionStore plainSubmissionStore)
    {
        _claimStore = claimStore;
        _acceptStore = acceptStore;
        _consumerMutations = consumerMutations;
        _adminQueries = adminQueries;
        _heartbeatStore = heartbeatStore;
        _attachmentStore = attachmentStore;
        _attachmentSubmissionStore = attachmentSubmissionStore;
        _recipientStore = recipientStore;
        _plainSubmissionStore = plainSubmissionStore;
    }

    public static MailRequestRepository CreateStandalone(
        SqliteConnectionFactory connections,
        MailerRuntimeMetrics? runtimeMetrics = null,
        Amane.Mailer.Attachments.Spool.AttachmentSpool? attachmentSpool = null,
        TimeProvider? timeProvider = null) =>
        new(
            new MailRequestClaimStore(connections, runtimeMetrics),
            new MailRequestAcceptStore(connections, attachmentSpool, runtimeMetrics),
            new MailRequestConsumerMutations(connections),
            new MailRequestAdminQueries(connections),
            new WorkerHeartbeatStore(connections),
            new MailRequestAttachmentStore(connections),
            new MailAttachmentSubmissionStore(connections, timeProvider ?? TimeProvider.System),
            new MailRequestRecipientStore(connections),
            new MailPlainSubmissionStore(connections, timeProvider ?? TimeProvider.System, runtimeMetrics));

    public Task<AdminMailRequestListPage> ListForAdminAsync(
        AdminMailRequestListQuery query,
        CancellationToken cancellationToken = default) =>
        _adminQueries.ListForAdminAsync(query, cancellationToken);

    public Task<AdminDeadLetterListPage> ListDeadLettersForAdminAsync(
        AdminDeadLetterListQuery query,
        CancellationToken cancellationToken = default) =>
        _adminQueries.ListDeadLettersForAdminAsync(query, cancellationToken);

    public Task<int> CountDeadLettersForAdminAsync(
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default) =>
        _adminQueries.CountDeadLettersForAdminAsync(allowedTenantIds, cancellationToken);

    public Task<AdminMailRequestDetail?> GetDetailForAdminAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default) =>
        _adminQueries.GetDetailForAdminAsync(id, allowedTenantIds, cancellationToken);

    public Task<IReadOnlyList<AdminMailAttemptRow>> ListAttemptsForAdminAsync(
        Guid requestId,
        IReadOnlySet<Guid>? allowedTenantIds,
        CancellationToken cancellationToken = default) =>
        _adminQueries.ListAttemptsForAdminAsync(requestId, allowedTenantIds, cancellationToken);

    public virtual Task<MailRequestIdempotencyRow?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken = default) =>
        _acceptStore.FindByIdempotencyKeyAsync(tenantId, sourceService, mailRequestId, cancellationToken);

    public virtual Task<MailRequestStatusRow?> GetStatusByIdempotencyKeyAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken = default) =>
        _acceptStore.GetStatusByIdempotencyKeyAsync(tenantId, sourceService, mailRequestId, cancellationToken);

    public virtual Task InsertAcceptedAsync(
        AcceptedMailRequestInsert insert,
        CancellationToken cancellationToken = default) =>
        _acceptStore.InsertAcceptedAsync(insert, cancellationToken);

    public Task<MailRequestRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        Guid lockToken,
        CancellationToken cancellationToken = default) =>
        _claimStore.TryClaimOneAsync(now, leaseDuration, lockToken, cancellationToken);

    public Task<IReadOnlyList<ExpiredProcessingDeadLetteredRequest>> DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        _claimStore.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, batchSize, cancellationToken);

    public Task<bool> FinalizeAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        MailRequestFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default) =>
        _claimStore.FinalizeAsync(
            id,
            lockToken,
            now,
            outcome,
            nextAttemptAt,
            lastErrorMessage,
            attempt,
            cancellationToken);

    public Task<SuccessfulDeliveryAttempt?> FindSuccessfulDeliveryAttemptAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _claimStore.FindSuccessfulDeliveryAttemptAsync(requestId, cancellationToken);

    public Task<bool> TryMarkDeliveredAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _claimStore.TryMarkDeliveredAsync(id, lockToken, now, cancellationToken);

    public Task<bool> HasDispatchableWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _claimStore.HasDispatchableWorkAsync(now, cancellationToken);

    public Task<MailRequestDispatchState?> FindDispatchStateByMailRequestIdAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken = default) =>
        _claimStore.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);

    public Task<int> CountAttemptsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _claimStore.CountAttemptsAsync(requestId, cancellationToken);

    public Task<ManualMailRequestMutationResult> TryManualRetryAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds,
        DateTimeOffset now,
        AdminAuditRepository auditRepository,
        AdminAuditEvent auditTemplate,
        CancellationToken cancellationToken = default) =>
        _consumerMutations.TryManualRetryAsync(
            id,
            allowedTenantIds,
            now,
            auditRepository,
            auditTemplate,
            cancellationToken);

    public Task<ManualMailRequestMutationResult> TryManualCancelAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds,
        DateTimeOffset now,
        AdminAuditRepository auditRepository,
        AdminAuditEvent auditTemplate,
        CancellationToken cancellationToken = default) =>
        _consumerMutations.TryManualCancelAsync(
            id,
            allowedTenantIds,
            now,
            auditRepository,
            auditTemplate,
            cancellationToken);

    public virtual Task<ConsumerMailRequestMutationResult> TryConsumerCancelAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _consumerMutations.TryConsumerCancelAsync(tenantId, sourceService, mailRequestId, now, cancellationToken);

    public virtual Task<ConsumerMailRequestMutationResult> TryRescheduleAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        DateTimeOffset? scheduledAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _consumerMutations.TryRescheduleAsync(
            tenantId,
            sourceService,
            mailRequestId,
            scheduledAt,
            now,
            cancellationToken);

    public Task<int> DeleteExpiredCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        _claimStore.DeleteExpiredCompletedAsync(completedBefore, batchSize, cancellationToken);

    public Task UpsertHeartbeatAsync(
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _heartbeatStore.UpsertHeartbeatAsync(name, now, cancellationToken);

    public Task<IReadOnlyList<WorkerHeartbeat>> GetHeartbeatsAsync(
        CancellationToken cancellationToken = default) =>
        _heartbeatStore.GetHeartbeatsAsync(cancellationToken);

    public Task<IReadOnlyList<AttachmentMetadataRow>> ListAttachmentsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _attachmentStore.ListByRequestIdAsync(requestId, cancellationToken);

    public Task<AttachmentSubmissionRow?> FindAttachmentSubmissionAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _attachmentSubmissionStore.FindAsync(requestId, cancellationToken);

    public Task<bool> TryInsertAttachmentSubmissionStartedAsync(
        Guid requestId,
        string provider,
        Guid lockToken,
        CancellationToken cancellationToken = default) =>
        _attachmentSubmissionStore.TryInsertStartedAsync(requestId, provider, lockToken, cancellationToken);

    public Task<bool> FinalizeAttachmentSubmissionAsync(
        Guid id,
        Guid requestLockToken,
        Guid submissionLockToken,
        DateTimeOffset now,
        AttachmentSubmissionState expectedSubmissionState,
        AttachmentSubmissionState targetSubmissionState,
        string? providerMessageId,
        MailRequestState requestTerminalState,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default) =>
        _claimStore.FinalizeAttachmentSubmissionAsync(
            id,
            requestLockToken,
            submissionLockToken,
            now,
            expectedSubmissionState,
            targetSubmissionState,
            providerMessageId,
            requestTerminalState,
            lastErrorMessage,
            attempt,
            cancellationToken);

    public Task<IReadOnlyList<MailRequestRecipientRow>> ListRecipientsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _recipientStore.ListByRequestIdAsync(requestId, cancellationToken);

    public Task<MailPlainSubmissionRow?> FindPlainSubmissionAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        _plainSubmissionStore.FindAsync(requestId, cancellationToken);

    public Task<PlainProviderInvocationResult> TryPreparePlainProviderInvocationAsync(
        Guid requestId,
        Guid tenantId,
        string provider,
        Guid lockToken,
        int attemptNumber,
        CancellationToken cancellationToken = default) =>
        _plainSubmissionStore.TryPrepareProviderInvocationAsync(
            requestId,
            tenantId,
            provider,
            lockToken,
            attemptNumber,
            cancellationToken);

    public Task<bool> FinalizePlainSubmissionAsync(
        Guid requestId,
        Guid requestLockToken,
        Guid evidenceClaimToken,
        MailPlainSubmissionEvidenceState expectedEvidenceState,
        DateTimeOffset now,
        MailPlainSubmissionEvidenceState targetEvidenceState,
        string? providerMessageId,
        MailRequestState requestTerminalState,
        MailRecipientDeliveryState? recipientTargetState,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default) =>
        _plainSubmissionStore.FinalizeAsync(
            requestId,
            requestLockToken,
            evidenceClaimToken,
            expectedEvidenceState,
            now,
            targetEvidenceState,
            providerMessageId,
            requestTerminalState,
            recipientTargetState,
            lastErrorMessage,
            attempt,
            cancellationToken);
}
