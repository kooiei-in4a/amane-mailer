using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Configuration;

public enum InstanceRuntimeStateKind
{
    Unknown = 0,
    Uninitialized = 1,
    Initialized = 2,
}

/// <summary>
/// The startup snapshot of the singleton instance gate. The state is deliberately small:
/// setup progress is derived from durable product records rather than a workflow state table.
/// </summary>
public sealed record InstanceRuntimeState(
    InstanceRuntimeStateKind Kind,
    string? InitializedAt,
    bool LiveSending,
    string? ProviderType,
    string? ProviderSecretRef,
    string? ProviderConfiguredAt,
    bool HasInstanceOwner)
{
    public bool IsUninitialized => Kind == InstanceRuntimeStateKind.Uninitialized;

    public bool IsInitialized => Kind == InstanceRuntimeStateKind.Initialized;

    public static InstanceRuntimeState Unknown { get; } = new(
        InstanceRuntimeStateKind.Unknown,
        null,
        false,
        null,
        null,
        null,
        false);
}

public static class InstanceRuntimeStateProbe
{
    public static async Task<InstanceRuntimeState> ReadAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var factory = new SqliteConnectionFactory(configuration);
        try
        {
            await using var connection = await factory.OpenSchemaProbeConnectionAsync(cancellationToken);
            if (!await HasTableAsync(connection, "instance_configuration", cancellationToken))
            {
                return InstanceRuntimeState.Unknown;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT initialized_at, live_sending, provider_type,
                       provider_secret_ref, provider_configured_at
                FROM instance_configuration
                WHERE id = 1
                LIMIT 1;
                """;

            string? initializedAt;
            bool liveSending;
            string? providerType;
            string? providerSecretRef;
            string? providerConfiguredAt;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return InstanceRuntimeState.Unknown;
                }

                initializedAt = reader.IsDBNull(0) ? null : reader.GetString(0);
                liveSending = reader.GetInt32(1) == 1;
                providerType = reader.IsDBNull(2) ? null : reader.GetString(2);
                providerSecretRef = reader.IsDBNull(3) ? null : reader.GetString(3);
                providerConfiguredAt = reader.IsDBNull(4) ? null : reader.GetString(4);
            }

            var hasInstanceOwner = initializedAt is not null
                && await HasInstanceOwnerAsync(connection, cancellationToken);

            return new InstanceRuntimeState(
                initializedAt is null
                    ? InstanceRuntimeStateKind.Uninitialized
                    : InstanceRuntimeStateKind.Initialized,
                initializedAt,
                liveSending,
                providerType,
                providerSecretRef,
                providerConfiguredAt,
                hasInstanceOwner);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException)
        {
            return InstanceRuntimeState.Unknown;
        }
        catch (IOException)
        {
            return InstanceRuntimeState.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return InstanceRuntimeState.Unknown;
        }
    }

    private static async Task<bool> HasInstanceOwnerAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM admin_users
                    WHERE disabled = 0
                      AND is_instance_owner = 1);
                """;
            return Convert.ToInt32(
                       await command.ExecuteScalarAsync(cancellationToken),
                       System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            // A database stopped before migration 020 is not an initialized v2 runtime.
            return false;
        }
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = @TableName);
            """;
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(
                   await command.ExecuteScalarAsync(cancellationToken),
                   System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
