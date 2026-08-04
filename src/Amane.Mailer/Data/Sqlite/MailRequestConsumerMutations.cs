using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestConsumerMutations(SqliteConnectionFactory connections)
{
    internal const string OperatorCancelledLastErrorMessage = "operator_cancelled";
    internal const string ConsumerCancelledLastErrorMessage = "consumer_cancelled";

    /// <summary>
    /// Marks a prior-cycle Delivered attempt as ineligible for worker prior-success
    /// convergence after Admin manual retry (#268). Status and provider_message_id stay
    /// intact for Admin history.
    /// </summary>
    internal const string SupersededByManualRetryErrorCode = "superseded_by_manual_retry";

    public async Task<ManualMailRequestMutationResult> TryManualRetryAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds,
        DateTimeOffset now,
        AdminAuditRepository auditRepository,
        AdminAuditEvent auditTemplate,
        CancellationToken cancellationToken = default)
    {
        if (allowedTenantIds is { Count: 0 })
            return new(ManualMailRequestMutationStatus.NotFound);

        var nowStorage = SqliteTime.ToStorageUtc(now);

        // attachment_count = 0 is the explicit ADR 0022 D-08 exception to ADR 0015: a request
        // carrying canonical attachment metadata can never be retried from any terminal state,
        // regardless of status. attachment_count is a DB-only column set once at accept time
        // from verified metadata -- never re-derived from public input (D-08).
        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @QueuedStatus,
                attempt_count = 0,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                completed_at = NULL,
                delivered_at = NULL,
                failed_at = NULL,
                updated_at = @Now
            WHERE id = @Id
              AND status IN (@DeadLetteredStatus, @FailedStatus)
              AND attachment_count = 0
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                var where = new StringBuilder(updateSql);
                MailRequestRepositorySql.AppendTenantScopeFilter(where, update, allowedTenantIds);
                update.CommandText = where.ToString();
                update.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@Id", id.ToString("D"));
                update.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                update.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                {
                    // Invalidate prior-cycle Delivered evidence so reclaim/retry in the new
                    // dispatch cycle cannot skip real send via #238 prior-success (#268).
                    await using (var invalidate = connection.CreateCommand())
                    {
                        invalidate.CommandText = """
                            UPDATE mail_attempts
                            SET error_code = @SupersededErrorCode
                            WHERE request_id = @Id
                              AND status = @DeliveredStatus;
                            """;
                        invalidate.Parameters.AddWithValue(
                            "@SupersededErrorCode",
                            SupersededByManualRetryErrorCode);
                        invalidate.Parameters.AddWithValue("@Id", id.ToString("D"));
                        invalidate.Parameters.AddWithValue(
                            "@DeliveredStatus",
                            (int)MailRequestState.Delivered);
                        await invalidate.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await auditRepository.WriteAsync(
                        auditTemplate with
                        {
                            Result = AdminAuditLog.Results.Success,
                            ErrorCode = null,
                        },
                        connection,
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded);
                }
            }

            var current = await MailRequestRepositorySql.ReadScopedStatusAsync(connection, id, allowedTenantIds, cancellationToken);
            ManualMailRequestMutationStatus status;
            string errorCode;
            if (current is null)
            {
                status = ManualMailRequestMutationStatus.NotFound;
                errorCode = AdminAuditLog.ErrorCodes.NotFound;
            }
            else if (current.Value.AttachmentCount > 0
                && current.Value.Status is MailRequestState.DeadLettered or MailRequestState.Failed)
            {
                status = ManualMailRequestMutationStatus.AttachmentManualRetryNotSupported;
                errorCode = AdminAuditLog.ErrorCodes.AttachmentManualRetryNotSupported;
            }
            else
            {
                status = ManualMailRequestMutationStatus.InvalidState;
                errorCode = AdminAuditLog.ErrorCodes.InvalidState;
            }

            await auditRepository.WriteAsync(
                auditTemplate with
                {
                    Result = AdminAuditLog.Results.Failure,
                    ErrorCode = errorCode,
                },
                connection,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(status);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ManualMailRequestMutationResult> TryManualCancelAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds,
        DateTimeOffset now,
        AdminAuditRepository auditRepository,
        AdminAuditEvent auditTemplate,
        CancellationToken cancellationToken = default)
    {
        if (allowedTenantIds is { Count: 0 })
            return new(ManualMailRequestMutationStatus.NotFound);

        var nowStorage = SqliteTime.ToStorageUtc(now);

        // ADR 0022 D-08 manual cancel boundary: once request-unique submission evidence exists
        // (Started or later), cancel is prohibited outright -- provider invocation may already
        // be underway or complete, and a Cancelled overwrite could race a real send. Requests
        // with no evidence row (including ordinary non-attachment requests, which never get one)
        // keep the existing ADR 0015 first-writer-wins boundary unchanged.
        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @CancelledStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                completed_at = @Now,
                failed_at = @Now,
                last_error_message = @LastErrorMessage,
                updated_at = @Now
            WHERE id = @Id
              AND (
                    status IN (@QueuedStatus, @FailedStatus, @DeadLetteredStatus)
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                    )
                  )
              AND NOT EXISTS (
                    SELECT 1 FROM mail_attachment_submissions s WHERE s.request_id = mail_requests.id
                  )
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                var where = new StringBuilder(updateSql);
                MailRequestRepositorySql.AppendTenantScopeFilter(where, update, allowedTenantIds);
                update.CommandText = where.ToString();
                update.Parameters.AddWithValue("@CancelledStatus", (int)MailRequestState.Cancelled);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@LastErrorMessage", OperatorCancelledLastErrorMessage);
                update.Parameters.AddWithValue("@Id", id.ToString("D"));
                update.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
                update.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
                update.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                {
                    await auditRepository.WriteAsync(
                        auditTemplate with
                        {
                            Result = AdminAuditLog.Results.Success,
                            ErrorCode = null,
                        },
                        connection,
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded);
                }
            }

            var current = await MailRequestRepositorySql.ReadScopedStatusAsync(connection, id, allowedTenantIds, cancellationToken);
            if (current is null)
            {
                await MailRequestRepositorySql.WriteFailureAuditAsync(
                    auditRepository,
                    connection,
                    auditTemplate,
                    AdminAuditLog.ErrorCodes.NotFound,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(ManualMailRequestMutationStatus.NotFound);
            }

            var failureCode = current.Value.Status == MailRequestState.Processing
                && current.Value.LockExpiresAt is not null
                && current.Value.LockExpiresAt > now
                ? AdminAuditLog.ErrorCodes.LockHeld
                : AdminAuditLog.ErrorCodes.InvalidState;

            var failureStatus = failureCode == AdminAuditLog.ErrorCodes.LockHeld
                ? ManualMailRequestMutationStatus.LockHeld
                : ManualMailRequestMutationStatus.InvalidState;

            await MailRequestRepositorySql.WriteFailureAuditAsync(
                auditRepository,
                connection,
                auditTemplate,
                failureCode,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(failureStatus);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ConsumerMailRequestMutationResult> TryConsumerCancelAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @CancelledStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                completed_at = @Now,
                failed_at = @Now,
                last_error_message = @LastErrorMessage,
                updated_at = @Now
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
              AND status = @QueuedStatus
            RETURNING id;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@CancelledStatus", (int)MailRequestState.Cancelled);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@LastErrorMessage", ConsumerCancelledLastErrorMessage);
                update.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
                update.Parameters.AddWithValue("@SourceService", sourceService);
                update.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
                update.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);

                await using var reader = await update.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var internalId = Guid.Parse(reader.GetString(0));
                    await reader.DisposeAsync();
                    var snapshot = await MailRequestRepositorySql.ReadStatusByIdempotencyKeyAsync(
                        connection,
                        tenantId,
                        sourceService,
                        mailRequestId,
                        cancellationToken);
                    if (snapshot is null)
                    {
                        throw new InvalidOperationException(
                            "Cancelled mail request row was not readable after update.");
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded, internalId, snapshot);
                }
            }

            var existing = await MailRequestRepositorySql.ReadStatusWithInternalIdByIdempotencyKeyAsync(
                connection,
                tenantId,
                sourceService,
                mailRequestId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (existing is null)
            {
                return new(ManualMailRequestMutationStatus.NotFound);
            }

            // Already cancelled: idempotent success so callers can converge after a prior
            // commit succeeded but the HTTP response failed (#269).
            if (existing.Value.Status.Status == MailRequestState.Cancelled)
            {
                return new(
                    ManualMailRequestMutationStatus.Succeeded,
                    existing.Value.InternalRequestId,
                    existing.Value.Status);
            }

            return new(ManualMailRequestMutationStatus.InvalidState);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ConsumerMailRequestMutationResult> TryRescheduleAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        DateTimeOffset? scheduledAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);
        var scheduledStorage = scheduledAt is null
            ? null
            : SqliteTime.ToStorageUtc(scheduledAt.Value);

        const string updateSql = """
            UPDATE mail_requests
            SET
                scheduled_at = @ScheduledAt,
                updated_at = @Now
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
              AND status = @QueuedStatus
              AND attempt_count = 0;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@ScheduledAt", (object?)scheduledStorage ?? DBNull.Value);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
                update.Parameters.AddWithValue("@SourceService", sourceService);
                update.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
                update.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                {
                    var snapshot = await MailRequestRepositorySql.ReadStatusByIdempotencyKeyAsync(
                        connection,
                        tenantId,
                        sourceService,
                        mailRequestId,
                        cancellationToken);
                    if (snapshot is null)
                    {
                        throw new InvalidOperationException(
                            "Rescheduled mail request row was not readable after update.");
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded, StatusSnapshot: snapshot);
                }
            }

            var exists = await MailRequestRepositorySql.ExistsByIdempotencyKeyAsync(
                connection,
                tenantId,
                sourceService,
                mailRequestId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(exists
                ? ManualMailRequestMutationStatus.InvalidState
                : ManualMailRequestMutationStatus.NotFound);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
