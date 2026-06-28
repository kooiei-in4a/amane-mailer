using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists admin login throttle state in SQLite (ADR 0014 D-03).
/// </summary>
public sealed class AdminLoginThrottleRepository(SqliteConnectionFactory connections)
{
    public sealed record RecordFailureResult(
        int FailureCount,
        DateTimeOffset? LockedUntil,
        bool LockCreated);

    public async Task<AdminLoginThrottleRow?> GetAsync(
        string throttleKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, throttleKey, cancellationToken);
    }

    public async Task<RecordFailureResult> RecordFailureAsync(
        string throttleKey,
        int failureLimit,
        TimeSpan cooldown,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var current = await ReadAsync(connection, throttleKey, cancellationToken);

            var count = current?.LockedUntil is not null && current.LockedUntil <= now
                ? 1
                : (current?.FailureCount ?? 0) + 1;
            var wasLocked = current?.LockedUntil is not null && current.LockedUntil > now;
            var lockCreated = false;
            DateTimeOffset? lockedUntil = current?.LockedUntil;

            if (count >= failureLimit)
            {
                if (!wasLocked)
                    lockCreated = true;

                lockedUntil = now + cooldown;
            }
            else if (current?.LockedUntil is not null && current.LockedUntil <= now)
            {
                lockedUntil = null;
            }

            await UpsertAsync(connection, throttleKey, count, lockedUntil, now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new RecordFailureResult(count, lockedUntil, lockCreated);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(string throttleKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_login_throttle WHERE throttle_key = @ThrottleKey;";
        command.Parameters.AddWithValue("@ThrottleKey", throttleKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdminLoginThrottleRow?> ReadAsync(
        SqliteConnection connection,
        string throttleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT throttle_key, failure_count, locked_until, updated_at
            FROM admin_login_throttle
            WHERE throttle_key = @ThrottleKey;
            """;
        command.Parameters.AddWithValue("@ThrottleKey", throttleKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadRow(reader);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        string throttleKey,
        int failureCount,
        DateTimeOffset? lockedUntil,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_login_throttle (throttle_key, failure_count, locked_until, updated_at)
            VALUES (@ThrottleKey, @FailureCount, @LockedUntil, @UpdatedAt)
            ON CONFLICT(throttle_key) DO UPDATE SET
                failure_count = excluded.failure_count,
                locked_until = excluded.locked_until,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("@ThrottleKey", throttleKey);
        command.Parameters.AddWithValue("@FailureCount", failureCount);
        command.Parameters.AddWithValue(
            "@LockedUntil",
            lockedUntil is null ? DBNull.Value : SqliteTime.ToStorageUtc(lockedUntil.Value));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AdminLoginThrottleRow ReadRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : SqliteTime.FromStorage(reader.GetString(2)),
            SqliteTime.FromStorage(reader.GetString(3)));
}
