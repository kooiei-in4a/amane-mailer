using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestClaimStore(
    SqliteConnectionFactory connections,
    MailerRuntimeMetrics? runtimeMetrics = null)
{
    private readonly MailerRuntimeMetrics? _runtimeMetrics = runtimeMetrics;
    private const string ExpiredProcessingReaperProvider = "lease-reaper";
    private const string ExpiredProcessingMaxAttemptsErrorCode = "PROCESSING_LEASE_EXPIRED_MAX_ATTEMPTS";
    private const string ExpiredProcessingMaxAttemptsErrorMessage =
        "Processing lease expired after the request reached max_attempts.";

    /// <summary>
    /// Queued rows are claimable only when both the first-dispatch schedule and retry backoff are due.
    /// </summary>
    private const string QueuedReadyPredicate = """
        status = @QueuedStatus
        AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
        AND (scheduled_at IS NULL OR scheduled_at <= @Now)
        """;

    public async Task<MailRequestRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            UPDATE mail_requests
            SET
                status = @ProcessingStatus,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                attempt_count = attempt_count + 1,
                updated_at = @Now
            WHERE id = (
                SELECT id
                FROM mail_requests
                WHERE
                    ({QueuedReadyPredicate})
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                ORDER BY created_at ASC
                LIMIT 1
            )
              AND (
                    ({QueuedReadyPredicate})
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                  )
            RETURNING
                id, tenant_id, source_service, mail_request_id,
                subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name,
                attempt_count, max_attempts, lock_token, lock_expires_at, attachment_count;
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var lockExpiresAtStorage = SqliteTime.ToStorageUtc(now.Add(leaseDuration));

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
            command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
            command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            command.Parameters.AddWithValue("@LockExpiresAt", lockExpiresAtStorage);
            command.Parameters.AddWithValue("@Now", nowStorage);

            MailRequestRow? row = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    row = MapClaimedRow(reader);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return row;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ExpiredProcessingDeadLetteredRequest>> DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string selectSql = """
            SELECT id, tenant_id, mail_request_id, attempt_count, lock_token, updated_at
            FROM mail_requests
            WHERE status = @ProcessingStatus
              AND lock_token IS NOT NULL
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count >= max_attempts
            ORDER BY lock_expires_at ASC, created_at ASC
            LIMIT @BatchSize;
            """;

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @DeadLetteredStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                failed_at = @Now,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count = @AttemptNumber
              AND attempt_count >= max_attempts;
            """;

        const string insertAttemptSql = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message, retryable,
                lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @AttemptStatus,
                NULL, @ErrorCode, @ErrorMessage, 1,
                @LockToken, @StartedAt, @CompletedAt);
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var requestedBatchSize = Math.Max(1, batchSize);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var candidates = new List<(Guid Id, Guid TenantId, Guid MailRequestId, int AttemptNumber, Guid LockToken, DateTimeOffset StartedAt)>();

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectSql;
                select.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                select.Parameters.AddWithValue("@Now", nowStorage);
                select.Parameters.AddWithValue("@BatchSize", requestedBatchSize);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidates.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        Guid.Parse(reader.GetString(2)),
                        reader.GetInt32(3),
                        Guid.Parse(reader.GetString(4)),
                        SqliteTime.FromStorage(reader.GetString(5))));
                }
            }

            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var deadLettered = new List<ExpiredProcessingDeadLetteredRequest>(candidates.Count);
            var recordedAttempts = new List<MailAttemptInsert>(candidates.Count);
            foreach (var candidate in candidates)
            {
                await using (var update = connection.CreateCommand())
                {
                    update.CommandText = updateSql;
                    update.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                    update.Parameters.AddWithValue("@Now", nowStorage);
                    update.Parameters.AddWithValue("@LastErrorMessage", ExpiredProcessingMaxAttemptsErrorMessage);
                    update.Parameters.AddWithValue("@Id", candidate.Id.ToString("D"));
                    update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                    update.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                    update.Parameters.AddWithValue("@AttemptNumber", candidate.AttemptNumber);

                    var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                    if (affected == 0)
                    {
                        continue;
                    }
                }

                await using (var insertAttempt = connection.CreateCommand())
                {
                    insertAttempt.CommandText = insertAttemptSql;
                    insertAttempt.Parameters.AddWithValue("@RequestId", candidate.Id.ToString("D"));
                    insertAttempt.Parameters.AddWithValue("@AttemptNumber", candidate.AttemptNumber);
                    insertAttempt.Parameters.AddWithValue("@Provider", ExpiredProcessingReaperProvider);
                    insertAttempt.Parameters.AddWithValue("@AttemptStatus", (int)MailRequestState.DeadLettered);
                    insertAttempt.Parameters.AddWithValue("@ErrorCode", ExpiredProcessingMaxAttemptsErrorCode);
                    insertAttempt.Parameters.AddWithValue("@ErrorMessage", ExpiredProcessingMaxAttemptsErrorMessage);
                    insertAttempt.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                    insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(candidate.StartedAt));
                    insertAttempt.Parameters.AddWithValue("@CompletedAt", nowStorage);
                    await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
                }

                recordedAttempts.Add(new MailAttemptInsert
                {
                    RequestId = candidate.Id,
                    AttemptNumber = candidate.AttemptNumber,
                    Provider = ExpiredProcessingReaperProvider,
                    Status = MailRequestState.DeadLettered,
                    ErrorCode = ExpiredProcessingMaxAttemptsErrorCode,
                    ErrorMessage = ExpiredProcessingMaxAttemptsErrorMessage,
                    Retryable = true,
                    LockToken = candidate.LockToken,
                    StartedAt = candidate.StartedAt,
                    CompletedAt = now,
                });

                deadLettered.Add(new ExpiredProcessingDeadLetteredRequest(
                    candidate.Id,
                    candidate.TenantId,
                    candidate.MailRequestId,
                    candidate.AttemptNumber,
                    ExpiredProcessingMaxAttemptsErrorCode,
                    ExpiredProcessingMaxAttemptsErrorMessage));
            }

            await transaction.CommitAsync(cancellationToken);
            foreach (var attempt in recordedAttempts)
            {
                _runtimeMetrics?.RecordAttemptCompleted(attempt);
            }

            return deadLettered;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> FinalizeAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        MailRequestFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default)
    {
        var (newStatus, completedAt, deliveredAt, failedAt) = MapOutcome(outcome, now, nextAttemptAt);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @NewStatus,
                next_attempt_at = @NextAttemptAt,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @CompletedAt,
                delivered_at = @DeliveredAt,
                failed_at = @FailedAt,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;

        const string insertAttemptSql = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message, retryable,
                lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @AttemptStatus,
                @ProviderMessageId, @ErrorCode, @ErrorMessage, @Retryable,
                @LockToken, @StartedAt, @CompletedAt);
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@NewStatus", (int)newStatus);
                update.Parameters.AddWithValue(
                    "@NextAttemptAt",
                    nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue(
                    "@CompletedAt",
                    completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
                update.Parameters.AddWithValue(
                    "@DeliveredAt",
                    deliveredAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(deliveredAt.Value));
                update.Parameters.AddWithValue(
                    "@FailedAt",
                    failedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(failedAt.Value));
                update.Parameters.AddWithValue("@LastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", id.ToString("D"));
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    // Provider send may already have succeeded under an expired/superseded lock,
                    // or a sweep reaper may have terminalized the row while send was in flight.
                    // Always persist delivered evidence + skip metric; only best-effort complete
                    // to Delivered while the row is still Processing under the same lock (#238).
                    if (attempt.Status == MailRequestState.Delivered)
                    {
                        await InsertMailAttemptAsync(connection, insertAttemptSql, attempt, cancellationToken);
                        var completedUnderLock = false;
                        if (await IsProcessingAsync(connection, id, cancellationToken))
                        {
                            completedUnderLock = await TryMarkDeliveredUnderLockIgnoringExpiryAsync(
                                connection,
                                id,
                                lockToken,
                                nowStorage,
                                cancellationToken);
                        }

                        await transaction.CommitAsync(cancellationToken);
                        _runtimeMetrics?.RecordAttemptCompleted(attempt);
                        _runtimeMetrics?.RecordFinalizeSkipped();
                        return completedUnderLock;
                    }

                    await transaction.CommitAsync(cancellationToken);
                    return false;
                }
            }

            await InsertMailAttemptAsync(connection, insertAttemptSql, attempt, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            _runtimeMetrics?.RecordAttemptCompleted(attempt);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Terminal commit for an attachment request (ADR 0022 D-08): the submission evidence
    /// terminal state, the mail_attempts history row, and the mail_requests terminal state
    /// commit in one SQLite transaction. Fenced on <c>status = Processing AND lock_token =
    /// @LockToken</c> only -- unlike the ordinary <see cref="FinalizeAsync"/>, it deliberately
    /// does not also require <c>lock_expires_at &gt; @Now</c>: once durable submission evidence
    /// exists, that evidence (not the lease timer) is the source of truth for whether this
    /// claim is still allowed to finalize. A losing/expired attempt's fenced updates simply
    /// affect 0 rows once a later reclaim has already converged the row.
    /// </summary>
    public async Task<bool> FinalizeAttachmentSubmissionAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        AttachmentSubmissionState submissionTerminalState,
        string? providerMessageId,
        MailRequestState requestTerminalState,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateRequestSql = """
            UPDATE mail_requests
            SET
                status = @NewStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                delivered_at = @DeliveredAt,
                failed_at = @FailedAt,
                delivery_unknown_at = @DeliveryUnknownAt,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken;
            """;

        const string updateSubmissionSql = """
            UPDATE mail_attachment_submissions
            SET
                submission_state = @SubmissionState,
                provider_message_id = @ProviderMessageId,
                completed_at = @Now,
                updated_at = @Now
            WHERE request_id = @Id
              AND submission_state = @StartedState;
            """;

        const string insertAttemptSql = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message, retryable,
                lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @AttemptStatus,
                @ProviderMessageId, @ErrorCode, @ErrorMessage, @Retryable,
                @LockToken, @StartedAt, @CompletedAt);
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            bool requestUpdated;
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateRequestSql;
                update.Parameters.AddWithValue("@NewStatus", (int)requestTerminalState);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue(
                    "@DeliveredAt",
                    requestTerminalState == MailRequestState.Delivered ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "@FailedAt",
                    requestTerminalState == MailRequestState.Failed ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "@DeliveryUnknownAt",
                    requestTerminalState == MailRequestState.DeliveryUnknown ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue("@LastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", id.ToString("D"));
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

                requestUpdated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            await using (var updateSubmission = connection.CreateCommand())
            {
                updateSubmission.CommandText = updateSubmissionSql;
                updateSubmission.Parameters.AddWithValue("@SubmissionState", (int)submissionTerminalState);
                updateSubmission.Parameters.AddWithValue("@ProviderMessageId", (object?)providerMessageId ?? DBNull.Value);
                updateSubmission.Parameters.AddWithValue("@Now", nowStorage);
                updateSubmission.Parameters.AddWithValue("@Id", id.ToString("D"));
                updateSubmission.Parameters.AddWithValue("@StartedState", (int)AttachmentSubmissionState.Started);
                await updateSubmission.ExecuteNonQueryAsync(cancellationToken);
            }

            if (requestUpdated)
            {
                await InsertMailAttemptAsync(connection, insertAttemptSql, attempt, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            if (requestUpdated)
            {
                _runtimeMetrics?.RecordAttemptCompleted(attempt);
            }

            return requestUpdated;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SuccessfulDeliveryAttempt?> FindSuccessfulDeliveryAttemptAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT attempt_number, provider, provider_message_id
            FROM mail_attempts
            WHERE request_id = @RequestId
              AND status = @DeliveredStatus
              AND IFNULL(error_code, '') <> @SupersededErrorCode
            ORDER BY id ASC
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue(
            "@SupersededErrorCode",
            MailRequestConsumerMutations.SupersededByManualRetryErrorCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SuccessfulDeliveryAttempt(
            AttemptNumber: reader.GetInt32(0),
            Provider: reader.GetString(1),
            ProviderMessageId: reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<bool> TryMarkDeliveredAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @DeliveredStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                delivered_at = @Now,
                failed_at = NULL,
                last_error_message = NULL
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var update = connection.CreateCommand();
            update.CommandText = updateSql;
            update.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
            update.Parameters.AddWithValue("@Now", nowStorage);
            update.Parameters.AddWithValue("@Id", id.ToString("D"));
            update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
            update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> HasDispatchableWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT EXISTS (
                SELECT 1
                FROM mail_requests
                WHERE
                    ({QueuedReadyPredicate})
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                LIMIT 1
            );
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<MailRequestDispatchState?> FindDispatchStateByMailRequestIdAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, status, attempt_count, next_attempt_at, completed_at, last_error_message, lock_token
            FROM mail_requests
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MailRequestDispatchState
        {
            Id = Guid.Parse(reader.GetString(0)),
            Status = (MailRequestState)reader.GetInt32(1),
            AttemptCount = reader.GetInt32(2),
            NextAttemptAt = reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)),
            CompletedAt = reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
            LastErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
            LockToken = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        };
    }

    public async Task<int> CountAttemptsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM mail_attempts
            WHERE request_id = @RequestId;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    public async Task<int> DeleteExpiredCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Select the expired batch once, then delete matching delivery_events,
        // bounce_events, and mail_requests from that fixed set. Two independent
        // ORDER BY ... LIMIT queries can diverge on completed_at ties; a single
        // selection avoids orphans.
        const string selectBatchSql = """
            SELECT id, tenant_id, source_service, mail_request_id
            FROM mail_requests
            WHERE status IN (
                    @DeliveredStatus, @FailedStatus, @DeadLetteredStatus, @CancelledStatus,
                    @DeliveryUnknownStatus)
              AND completed_at IS NOT NULL
              AND completed_at < @CompletedBefore
            ORDER BY completed_at ASC, id ASC
            LIMIT @BatchSize;
            """;

        var effectiveBatchSize = Math.Clamp(batchSize, 1, MailerRetentionOptions.MaxBatchSize);
        var completedBeforeStorage = SqliteTime.ToStorageUtc(completedBefore);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var batch = new List<(string Id, string TenantId, string SourceService, string MailRequestId)>(
                effectiveBatchSize);

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectBatchSql;
                select.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
                select.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
                select.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                select.Parameters.AddWithValue("@CancelledStatus", (int)MailRequestState.Cancelled);
                select.Parameters.AddWithValue("@DeliveryUnknownStatus", (int)MailRequestState.DeliveryUnknown);
                select.Parameters.AddWithValue("@CompletedBefore", completedBeforeStorage);
                select.Parameters.AddWithValue("@BatchSize", effectiveBatchSize);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)));
                }
            }

            if (batch.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            await using (var deleteEvents = connection.CreateCommand())
            {
                var eventTuples = new StringBuilder();
                for (var i = 0; i < batch.Count; i++)
                {
                    if (i > 0)
                    {
                        eventTuples.Append(", ");
                    }

                    eventTuples.Append($"(@TenantId{i}, @SourceService{i}, @MailRequestId{i})");
                    deleteEvents.Parameters.AddWithValue($"@TenantId{i}", batch[i].TenantId);
                    deleteEvents.Parameters.AddWithValue($"@SourceService{i}", batch[i].SourceService);
                    deleteEvents.Parameters.AddWithValue($"@MailRequestId{i}", batch[i].MailRequestId);
                }

                deleteEvents.CommandText = $"""
                    DELETE FROM delivery_events
                    WHERE (tenant_id, source_service, mail_request_id) IN ({eventTuples});
                    """;
                _ = await deleteEvents.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteBounces = connection.CreateCommand())
            {
                var bounceTuples = new StringBuilder();
                for (var i = 0; i < batch.Count; i++)
                {
                    if (i > 0)
                    {
                        bounceTuples.Append(", ");
                    }

                    bounceTuples.Append($"(@BounceTenantId{i}, @BounceSourceService{i}, @BounceMailRequestId{i})");
                    deleteBounces.Parameters.AddWithValue($"@BounceTenantId{i}", batch[i].TenantId);
                    deleteBounces.Parameters.AddWithValue($"@BounceSourceService{i}", batch[i].SourceService);
                    deleteBounces.Parameters.AddWithValue($"@BounceMailRequestId{i}", batch[i].MailRequestId);
                }

                deleteBounces.CommandText = $"""
                    DELETE FROM bounce_events
                    WHERE (tenant_id, source_service, mail_request_id) IN ({bounceTuples});
                    """;
                _ = await deleteBounces.ExecuteNonQueryAsync(cancellationToken);
            }

            int deleted;
            await using (var deleteRequests = connection.CreateCommand())
            {
                var idList = new StringBuilder();
                for (var i = 0; i < batch.Count; i++)
                {
                    if (i > 0)
                    {
                        idList.Append(", ");
                    }

                    idList.Append($"@Id{i}");
                    deleteRequests.Parameters.AddWithValue($"@Id{i}", batch[i].Id);
                }

                deleteRequests.CommandText = $"""
                    DELETE FROM mail_requests
                    WHERE id IN ({idList});
                    """;
                deleted = await deleteRequests.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task InsertMailAttemptAsync(
        SqliteConnection connection,
        string insertAttemptSql,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken)
    {
        await using var insertAttempt = connection.CreateCommand();
        insertAttempt.CommandText = insertAttemptSql;
        insertAttempt.Parameters.AddWithValue("@RequestId", attempt.RequestId.ToString("D"));
        insertAttempt.Parameters.AddWithValue("@AttemptNumber", attempt.AttemptNumber);
        insertAttempt.Parameters.AddWithValue("@Provider", attempt.Provider);
        insertAttempt.Parameters.AddWithValue("@AttemptStatus", (int)attempt.Status);
        insertAttempt.Parameters.AddWithValue("@ProviderMessageId", (object?)attempt.ProviderMessageId ?? DBNull.Value);
        insertAttempt.Parameters.AddWithValue("@ErrorCode", (object?)attempt.ErrorCode ?? DBNull.Value);
        insertAttempt.Parameters.AddWithValue("@ErrorMessage", (object?)attempt.ErrorMessage ?? DBNull.Value);
        insertAttempt.Parameters.AddWithValue("@Retryable", attempt.Retryable ? 1 : 0);
        insertAttempt.Parameters.AddWithValue("@LockToken", attempt.LockToken.ToString("D"));
        insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(attempt.StartedAt));
        insertAttempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(attempt.CompletedAt));
        await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsProcessingAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM mail_requests
            WHERE id = @Id
              AND status = @ProcessingStatus
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    private static async Task<bool> TryMarkDeliveredUnderLockIgnoringExpiryAsync(
        SqliteConnection connection,
        Guid id,
        Guid lockToken,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE mail_requests
            SET
                status = @DeliveredStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                delivered_at = @Now,
                failed_at = NULL,
                last_error_message = NULL
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken;
            """;
        update.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        update.Parameters.AddWithValue("@Now", nowStorage);
        update.Parameters.AddWithValue("@Id", id.ToString("D"));
        update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    private static MailRequestRow MapClaimedRow(SqliteDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            SourceService = reader.GetString(2),
            MailRequestId = Guid.Parse(reader.GetString(3)),
            Subject = reader.GetString(4),
            HtmlBody = reader.IsDBNull(5) ? null : reader.GetString(5),
            TextBody = reader.IsDBNull(6) ? null : reader.GetString(6),
            ReplyTo = reader.IsDBNull(7) ? null : reader.GetString(7),
            RecipientEmail = reader.GetString(8),
            RecipientDisplayName = reader.IsDBNull(9) ? null : reader.GetString(9),
            AttemptCount = reader.GetInt32(10),
            MaxAttempts = reader.GetInt32(11),
            LockToken = Guid.Parse(reader.GetString(12)),
            LockExpiresAt = SqliteTime.FromStorage(reader.GetString(13)),
            AttachmentCount = reader.GetInt32(14),
            Status = MailRequestState.Processing,
        };

    private static (MailRequestState Status, DateTimeOffset? CompletedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? FailedAt)
        MapOutcome(MailRequestFinalizeOutcome outcome, DateTimeOffset now, DateTimeOffset? nextAttemptAt) =>
        outcome switch
        {
            MailRequestFinalizeOutcome.Delivered => (
                MailRequestState.Delivered,
                now,
                now,
                null),
            MailRequestFinalizeOutcome.RetryScheduled => (
                MailRequestState.Queued,
                null,
                null,
                null),
            MailRequestFinalizeOutcome.Failed => (
                MailRequestState.Failed,
                now,
                null,
                now),
            MailRequestFinalizeOutcome.DeadLettered => (
                MailRequestState.DeadLettered,
                now,
                null,
                now),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}
