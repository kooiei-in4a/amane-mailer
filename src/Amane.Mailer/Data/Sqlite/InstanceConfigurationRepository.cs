using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

public sealed class InstanceConfigurationRepository(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider)
{
    public async Task<InstanceConfigurationRow?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, cancellationToken);
    }

    public async Task<bool> ConfigureAcsAsync(
        string secretPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretPath);
        var now = SqliteTime.ToStorageUtc(timeProvider.GetUtcNow());

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var current = await ReadAsync(connection, cancellationToken)
                ?? throw new InvalidOperationException("Instance configuration is unavailable.");

            if (current.InitializedAt is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            if (current.ProviderType is not null
                && (!string.Equals(current.ProviderType, "acs", StringComparison.Ordinal)
                    || !string.Equals(current.ProviderSecretRef, secretPath, StringComparison.Ordinal)))
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE instance_configuration
                SET provider_type = 'acs',
                    provider_secret_ref = @SecretRef,
                    provider_configured_at = COALESCE(provider_configured_at, @ConfiguredAt)
                WHERE id = 1 AND initialized_at IS NULL;
                """;
            update.Parameters.AddWithValue("@SecretRef", secretPath);
            update.Parameters.AddWithValue("@ConfiguredAt", now);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected == 1;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        var now = SqliteTime.ToStorageUtc(timeProvider.GetUtcNow());
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE instance_configuration
                SET initialized_at = @InitializedAt
                WHERE id = 1
                  AND initialized_at IS NULL
                  AND provider_type = 'acs'
                  AND provider_secret_ref IS NOT NULL
                  AND provider_configured_at IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM admin_users
                      WHERE disabled = 0 AND is_instance_owner = 1)
                  AND EXISTS (
                      SELECT 1 FROM senders
                      WHERE enabled = 1);
                """;
            update.Parameters.AddWithValue("@InitializedAt", now);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected == 1;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<InstanceConfigurationRow?> ReadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT initialized_at, live_sending, provider_type,
                   provider_secret_ref, provider_configured_at
            FROM instance_configuration
            WHERE id = 1
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InstanceConfigurationRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt32(1) == 1,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }
}

public sealed record InstanceConfigurationRow(
    string? InitializedAt,
    bool LiveSending,
    string? ProviderType,
    string? ProviderSecretRef,
    string? ProviderConfiguredAt);
