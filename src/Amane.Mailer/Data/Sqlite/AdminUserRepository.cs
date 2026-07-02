using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Persists admin users and tenant scopes for Admin UI authorization (ADR 0014 D-02).
/// </summary>
public sealed class AdminUserRepository(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public async Task EnsureSeedUserAsync(
        string username,
        string passwordHash,
        IEnumerable<Guid> configuredTenantIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var tenantIds = NormalizeTenantIds(configuredTenantIds);
        var now = timeProvider.GetUtcNow();

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            if (await CountUsersAsync(connection, cancellationToken) == 0)
            {
                var userId = await InsertUserAsync(
                    connection,
                    normalizedUsername,
                    passwordHash,
                    isBreakGlass: false,
                    now,
                    cancellationToken);

                await ReplaceTenantScopesAsync(connection, userId, tenantIds, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var existing = await ReadUserByUsernameAsync(connection, normalizedUsername, cancellationToken);
            if (existing is not null
                && !string.Equals(existing.PasswordHash, passwordHash, StringComparison.Ordinal))
            {
                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE admin_users
                    SET password_hash = @PasswordHash,
                        credential_epoch = credential_epoch + 1,
                        updated_at = @UpdatedAt
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@PasswordHash", passwordHash);
                update.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
                update.Parameters.AddWithValue("@Id", existing.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);

                await RevokeActiveSessionsByActorAsync(
                    connection,
                    normalizedUsername,
                    AdminSessionRevokeReasons.CredentialChanged,
                    now,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task EnsureTenantScopeReadyAsync(
        IEnumerable<Guid> configuredTenantIds,
        CancellationToken cancellationToken = default)
    {
        var configuredCount = NormalizeTenantIds(configuredTenantIds).Count;
        var dbTenantCount = await CountDistinctMailRequestTenantsAsync(cancellationToken);
        var effectiveTenantCount = Math.Max(configuredCount, dbTenantCount);
        if (effectiveTenantCount < 2)
            return;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM admin_users u
            WHERE u.disabled = 0
              AND (
                  u.is_break_glass = 1
                  OR EXISTS (
                      SELECT 1
                      FROM admin_user_tenant_scopes s
                      WHERE s.admin_user_id = u.id
                      LIMIT 1
                  )
              )
            LIMIT 1;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException(
                "Admin UI is enabled with multiple tenants, but no enabled admin user has a tenant scope or break-glass access. "
                + "Apply the admin user tenant-scope migration and create scoped admin users before enabling Admin.");
        }
    }

    public async Task<AdminUserRow?> GetActiveUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var user = await ReadUserByUsernameAsync(connection, NormalizeUsername(username), cancellationToken);
        return user is null || user.Disabled ? null : user;
    }

    public async Task<AdminUserRow?> GetActiveUserByIdAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var user = await ReadUserByIdAsync(connection, userId, cancellationToken);
        return user is null || user.Disabled ? null : user;
    }

    public async Task<AdminTenantAccess?> GetTenantAccessAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        var user = await ReadUserByUsernameAsync(connection, NormalizeUsername(username), cancellationToken);
        if (user is null || user.Disabled)
            return null;

        var scopes = await ReadTenantScopesAsync(connection, user.Id, cancellationToken);
        return new AdminTenantAccess(user.Username, user.IsBreakGlass, scopes);
    }

    public async Task<bool> CanRunServiceWideBackupAsync(
        string username,
        IEnumerable<Guid> configuredTenantIds,
        CancellationToken cancellationToken = default)
    {
        var access = await GetTenantAccessAsync(username, cancellationToken);
        if (access is null)
            return false;

        var effectiveTenants = await ListEffectiveTenantIdsAsync(configuredTenantIds, cancellationToken);
        return access.HasAllTenantScopes(effectiveTenants);
    }

    public async Task<long> CreateOrUpdateScopedUserAsync(
        string username,
        string passwordHash,
        IEnumerable<Guid> tenantIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var scopes = NormalizeTenantIds(tenantIds);
        if (scopes.Count == 0)
            throw new ArgumentException("At least one tenant scope is required.", nameof(tenantIds));

        return await UpsertUserAsync(
            normalizedUsername,
            passwordHash,
            scopes,
            isBreakGlass: false,
            cancellationToken);
    }

    public async Task<long> CreateBreakGlassUserAsync(
        string username,
        string passwordHash,
        CancellationToken cancellationToken = default) =>
        await UpsertUserAsync(
            NormalizeUsername(username),
            passwordHash,
            [],
            isBreakGlass: true,
            cancellationToken);

    public async Task ReplaceTenantScopesAsync(
        string username,
        IEnumerable<Guid> tenantIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var scopes = NormalizeTenantIds(tenantIds);
        if (scopes.Count == 0)
            throw new ArgumentException("At least one tenant scope is required.", nameof(tenantIds));

        var now = timeProvider.GetUtcNow();
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var user = await ReadUserByUsernameAsync(connection, normalizedUsername, cancellationToken)
                ?? throw new InvalidOperationException($"Admin user '{normalizedUsername}' does not exist.");

            var currentScopes = await ReadTenantScopesAsync(connection, user.Id, cancellationToken);
            if (currentScopes.SetEquals(scopes))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await ReplaceTenantScopesAsync(connection, user.Id, scopes, cancellationToken);
            await IncrementCredentialEpochAsync(connection, user.Id, now, cancellationToken);
            await RevokeActiveSessionsByActorAsync(
                connection,
                normalizedUsername,
                AdminSessionRevokeReasons.TenantScopeChanged,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> CountDistinctMailRequestTenantsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT tenant_id) FROM mail_requests;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlySet<Guid>> ListEffectiveTenantIdsAsync(
        IEnumerable<Guid> configuredTenantIds,
        CancellationToken cancellationToken = default)
    {
        var tenantIds = NormalizeTenantIds(configuredTenantIds);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT tenant_id FROM mail_requests;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tenantIds.Add(Guid.Parse(reader.GetString(0)));

        return tenantIds;
    }

    private async Task<long> UpsertUserAsync(
        string username,
        string passwordHash,
        IReadOnlyCollection<Guid> tenantIds,
        bool isBreakGlass,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var existing = await ReadUserByUsernameAsync(connection, username, cancellationToken);
            long userId;
            if (existing is null)
            {
                userId = await InsertUserAsync(
                    connection,
                    username,
                    passwordHash,
                    isBreakGlass,
                    now,
                    cancellationToken);
            }
            else
            {
                userId = existing.Id;
                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE admin_users
                    SET password_hash = @PasswordHash,
                        disabled = 0,
                        credential_epoch = credential_epoch + 1,
                        is_break_glass = @IsBreakGlass,
                        updated_at = @UpdatedAt
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@PasswordHash", passwordHash);
                update.Parameters.AddWithValue("@IsBreakGlass", isBreakGlass ? 1 : 0);
                update.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
                update.Parameters.AddWithValue("@Id", existing.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReplaceTenantScopesAsync(connection, userId, tenantIds, cancellationToken);
            await RevokeActiveSessionsByActorAsync(
                connection,
                username,
                isBreakGlass ? AdminSessionRevokeReasons.CredentialChanged : AdminSessionRevokeReasons.TenantScopeChanged,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return userId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<int> CountUsersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admin_users;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertUserAsync(
        SqliteConnection connection,
        string username,
        string passwordHash,
        bool isBreakGlass,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO admin_users (
                username, password_hash, disabled, credential_epoch,
                is_break_glass, created_at, updated_at)
            VALUES (
                @Username, @PasswordHash, 0, 0,
                @IsBreakGlass, @CreatedAt, @UpdatedAt);

            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("@Username", username);
        insert.Parameters.AddWithValue("@PasswordHash", passwordHash);
        insert.Parameters.AddWithValue("@IsBreakGlass", isBreakGlass ? 1 : 0);
        insert.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        insert.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));

        var result = await insert.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ReplaceTenantScopesAsync(
        SqliteConnection connection,
        long userId,
        IEnumerable<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM admin_user_tenant_scopes WHERE admin_user_id = @UserId;";
            delete.Parameters.AddWithValue("@UserId", userId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tenantId in tenantIds)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO admin_user_tenant_scopes (admin_user_id, tenant_id)
                VALUES (@UserId, @TenantId);
                """;
            insert.Parameters.AddWithValue("@UserId", userId);
            insert.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task IncrementCredentialEpochAsync(
        SqliteConnection connection,
        long userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_users
            SET credential_epoch = credential_epoch + 1,
                updated_at = @UpdatedAt
            WHERE id = @UserId;
            """;
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeActiveSessionsByActorAsync(
        SqliteConnection connection,
        string actor,
        string revokeReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET revoked_at = @RevokedAt,
                revoke_reason = @RevokeReason
            WHERE actor = @Actor
              AND revoked_at IS NULL;
            """;
        command.Parameters.AddWithValue("@Actor", actor);
        command.Parameters.AddWithValue("@RevokedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@RevokeReason", revokeReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AdminUserRow?> ReadUserByUsernameAsync(
        SqliteConnection connection,
        string username,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, password_hash, disabled, credential_epoch, is_break_glass
            FROM admin_users
            WHERE username = @Username
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Username", username);
        return await ReadSingleUserAsync(command, cancellationToken);
    }

    private static async Task<AdminUserRow?> ReadUserByIdAsync(
        SqliteConnection connection,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, password_hash, disabled, credential_epoch, is_break_glass
            FROM admin_users
            WHERE id = @UserId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@UserId", userId);
        return await ReadSingleUserAsync(command, cancellationToken);
    }

    private static async Task<AdminUserRow?> ReadSingleUserAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AdminUserRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3) != 0,
            reader.GetInt32(4),
            reader.GetInt32(5) != 0);
    }

    private static async Task<HashSet<Guid>> ReadTenantScopesAsync(
        SqliteConnection connection,
        long userId,
        CancellationToken cancellationToken)
    {
        var tenantIds = new HashSet<Guid>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tenant_id
            FROM admin_user_tenant_scopes
            WHERE admin_user_id = @UserId
            ORDER BY tenant_id;
            """;
        command.Parameters.AddWithValue("@UserId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tenantIds.Add(Guid.Parse(reader.GetString(0)));

        return tenantIds;
    }

    private static HashSet<Guid> NormalizeTenantIds(IEnumerable<Guid> tenantIds) =>
        new(tenantIds);

    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Admin username is required.", nameof(username));

        return username.Trim();
    }
}
