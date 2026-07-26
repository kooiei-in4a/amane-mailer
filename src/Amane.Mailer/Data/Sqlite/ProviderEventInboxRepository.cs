using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Durable provider-event inbox (ADR 0020 D-01). Lease / retry idiom mirrors
/// <c>delivery_events</c>, including expired-lease terminalization at max_attempts (#388)
/// and claim predicates that avoid idle hot-spin (#402).
/// </summary>
public sealed class ProviderEventInboxRepository(SqliteConnectionFactory connections)
{
    internal const string LeaseExpiredMaxAttemptsErrorCode = "PROVIDER_EVENT_LEASE_EXPIRED_MAX_ATTEMPTS";
    internal const string UnparseableEventErrorCode = "PROVIDER_EVENT_UNPARSEABLE";

    public async Task<bool> TryInsertAsync(
        ProviderEventInboxInsert row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row.MaxAttempts, "max_attempts must be >= 1.");
        }

        var nowStorage = SqliteTime.ToStorageUtc(row.CreatedAt);
        var recipientEmail = row.RecipientEmail is null
            ? null
            : RecipientEmailNormalizer.Normalize(row.RecipientEmail);

        const string sql = """
            INSERT INTO provider_event_inbox (
                id, provider, event_id, provider_message_id, delivery_status, recipient_email,
                status, disposition, attempt_count, max_attempts, next_attempt_at,
                created_at, updated_at)
            VALUES (
                @Id, @Provider, @EventId, @ProviderMessageId, @DeliveryStatus, @RecipientEmail,
                @PendingStatus, NULL, 0, @MaxAttempts, NULL,
                @Now, @Now)
            ON CONFLICT (provider, event_id) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@Provider", row.Provider);
        command.Parameters.AddWithValue("@EventId", row.EventId);
        command.Parameters.AddWithValue("@ProviderMessageId", (object?)row.ProviderMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("@DeliveryStatus", (object?)row.DeliveryStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", (object?)recipientEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@PendingStatus", (int)ProviderEventInboxState.Pending);
        command.Parameters.AddWithValue("@MaxAttempts", row.MaxAttempts);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<ProviderEventInboxRow?> TryClaimOneAsync(
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
            FROM provider_event_inbox
            WHERE
                (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                OR (
                    status = @ProcessingStatus
                    AND lock_expires_at IS NOT NULL
                    AND lock_expires_at <= @Now
                    AND attempt_count < max_attempts
                )
            ORDER BY created_at ASC
            LIMIT 1;
            """;

        const string updateSql = """
            UPDATE provider_event_inbox
            SET
                status = @ProcessingStatus,
                attempt_count = attempt_count + 1,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @Now
            WHERE id = @Id
              AND (
                    (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                  );
            """;

        const string readSql = """
            SELECT
                id, provider, event_id, provider_message_id, delivery_status, recipient_email,
                status, disposition, attempt_count, max_attempts, lock_token, lock_expires_at
            FROM provider_event_inbox
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
                select.Parameters.AddWithValue("@PendingStatus", (int)ProviderEventInboxState.Pending);
                select.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
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
                update.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
                update.Parameters.AddWithValue("@PendingStatus", (int)ProviderEventInboxState.Pending);
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

            ProviderEventInboxRow? row = null;
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
    /// Dead-letters Processing rows whose lease expired at or past max_attempts (#388).
    /// </summary>
    public async Task<IReadOnlyList<ExpiredProcessingDeadLetteredInboxEvent>> DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string selectSql = """
            SELECT id, provider, event_id, attempt_count, lock_token
            FROM provider_event_inbox
            WHERE status = @ProcessingStatus
              AND lock_token IS NOT NULL
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count >= max_attempts
            ORDER BY lock_expires_at ASC, created_at ASC
            LIMIT @BatchSize;
            """;

        const string updateSql = """
            UPDATE provider_event_inbox
            SET
                status = @DeadLetteredStatus,
                disposition = @Disposition,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                last_error_code = @LastErrorCode,
                updated_at = @Now,
                completed_at = @Now
            WHERE id = @Id
              AND status = @ProcessingStatus
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
            var candidates = new List<(Guid Id, string Provider, string EventId, int AttemptCount, Guid LockToken)>();

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectSql;
                select.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
                select.Parameters.AddWithValue("@Now", nowStorage);
                select.Parameters.AddWithValue("@BatchSize", requestedBatchSize);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidates.Add((
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        Guid.Parse(reader.GetString(4))));
                }
            }

            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var deadLettered = new List<ExpiredProcessingDeadLetteredInboxEvent>(candidates.Count);
            foreach (var candidate in candidates)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@DeadLetteredStatus", (int)ProviderEventInboxState.DeadLettered);
                update.Parameters.AddWithValue("@Disposition", ProviderEventInboxDisposition.DeadLettered);
                update.Parameters.AddWithValue("@LastErrorCode", LeaseExpiredMaxAttemptsErrorCode);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@Id", candidate.Id.ToString("D"));
                update.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
                update.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                update.Parameters.AddWithValue("@AttemptCount", candidate.AttemptCount);

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    continue;
                }

                deadLettered.Add(new ExpiredProcessingDeadLetteredInboxEvent(
                    candidate.Id,
                    candidate.Provider,
                    candidate.EventId,
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
        ProviderEventInboxFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorCode,
        CancellationToken cancellationToken = default)
    {
        var (newStatus, disposition, completedAt) = MapOutcome(outcome, now);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string sql = """
            UPDATE provider_event_inbox
            SET
                status = @NewStatus,
                disposition = @Disposition,
                next_attempt_at = @NextAttemptAt,
                lock_token = NULL,
                lock_expires_at = NULL,
                last_error_code = @LastErrorCode,
                updated_at = @Now,
                completed_at = @CompletedAt
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@NewStatus", (int)newStatus);
        command.Parameters.AddWithValue("@Disposition", (object?)disposition ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@NextAttemptAt",
            nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
        command.Parameters.AddWithValue("@LastErrorCode", (object?)lastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue(
            "@CompletedAt",
            completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<(long PendingCount, long DeadLetteredCount)> CountOperationalAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                SUM(CASE WHEN status IN (0, 1) THEN 1 ELSE 0 END) AS pending_count,
                SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END) AS dead_lettered_count
            FROM provider_event_inbox;
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
    public async Task<bool> HasPendingWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM provider_event_inbox
                WHERE
                    (status = @PendingStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
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
        command.Parameters.AddWithValue("@PendingStatus", (int)ProviderEventInboxState.Pending);
        command.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<int> DeleteExpiredTerminalAsync(
        DateTimeOffset completedBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM provider_event_inbox
            WHERE id IN (
                SELECT id
                FROM provider_event_inbox
                WHERE status IN (@ProcessedStatus, @DeadLetteredStatus)
                  AND completed_at IS NOT NULL
                  AND completed_at < @CompletedBefore
                ORDER BY completed_at ASC, id ASC
                LIMIT @BatchSize
            );
            """;

        var effectiveBatchSize = Math.Clamp(batchSize, 1, MailerRetentionOptions.MaxBatchSize);
        var completedBeforeStorage = SqliteTime.ToStorageUtc(completedBefore);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@ProcessedStatus", (int)ProviderEventInboxState.Processed);
        command.Parameters.AddWithValue("@DeadLetteredStatus", (int)ProviderEventInboxState.DeadLettered);
        command.Parameters.AddWithValue("@CompletedBefore", completedBeforeStorage);
        command.Parameters.AddWithValue("@BatchSize", effectiveBatchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderEventInboxRow MapRow(SqliteDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            Provider = reader.GetString(1),
            EventId = reader.GetString(2),
            ProviderMessageId = reader.IsDBNull(3) ? null : reader.GetString(3),
            DeliveryStatus = reader.IsDBNull(4) ? null : reader.GetString(4),
            RecipientEmail = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = (ProviderEventInboxState)reader.GetInt32(6),
            Disposition = reader.IsDBNull(7) ? null : reader.GetString(7),
            AttemptCount = reader.GetInt32(8),
            MaxAttempts = reader.GetInt32(9),
            LockToken = Guid.Parse(reader.GetString(10)),
            LockExpiresAt = SqliteTime.FromStorage(reader.GetString(11)),
        };

    private static (ProviderEventInboxState NewStatus, string? Disposition, DateTimeOffset? CompletedAt) MapOutcome(
        ProviderEventInboxFinalizeOutcome outcome,
        DateTimeOffset now) =>
        outcome switch
        {
            ProviderEventInboxFinalizeOutcome.Processed =>
                (ProviderEventInboxState.Processed, ProviderEventInboxDisposition.Processed, now),
            ProviderEventInboxFinalizeOutcome.Discarded =>
                (ProviderEventInboxState.Processed, ProviderEventInboxDisposition.Discarded, now),
            ProviderEventInboxFinalizeOutcome.RetryScheduled =>
                (ProviderEventInboxState.Pending, null, null),
            ProviderEventInboxFinalizeOutcome.DeadLettered =>
                (ProviderEventInboxState.DeadLettered, ProviderEventInboxDisposition.DeadLettered, now),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}
