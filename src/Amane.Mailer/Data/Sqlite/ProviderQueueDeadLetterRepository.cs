using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Local durable records for poison ACS Storage Queue envelopes (#461).
/// Does not store raw body, recipient, or provider exception text.
/// </summary>
public class ProviderQueueDeadLetterRepository(SqliteConnectionFactory connections)
{
    public const string BodyInvalidErrorCode = "PROVIDER_QUEUE_BODY_INVALID";
    public const string EventInvalidErrorCode = "PROVIDER_QUEUE_EVENT_INVALID";
    public const string FailureStageDecode = "decode";
    public const string FailureStageParse = "parse";

    /// <summary>
    /// Inserts a dead-letter row. UNIQUE(provider, queue_message_id) conflicts return false
    /// without creating a duplicate (Queue delete retry path).
    /// </summary>
    public virtual async Task<bool> TryInsertAsync(
        ProviderQueueDeadLetterInsert row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.DequeueCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row.DequeueCount, "dequeue_count must be >= 0.");
        }

        var nowStorage = SqliteTime.ToStorageUtc(row.CreatedAt);

        const string sql = """
            INSERT INTO provider_queue_dead_letters (
                id, provider, queue_message_id, failure_stage, last_error_code,
                dequeue_count, created_at, updated_at)
            VALUES (
                @Id, @Provider, @QueueMessageId, @FailureStage, @LastErrorCode,
                @DequeueCount, @Now, @Now)
            ON CONFLICT (provider, queue_message_id) DO NOTHING;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@Provider", row.Provider);
        command.Parameters.AddWithValue("@QueueMessageId", row.QueueMessageId);
        command.Parameters.AddWithValue("@FailureStage", row.FailureStage);
        command.Parameters.AddWithValue("@LastErrorCode", row.LastErrorCode);
        command.Parameters.AddWithValue("@DequeueCount", row.DequeueCount);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public virtual async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_queue_dead_letters;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public virtual async Task<int> DeleteExpiredAsync(
        DateTimeOffset createdBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be >= 1.");
        }

        var cutoffStorage = SqliteTime.ToStorageUtc(createdBefore);

        const string sql = """
            DELETE FROM provider_queue_dead_letters
            WHERE id IN (
                SELECT id
                FROM provider_queue_dead_letters
                WHERE created_at < @Cutoff
                ORDER BY created_at ASC
                LIMIT @BatchSize
            );
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Cutoff", cutoffStorage);
        command.Parameters.AddWithValue("@BatchSize", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
