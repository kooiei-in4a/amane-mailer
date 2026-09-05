using System.Net.Mail;
using System.Security.Cryptography;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Identity;

public sealed class SenderRepository(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    private const string ApiKeyPrefix = "amk_";
    private const int SecretSizeBytes = 32;
    private const int SecretDigestSizeBytes = 32;
    internal const int ApiKeyLength = 80;

    public async Task<SenderIdentity> CreateAsync(
        string email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var now = timeProvider.GetUtcNow();
        var senderId = Guid.CreateVersion7(now);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO senders (
                sender_id, email, display_name, enabled, created_at, disabled_at)
            VALUES (
                @SenderId, @Email, @DisplayName, 1, @CreatedAt, NULL);
            """;
        command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));
        command.Parameters.AddWithValue("@Email", normalizedEmail);
        command.Parameters.AddWithValue("@DisplayName", (object?)normalizedDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new SenderIdentity(senderId, normalizedEmail, normalizedDisplayName, true, now, null);
    }

    public Task EnableAsync(Guid senderId, CancellationToken cancellationToken = default) =>
        SetEnabledAsync(senderId, enabled: true, cancellationToken);

    public Task DisableAsync(Guid senderId, CancellationToken cancellationToken = default) =>
        SetEnabledAsync(senderId, enabled: false, cancellationToken);

    public async Task<SenderIdentity?> FindAsync(
        Guid senderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sender_id, email, display_name, enabled, created_at, disabled_at
            FROM senders
            WHERE sender_id = @SenderId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSender(reader) : null;
    }

    public async Task<SenderIdentity?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sender_id, email, display_name, enabled, created_at, disabled_at
            FROM senders
            WHERE email = @Email
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Email", normalizedEmail);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSender(reader) : null;
    }

    public async Task<IReadOnlyList<SenderSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.sender_id,
                s.email,
                s.display_name,
                s.enabled,
                s.created_at,
                s.disabled_at,
                COUNT(k.key_id)
            FROM senders s
            LEFT JOIN api_keys k ON k.sender_id = s.sender_id
            GROUP BY s.sender_id, s.email, s.display_name, s.enabled, s.created_at, s.disabled_at
            ORDER BY s.created_at DESC, s.sender_id;
            """;

        var senders = new List<SenderSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            senders.Add(new SenderSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3) == 1,
                SqliteTime.FromStorage(reader.GetString(4)),
                reader.IsDBNull(5) ? null : SqliteTime.FromStorage(reader.GetString(5)),
                Convert.ToInt32(reader.GetInt64(6), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return senders;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM senders;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<CreatedApiKey> CreateApiKeyAsync(
        Guid senderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("API key name is required.", nameof(name));
        }

        var now = timeProvider.GetUtcNow();
        var keyId = Guid.CreateVersion7(now);
        var secretBytes = RandomNumberGenerator.GetBytes(SecretSizeBytes);
        var encodedSecret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var digest = SHA256.HashData(secretBytes);

        try
        {
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO api_keys (
                    key_id, sender_id, name, secret_digest, created_at, revoked_at)
                VALUES (
                    @KeyId, @SenderId, @Name, @SecretDigest, @CreatedAt, NULL);
                """;
            command.Parameters.AddWithValue("@KeyId", keyId.ToString("D"));
            command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));
            command.Parameters.AddWithValue("@Name", name.Trim());
            command.Parameters.Add("@SecretDigest", SqliteType.Blob).Value = digest;
            command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(digest);
        }

        var plaintext = $"{ApiKeyPrefix}{keyId:N}.{encodedSecret}";
        return new CreatedApiKey(keyId, senderId, name.Trim(), plaintext, now);
    }

    public async Task RevokeApiKeyAsync(
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE api_keys
            SET revoked_at = COALESCE(revoked_at, @RevokedAt)
            WHERE key_id = @KeyId;
            """;
        command.Parameters.AddWithValue("@KeyId", keyId.ToString("D"));
        command.Parameters.AddWithValue("@RevokedAt", SqliteTime.ToStorageUtc(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RevokeApiKeyAsync(
        Guid senderId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE api_keys
            SET revoked_at = COALESCE(revoked_at, @RevokedAt)
            WHERE sender_id = @SenderId
              AND key_id = @KeyId;
            """;
        command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));
        command.Parameters.AddWithValue("@KeyId", keyId.ToString("D"));
        command.Parameters.AddWithValue("@RevokedAt", SqliteTime.ToStorageUtc(timeProvider.GetUtcNow()));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ApiKeyMetadata>> ListApiKeysAsync(
        Guid senderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key_id, sender_id, name, created_at, revoked_at
            FROM api_keys
            WHERE sender_id = @SenderId
            ORDER BY created_at DESC, key_id;
            """;
        command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));

        var keys = new List<ApiKeyMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new ApiKeyMetadata(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                SqliteTime.FromStorage(reader.GetString(3)),
                reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4))));
        }

        return keys;
    }

    public async Task<AuthenticatedApiKey?> AuthenticateAsync(
        string? plaintext,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(plaintext, out var keyId, out var secretBytes))
        {
            return null;
        }

        try
        {
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    k.secret_digest,
                    k.revoked_at,
                    s.sender_id,
                    s.email,
                    s.display_name,
                    s.enabled,
                    s.created_at,
                    s.disabled_at
                FROM api_keys k
                JOIN senders s ON s.sender_id = k.sender_id
                WHERE k.key_id = @KeyId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@KeyId", keyId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var expectedDigest = (byte[])reader.GetValue(0);
            var actualDigest = SHA256.HashData(secretBytes);
            var secretMatches = expectedDigest.Length == SecretDigestSizeBytes
                && CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest);
            CryptographicOperations.ZeroMemory(actualDigest);

            var sender = new SenderIdentity(
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                SqliteTime.FromStorage(reader.GetString(6)),
                reader.IsDBNull(7) ? null : SqliteTime.FromStorage(reader.GetString(7)));

            return secretMatches && reader.IsDBNull(1) && sender.Enabled
                ? new AuthenticatedApiKey(keyId, sender)
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private async Task SetEnabledAsync(
        Guid senderId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE senders
            SET enabled = @Enabled,
                disabled_at = CASE WHEN @Enabled = 1 THEN NULL ELSE @Now END
            WHERE sender_id = @SenderId;
            """;
        command.Parameters.AddWithValue("@SenderId", senderId.ToString("D"));
        command.Parameters.AddWithValue("@Enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SenderIdentity ReadSender(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3) == 1,
            SqliteTime.FromStorage(reader.GetString(4)),
            reader.IsDBNull(5) ? null : SqliteTime.FromStorage(reader.GetString(5)));

    internal static string NormalizeEmail(string email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        if (!MailAddress.TryCreate(trimmed, out _))
        {
            throw new ArgumentException("Sender email is invalid.", nameof(email));
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool TryParse(string? plaintext, out Guid keyId, out byte[] secretBytes)
    {
        keyId = default;
        secretBytes = [];
        if (plaintext is null || plaintext.Length != ApiKeyLength || !plaintext.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = plaintext.IndexOf('.', ApiKeyPrefix.Length);
        if (separator != ApiKeyPrefix.Length + 32
            || !Guid.TryParseExact(plaintext.AsSpan(ApiKeyPrefix.Length, 32), "N", out keyId))
        {
            return false;
        }

        var encoded = plaintext[(separator + 1)..].Replace('-', '+').Replace('_', '/');
        try
        {
            secretBytes = Convert.FromBase64String(encoded + "=");
            return secretBytes.Length == SecretSizeBytes;
        }
        catch (FormatException)
        {
            secretBytes = [];
            return false;
        }
    }
}
