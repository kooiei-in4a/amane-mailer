using System.Text;
using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Webhooks.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Webhooks;

public sealed class DeliveryEventRepository(SqliteConnectionFactory connections) : IWebhookDeliveryWorkStore
{
    internal const string LeaseExpiredMaxAttemptsErrorCode = "WEBHOOK_LEASE_EXPIRED_MAX_ATTEMPTS";

    public async Task<bool> TryInsertAsync(
        MailDeliveryEventPayload payload,
        int maxAttempts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(payload, MailerContractsJsonContext.Default.MailDeliveryEventPayload);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string sql = """
            INSERT INTO delivery_events (
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, next_attempt_at,
                created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @EventType, @PayloadJson,
                @PendingStatus, 0, @MaxAttempts, NULL,
                @Now, @Now)
            ON CONFLICT (tenant_id, source_service, mail_request_id) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", payload.EventId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", payload.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", payload.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", payload.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@EventType", payload.EventType);
        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
        command.Parameters.AddWithValue("@PendingStatus", (int)DeliveryEventState.Pending);
        command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<DeliveryEventContext?> FindContextByInternalRequestIdAsync(
        Guid internalRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                mr.tenant_id,
                mr.source_service,
                mr.mail_request_id,
                mr.status,
                mr.attempt_count,
                COALESCE((
                    SELECT ma.error_code
                    FROM mail_attempts ma
                    WHERE ma.request_id = mr.id
                      AND IFNULL(ma.error_code, '') <> @SupersededErrorCode
                    ORDER BY ma.id DESC
                    LIMIT 1
                ), '') AS last_error_code,
                COALESCE(mr.completed_at, mr.updated_at) AS occurred_at
            FROM mail_requests mr
            WHERE mr.id = @Id
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", internalRequestId.ToString("D"));
        command.Parameters.AddWithValue(
            "@SupersededErrorCode",
            MailRequestConsumerMutations.SupersededByManualRetryErrorCode);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = (MailRequestState)reader.GetInt32(3);
        var eventType = MapTerminalStatusToEventType(status);
        if (eventType is null)
        {
            return null;
        }

        var lastErrorCode = reader.GetString(5);
        return new DeliveryEventContext(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)),
            eventType,
            reader.GetInt32(4),
            string.IsNullOrEmpty(lastErrorCode) ? null : lastErrorCode,
            SqliteTime.FromStorage(reader.GetString(6)));
    }

    public async Task<IReadOnlyList<Guid>> FindInternalRequestIdsMissingDeliveryEventsAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT mr.id
            FROM mail_requests mr
            WHERE mr.status IN (@DeliveredStatus, @FailedStatus, @DeadLetteredStatus, @CancelledStatus)
              AND NOT EXISTS (
                    SELECT 1
                    FROM delivery_events de
                    WHERE de.tenant_id = mr.tenant_id
                      AND de.source_service = mr.source_service
                      AND de.mail_request_id = mr.mail_request_id
                  )
            ORDER BY COALESCE(mr.completed_at, mr.updated_at) ASC, mr.id ASC
            LIMIT @BatchSize;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@CancelledStatus", (int)MailRequestState.Cancelled);
        command.Parameters.AddWithValue("@BatchSize", Math.Max(1, batchSize));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    public async Task<DeliveryEventRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);
        var lockToken = Guid.NewGuid();
        var lockExpiresAt = now.Add(leaseDuration);
        var lockExpiresStorage = SqliteTime.ToStorageUtc(lockExpiresAt);

        const string selectSql = """
            SELECT id
            FROM delivery_events
            WHERE
                (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                OR (
                    status = @DeliveringStatus
                    AND lock_expires_at IS NOT NULL
                    AND lock_expires_at <= @Now
                    AND attempt_count < max_attempts
                )
            ORDER BY created_at ASC
            LIMIT 1;
            """;

        const string updateSql = """
            UPDATE delivery_events
            SET
                status = @DeliveringStatus,
                attempt_count = attempt_count + 1,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @Now
            WHERE id = @Id
              AND (
                    (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @DeliveringStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                  );
            """;

        const string readSql = """
            SELECT
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, lock_token, lock_expires_at
            FROM delivery_events
            WHERE id = @Id
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            Guid? claimedId = null;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectSql;
                select.Parameters.AddWithValue("@PendingStatus", (int)DeliveryEventState.Pending);
                select.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
                select.Parameters.AddWithValue("@Now", nowStorage);

                var scalar = await select.ExecuteScalarAsync(cancellationToken);
                if (scalar is string idText)
                {
                    claimedId = Guid.Parse(idText);
                }
            }

            if (claimedId is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
                update.Parameters.AddWithValue("@PendingStatus", (int)DeliveryEventState.Pending);
                update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
                update.Parameters.AddWithValue("@LockExpiresAt", lockExpiresStorage);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@Id", claimedId.Value.ToString("D"));

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }
            }

            DeliveryEventRow? row = null;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = readSql;
                read.Parameters.AddWithValue("@Id", claimedId.Value.ToString("D"));
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    row = MapRow(reader);
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

    /// <summary>
    /// Dead-letters Delivering rows whose lease expired at or past max_attempts.
    /// Fencing mirrors mail <c>DeadLetterExpiredProcessingAtMaxAttemptsAsync</c>:
    /// select candidates, then update only when id/status/lock_token/attempt_count/lease
    /// still match inside the same write transaction. Do not enqueue after success —
    /// DeadLettered is terminal for webhook events.
    /// </summary>
    public async Task<IReadOnlyList<ExpiredDeliveringDeadLetteredEvent>> DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string selectSql = """
            SELECT id, tenant_id, mail_request_id, attempt_count, lock_token
            FROM delivery_events
            WHERE status = @DeliveringStatus
              AND lock_token IS NOT NULL
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count >= max_attempts
            ORDER BY lock_expires_at ASC, created_at ASC
            LIMIT @BatchSize;
            """;

        // Fencing: refuse to overwrite a newer claim or non-expired lease.
        const string updateSql = """
            UPDATE delivery_events
            SET
                status = @DeadLetteredStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                last_error_code = @LastErrorCode,
                updated_at = @Now,
                completed_at = @Now
            WHERE id = @Id
              AND status = @DeliveringStatus
              AND lock_token = @LockToken
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count = @AttemptCount
              AND attempt_count >= max_attempts;
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var requestedBatchSize = Math.Max(1, batchSize);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var candidates = new List<(Guid Id, Guid TenantId, Guid MailRequestId, int AttemptCount, Guid LockToken)>();

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectSql;
                select.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
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
                        Guid.Parse(reader.GetString(4))));
                }
            }

            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var deadLettered = new List<ExpiredDeliveringDeadLetteredEvent>(candidates.Count);
            foreach (var candidate in candidates)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@DeadLetteredStatus", (int)DeliveryEventState.DeadLettered);
                update.Parameters.AddWithValue("@LastErrorCode", LeaseExpiredMaxAttemptsErrorCode);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@Id", candidate.Id.ToString("D"));
                update.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
                update.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                update.Parameters.AddWithValue("@AttemptCount", candidate.AttemptCount);

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    continue;
                }

                deadLettered.Add(new ExpiredDeliveringDeadLetteredEvent(
                    candidate.Id,
                    candidate.TenantId,
                    candidate.MailRequestId,
                    candidate.AttemptCount,
                    LeaseExpiredMaxAttemptsErrorCode));
            }

            await transaction.CommitAsync(cancellationToken);
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
        DeliveryEventFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorCode,
        CancellationToken cancellationToken = default)
    {
        var (newStatus, completedAt) = MapOutcome(outcome, now, nextAttemptAt);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string sql = """
            UPDATE delivery_events
            SET
                status = @NewStatus,
                next_attempt_at = @NextAttemptAt,
                lock_token = NULL,
                lock_expires_at = NULL,
                last_error_code = @LastErrorCode,
                updated_at = @Now,
                completed_at = @CompletedAt
            WHERE id = @Id
              AND status = @DeliveringStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@NewStatus", (int)newStatus);
        command.Parameters.AddWithValue(
            "@NextAttemptAt",
            nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
        command.Parameters.AddWithValue("@LastErrorCode", (object?)lastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue(
            "@CompletedAt",
            completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<bool> HasPendingWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM delivery_events
                WHERE
                    (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @DeliveringStatus
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
        command.Parameters.AddWithValue("@PendingStatus", (int)DeliveryEventState.Pending);
        command.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<AdminWebhookDeadLetterListPage> ListDeadLettersForAdminAsync(
        AdminWebhookDeadLetterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.DeadLettered);
        command.Parameters.AddWithValue("@Limit", limit);

        var where = new StringBuilder("""
            WHERE status = @Status
              AND completed_at IS NOT NULL
            """);

        AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (query.CursorCompletedAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (completed_at < @CursorCompletedAt OR (completed_at = @CursorCompletedAt AND id < @CursorId))");
            command.Parameters.AddWithValue("@CursorCompletedAt", SqliteTime.ToStorageUtc(query.CursorCompletedAt.Value));
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id, event_type,
                attempt_count, last_error_code, completed_at
            FROM delivery_events
            {{where}}
            ORDER BY completed_at DESC, id DESC
            LIMIT @Limit;
            """;

        var rows = new List<AdminWebhookDeadLetterListRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminWebhookDeadLetterListRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                SqliteTime.FromStorage(reader.GetString(7))));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminWebhookDeadLetterCursor.Encode(last.CompletedAt, last.EventId);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminWebhookDeadLetterListPage(rows, nextCursor);
    }

    public async Task<int> CountDeadLettersForAdminAsync(
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE status = @Status");
        AppendTenantScopeFilter(where, command, allowedTenantIds);
        command.CommandText = $$"""
            SELECT COUNT(*)
            FROM delivery_events
            {{where}};
            """;
        command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.DeadLettered);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<(long PendingCount, long DeadLetteredCount)> CountOperationalAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN status IN (0, 1) THEN 1 ELSE 0 END) AS pending_count,
                SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END) AS dead_lettered_count
            FROM delivery_events;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }

    private static DeliveryEventRow MapRow(SqliteDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            SourceService = reader.GetString(2),
            MailRequestId = Guid.Parse(reader.GetString(3)),
            EventType = reader.GetString(4),
            PayloadJson = reader.GetString(5),
            Status = (DeliveryEventState)reader.GetInt32(6),
            AttemptCount = reader.GetInt32(7),
            MaxAttempts = reader.GetInt32(8),
            LockToken = Guid.Parse(reader.GetString(9)),
            LockExpiresAt = SqliteTime.FromStorage(reader.GetString(10)),
        };

    private static (DeliveryEventState NewStatus, DateTimeOffset? CompletedAt) MapOutcome(
        DeliveryEventFinalizeOutcome outcome,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt) =>
        outcome switch
        {
            DeliveryEventFinalizeOutcome.Delivered => (DeliveryEventState.Delivered, now),
            DeliveryEventFinalizeOutcome.RetryScheduled => (DeliveryEventState.Pending, null),
            DeliveryEventFinalizeOutcome.DeadLettered => (DeliveryEventState.DeadLettered, now),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    private static string? MapTerminalStatusToEventType(MailRequestState status) =>
        status switch
        {
            MailRequestState.Delivered => MailDeliveryEventType.Delivered,
            MailRequestState.Failed => MailDeliveryEventType.Failed,
            MailRequestState.DeadLettered => MailDeliveryEventType.DeadLettered,
            MailRequestState.Cancelled => MailDeliveryEventType.Cancelled,
            _ => null,
        };

    private static void AppendTenantScopeFilter(
        StringBuilder where,
        SqliteCommand command,
        IReadOnlySet<Guid>? allowedTenantIds)
    {
        if (allowedTenantIds is null)
        {
            return;
        }

        if (allowedTenantIds.Count == 0)
        {
            where.AppendLine();
            where.Append("  AND 1 = 0");
            return;
        }

        var index = 0;
        where.AppendLine();
        where.Append("  AND tenant_id IN (");
        foreach (var tenantId in allowedTenantIds)
        {
            if (index > 0)
            {
                where.Append(", ");
            }

            var parameterName = $"@TenantScope{index++}";
            where.Append(parameterName);
            command.Parameters.AddWithValue(parameterName, tenantId.ToString("D"));
        }

        where.Append(')');
    }
}
