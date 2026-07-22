using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestConsumerMutations(SqliteConnectionFactory connections)
{
    internal const string OperatorCancelledLastErrorMessage = "operator_cancelled";
    internal const string ConsumerCancelledLastErrorMessage = "consumer_cancelled";

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
            var status = current is null
                ? ManualMailRequestMutationStatus.NotFound
                : ManualMailRequestMutationStatus.InvalidState;

            await auditRepository.WriteAsync(
                auditTemplate with
                {
                    Result = AdminAuditLog.Results.Failure,
                    ErrorCode = status == ManualMailRequestMutationStatus.NotFound
                        ? AdminAuditLog.ErrorCodes.NotFound
                        : AdminAuditLog.ErrorCodes.InvalidState,
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
                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded, internalId);
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
                    await transaction.CommitAsync(cancellationToken);
                    return new(ManualMailRequestMutationStatus.Succeeded);
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
