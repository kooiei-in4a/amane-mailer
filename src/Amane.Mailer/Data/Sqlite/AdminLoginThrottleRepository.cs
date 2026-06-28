using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists admin login throttle state in SQLite (ADR 0014 D-03).
/// </summary>
public sealed class AdminLoginThrottleRepository(SqliteConnectionFactory connections)
{
    public async Task<AdminLoginThrottleRow?> GetAsync(
        string throttleKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
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

    public async Task UpsertAsync(
        AdminLoginThrottleRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_login_throttle (throttle_key, failure_count, locked_until, updated_at)
            VALUES (@ThrottleKey, @FailureCount, @LockedUntil, @UpdatedAt)
            ON CONFLICT(throttle_key) DO UPDATE SET
                failure_count = excluded.failure_count,
                locked_until = excluded.locked_until,
                updated_at = excluded.updated_at;
            """;
        BindParameters(command, row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string throttleKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM admin_login_throttle WHERE throttle_key = @ThrottleKey;";
        command.Parameters.AddWithValue("@ThrottleKey", throttleKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindParameters(SqliteCommand command, AdminLoginThrottleRow row)
    {
        command.Parameters.AddWithValue("@ThrottleKey", row.ThrottleKey);
        command.Parameters.AddWithValue("@FailureCount", row.FailureCount);
        command.Parameters.AddWithValue(
            "@LockedUntil",
            row.LockedUntil is null ? DBNull.Value : SqliteTime.ToStorageUtc(row.LockedUntil.Value));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(row.UpdatedAt));
    }

    private static AdminLoginThrottleRow ReadRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : SqliteTime.FromStorage(reader.GetString(2)),
            SqliteTime.FromStorage(reader.GetString(3)));
}
