using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Admin;

internal static class AdminBootstrapDatabaseClassification
{
    internal const string Fresh = "fresh";
    internal const string ManagedSameUser = "managed-same-user";
    internal const string ExistingManualOrUnsupported = "existing-manual-or-unsupported";
    internal const string PartialOrInconsistent = "partial-or-inconsistent";
}

internal sealed record AdminBootstrapDatabaseSnapshot(
    string Classification,
    int AdminConfigCount,
    int AdminUserCount,
    int? AdminConfigCredentialEpoch,
    int? AdminUserCredentialEpoch,
    string? Username,
    string? AppliedPasswordHash,
    string? UserPasswordHash,
    string? ScopeFingerprint,
    bool HasAnyAdminSessionRows)
{
    internal SetupAdminDatabaseExpectationState ToExpectationState(bool includeFreshSessionGuard) =>
        new()
        {
            Classification = Classification,
            AdminConfigCount = AdminConfigCount,
            AdminUserCount = AdminUserCount,
            AdminConfigCredentialEpoch = AdminConfigCredentialEpoch,
            AdminUserCredentialEpoch = AdminUserCredentialEpoch,
            ScopeFingerprint = ScopeFingerprint,
            FreshHasAnyAdminSessionRows = includeFreshSessionGuard
                ? HasAnyAdminSessionRows
                : null,
        };
}

