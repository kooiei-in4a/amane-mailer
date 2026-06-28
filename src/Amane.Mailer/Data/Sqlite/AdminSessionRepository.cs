using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists admin server-side sessions and credential epoch state (ADR 0014 D-01).
/// </summary>
public sealed class AdminSessionRepository(SqliteConnectionFactory connections)
{
    public async Task<AdminConfigRow> GetOrInitializeConfigAsync(
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var existing = await ReadConfigAsync(connection, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO admin_config (id, applied_password_hash, credential_epoch)
                VALUES (1, @AppliedPasswordHash, 0);
                """;
            insert.Parameters.AddWithValue("@AppliedPasswordHash", passwordHash);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AdminConfigRow(passwordHash, 0);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<AdminConfigRow> RotateCredentialAsync(
        string passwordHash,
        int newEpoch,
        string revokeReason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE admin_config
                    SET applied_password_hash = @AppliedPasswordHash,
                        credential_epoch = @CredentialEpoch
                    WHERE id = 1;
                    """;
                update.Parameters.AddWithValue("@AppliedPasswordHash", passwordHash);
                update.Parameters.AddWithValue("@CredentialEpoch", newEpoch);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await RevokeAllActiveSessionsAsync(connection, revokeReason, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new AdminConfigRow(passwordHash, newEpoch);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<string> CreateSessionAsync(
        AdminSessionRow session,
        int maxConcurrentSessions,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await EnforceConcurrentSessionLimitAsync(
                connection,
                session.Actor,
                maxConcurrentSessions,
                cancellationToken);

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO admin_sessions (
                    session_id, actor, issued_at, last_seen_at,
                    absolute_expires_at, idle_expires_at,
                    revoked_at, revoke_reason, credential_epoch)
                VALUES (
                    @SessionId, @Actor, @IssuedAt, @LastSeenAt,
                    @AbsoluteExpiresAt, @IdleExpiresAt,
                    NULL, NULL, @CredentialEpoch);
                """;
            BindSessionParameters(insert, session);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return session.SessionId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<AdminSessionRow?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await ReadSessionAsync(connection, sessionId, cancellationToken);
    }

    public async Task UpdateLastSeenAsync(
        string sessionId,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET last_seen_at = @LastSeenAt,
                idle_expires_at = @IdleExpiresAt
            WHERE session_id = @SessionId
              AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@LastSeenAt", SqliteTime.ToStorageUtc(lastSeenAt));
        command.Parameters.AddWithValue("@IdleExpiresAt", SqliteTime.ToStorageUtc(idleExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeSessionAsync(
        string sessionId,
        string revokeReason,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET revoked_at = @RevokedAt,
                revoke_reason = @RevokeReason
            WHERE session_id = @SessionId
              AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@RevokedAt", SqliteTime.ToStorageUtc(revokedAt));
        command.Parameters.AddWithValue("@RevokeReason", revokeReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdminConfigRow?> ReadConfigAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT applied_password_hash, credential_epoch
            FROM admin_config
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AdminConfigRow(reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<AdminSessionRow?> ReadSessionAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                session_id, actor, issued_at, last_seen_at,
                absolute_expires_at, idle_expires_at,
                revoked_at, revoke_reason, credential_epoch
            FROM admin_sessions
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadSessionRow(reader);
    }

    private static async Task EnforceConcurrentSessionLimitAsync(
        SqliteConnection connection,
        string actor,
        int maxConcurrentSessions,
        CancellationToken cancellationToken)
    {
        if (maxConcurrentSessions <= 0)
            return;

        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT session_id
            FROM admin_sessions
            WHERE actor = @Actor
              AND revoked_at IS NULL
              AND absolute_expires_at > @Now
              AND idle_expires_at > @Now
            ORDER BY issued_at ASC;
            """;
        select.Parameters.AddWithValue("@Actor", actor);
        select.Parameters.AddWithValue("@Now", now);

        var activeSessionIds = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                activeSessionIds.Add(reader.GetString(0));
            }
        }

        var excess = activeSessionIds.Count - maxConcurrentSessions + 1;
        if (excess <= 0)
            return;

        for (var index = 0; index < excess; index++)
        {
            await using var revoke = connection.CreateCommand();
            revoke.CommandText = """
                UPDATE admin_sessions
                SET revoked_at = @RevokedAt,
                    revoke_reason = @RevokeReason
                WHERE session_id = @SessionId
                  AND revoked_at IS NULL;
                """;
            revoke.Parameters.AddWithValue("@SessionId", activeSessionIds[index]);
            revoke.Parameters.AddWithValue("@RevokedAt", now);
            revoke.Parameters.AddWithValue("@RevokeReason", AdminSessionRevokeReasons.ConcurrentLimit);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RevokeAllActiveSessionsAsync(
        SqliteConnection connection,
        string revokeReason,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET revoked_at = @RevokedAt,
                revoke_reason = @RevokeReason
            WHERE revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("@RevokedAt", now);
        command.Parameters.AddWithValue("@RevokeReason", revokeReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindSessionParameters(SqliteCommand command, AdminSessionRow session)
    {
        command.Parameters.AddWithValue("@SessionId", session.SessionId);
        command.Parameters.AddWithValue("@Actor", session.Actor);
        command.Parameters.AddWithValue("@IssuedAt", SqliteTime.ToStorageUtc(session.IssuedAt));
        command.Parameters.AddWithValue("@LastSeenAt", SqliteTime.ToStorageUtc(session.LastSeenAt));
        command.Parameters.AddWithValue("@AbsoluteExpiresAt", SqliteTime.ToStorageUtc(session.AbsoluteExpiresAt));
        command.Parameters.AddWithValue("@IdleExpiresAt", SqliteTime.ToStorageUtc(session.IdleExpiresAt));
        command.Parameters.AddWithValue("@CredentialEpoch", session.CredentialEpoch);
    }

    private static AdminSessionRow ReadSessionRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            SqliteTime.FromStorage(reader.GetString(2)),
            SqliteTime.FromStorage(reader.GetString(3)),
            SqliteTime.FromStorage(reader.GetString(4)),
            SqliteTime.FromStorage(reader.GetString(5)),
            reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8));
}

public static class AdminSessionRevokeReasons
{
    public const string Logout = "logout";
    public const string AbsoluteExpired = "absolute_expired";
    public const string IdleExpired = "idle_expired";
    public const string CredentialChanged = "credential_changed";
    public const string TenantScopeChanged = "tenant_scope_changed";
    public const string ConcurrentLimit = "concurrent_limit";
    public const string Invalid = "invalid";
}