/// <summary>
/// Read-only Admin DB classification plus the managed-only guarded startup mutation. The guarded
/// path uses one BEGIN IMMEDIATE transaction and never revokes sessions or rotates credentials.
/// </summary>
internal sealed class AdminBootstrapDatabase(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    internal async Task<AdminBootstrapDatabaseSnapshot> InspectReadOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenSchemaProbeConnectionAsync(cancellationToken);
        return await ReadSnapshotAsync(connection, cancellationToken);
    }

    internal async Task<int> EnsureExpectedStateAsync(
        SetupAdminBootstrapExpectation expectation,
        string effectiveUsername,
        string effectivePasswordHash,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken = default)
    {
        if (!AdminBootstrapOperationId.TryParse(expectation.OperationId, out _))
            throw new InvalidOperationException("Managed Admin bootstrap expectation is invalid.");

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var before = await ReadSnapshotAsync(connection, cancellationToken);
            if (Matches(before, expectation.After))
            {
                EnsureManagedHashesMatch(before, effectiveUsername, effectivePasswordHash);
                await transaction.CommitAsync(cancellationToken);
                return before.AdminConfigCredentialEpoch ?? 0;
            }

            if (!Matches(before, expectation.Before))
                throw new InvalidOperationException("Managed Admin bootstrap database state did not match its operation guard.");

            if (!string.Equals(
                    expectation.Before.Classification,
                    AdminBootstrapDatabaseClassification.Fresh,
                    StringComparison.Ordinal)
                || expectation.Before.FreshHasAnyAdminSessionRows is not false
                || before.HasAnyAdminSessionRows)
            {
                throw new InvalidOperationException("Managed Admin bootstrap database state is not eligible for initialization.");
            }

            await InsertFreshAsync(
                connection,
                effectiveUsername,
                effectivePasswordHash,
                tenantIds,
                cancellationToken);

            var after = await ReadSnapshotAsync(connection, cancellationToken);
            if (!Matches(after, expectation.After))
                throw new InvalidOperationException("Managed Admin bootstrap database postcondition was not met.");

            EnsureManagedHashesMatch(after, effectiveUsername, effectivePasswordHash);
            await transaction.CommitAsync(cancellationToken);
            return after.AdminConfigCredentialEpoch ?? 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal static SetupAdminBootstrapExpectation? LoadCurrentExpectation(IConfiguration configuration)
    {
        var path = configuration["MAILER_SETUP_RECORDED_METADATA_PATH"]
            ?? SetupBundleLayout.ContainerRecordedMetadataPath;
        if (!File.Exists(path))
            return null;

        try
        {
            var recorded = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SetupJsonContext.Default.SetupRecordedMetadata);
            if (recorded is null
                || !SetupBundleLayout.IsSupportedRecordedSchemaVersion(recorded.SchemaVersion)
                || !recorded.AdminBootstrapRequested)
            {
                return null;
            }

            return recorded.AdminBootstrapExpectation
                ?? throw new InvalidOperationException("Managed Admin bootstrap expectation is missing.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Managed Admin bootstrap metadata is invalid.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Managed Admin bootstrap metadata could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Managed Admin bootstrap metadata could not be read.");
        }
    }

    private async Task InsertFreshAsync(
        SqliteConnection connection,
        string username,
        string passwordHash,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = NormalizeUsername(username);
        var now = timeProvider.GetUtcNow();

        await using (var config = connection.CreateCommand())
        {
            config.CommandText = """
                INSERT INTO admin_config (id, applied_password_hash, credential_epoch)
                VALUES (1, @PasswordHash, 0);
                """;
            config.Parameters.AddWithValue("@PasswordHash", passwordHash);
            await config.ExecuteNonQueryAsync(cancellationToken);
        }

        long userId;
        await using (var user = connection.CreateCommand())
        {
            user.CommandText = """
                INSERT INTO admin_users (
                    username, password_hash, disabled, credential_epoch,
                    is_break_glass, created_at, updated_at)
                VALUES (@Username, @PasswordHash, 0, 0, 0, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """;
            user.Parameters.AddWithValue("@Username", normalizedUsername);
            user.Parameters.AddWithValue("@PasswordHash", passwordHash);
            user.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
            user.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
            userId = Convert.ToInt64(
                await user.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var tenantId in tenantIds.Distinct().OrderBy(static id => id))
        {
            await using var scope = connection.CreateCommand();
            scope.CommandText = """
                INSERT INTO admin_user_tenant_scopes (admin_user_id, tenant_id)
                VALUES (@UserId, @TenantId);
                """;
            scope.Parameters.AddWithValue("@UserId", userId);
            scope.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<AdminBootstrapDatabaseSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var requiredTables = await CountRequiredTablesAsync(connection, cancellationToken);
        if (requiredTables != 4)
        {
            return new AdminBootstrapDatabaseSnapshot(
                AdminBootstrapDatabaseClassification.PartialOrInconsistent,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                false);
        }

        var configCount = 0;
        int? configEpoch = null;
        string? appliedPasswordHash = null;
        await using (var config = connection.CreateCommand())
        {
            config.CommandText = "SELECT applied_password_hash, credential_epoch FROM admin_config ORDER BY id;";
            await using var reader = await config.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                configCount++;
                if (configCount == 1)
                {
                    appliedPasswordHash = reader.GetString(0);
                    configEpoch = reader.GetInt32(1);
                }
            }
        }

        var userCount = 0;
        long? userId = null;
        string? username = null;
        string? userPasswordHash = null;
        int? userEpoch = null;
        var unsupportedUser = false;
        await using (var users = connection.CreateCommand())
        {
            users.CommandText = """
                SELECT id, username, password_hash, disabled, credential_epoch, is_break_glass
                FROM admin_users
                ORDER BY id;
                """;
            await using var reader = await users.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                userCount++;
                if (userCount == 1)
                {
                    userId = reader.GetInt64(0);
                    username = reader.GetString(1);
                    userPasswordHash = reader.GetString(2);
                    unsupportedUser = reader.GetInt32(3) != 0 || reader.GetInt32(5) != 0;
                    userEpoch = reader.GetInt32(4);
                }
            }
        }

        var hasSessionRows = await CountRowsAsync(connection, "admin_sessions", cancellationToken) > 0;
        string? scopeFingerprint = null;
        if (userId is not null)
        {
            var scopes = new List<Guid>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tenant_id
                FROM admin_user_tenant_scopes
                WHERE admin_user_id = @UserId
                ORDER BY tenant_id;
                """;
            command.Parameters.AddWithValue("@UserId", userId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Guid.TryParse(reader.GetString(0), out var tenantId))
                {
                    unsupportedUser = true;
                    continue;
                }

                scopes.Add(tenantId);
            }

            scopeFingerprint = AdminBootstrapScopeFingerprint.Compute(scopes);
        }

        var classification = (configCount, userCount) switch
        {
            (0, 0) when !hasSessionRows => AdminBootstrapDatabaseClassification.Fresh,
            (1, 1) when !unsupportedUser => AdminBootstrapDatabaseClassification.ManagedSameUser,
            (0, 0) => AdminBootstrapDatabaseClassification.PartialOrInconsistent,
            (1, 1) => AdminBootstrapDatabaseClassification.ExistingManualOrUnsupported,
            _ => AdminBootstrapDatabaseClassification.PartialOrInconsistent,
        };

        return new AdminBootstrapDatabaseSnapshot(
            classification,
            configCount,
            userCount,
            configEpoch,
            userEpoch,
            username,
            appliedPasswordHash,
            userPasswordHash,
            scopeFingerprint,
            hasSessionRows);
    }

    private static bool Matches(
        AdminBootstrapDatabaseSnapshot actual,
        SetupAdminDatabaseExpectationState expected) =>
        string.Equals(actual.Classification, expected.Classification, StringComparison.Ordinal)
        && actual.AdminConfigCount == expected.AdminConfigCount
        && actual.AdminUserCount == expected.AdminUserCount
        && actual.AdminConfigCredentialEpoch == expected.AdminConfigCredentialEpoch
        && actual.AdminUserCredentialEpoch == expected.AdminUserCredentialEpoch
        && string.Equals(actual.ScopeFingerprint, expected.ScopeFingerprint, StringComparison.Ordinal)
        && (expected.FreshHasAnyAdminSessionRows is null
            || actual.HasAnyAdminSessionRows == expected.FreshHasAnyAdminSessionRows);

    private static void EnsureManagedHashesMatch(
        AdminBootstrapDatabaseSnapshot snapshot,
        string effectiveUsername,
        string effectivePasswordHash)
    {
        if (!string.Equals(snapshot.Username, NormalizeUsername(effectiveUsername), StringComparison.Ordinal)
            || !string.Equals(snapshot.AppliedPasswordHash, effectivePasswordHash, StringComparison.Ordinal)
            || !string.Equals(snapshot.UserPasswordHash, effectivePasswordHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Managed Admin bootstrap credential state did not match.");
        }
    }

    private static async Task<int> CountRequiredTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('admin_config', 'admin_users', 'admin_user_tenant_scopes', 'admin_sessions');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var sql = table switch
        {
            "admin_sessions" => "SELECT COUNT(*) FROM admin_sessions;",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Managed Admin bootstrap username is invalid.");

        return username.Trim();
    }
}

internal static class AdminBootstrapScopeFingerprint
{
    internal static string Compute(IEnumerable<Guid> tenantIds)
    {
        var normalized = tenantIds
            .Select(static id => id.ToString("D").ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, normalized.Length);
        stream.Write(length);
        foreach (var value in normalized)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }
}
