using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

public sealed record AppliedSchemaMigration(string Version, DateTimeOffset AppliedAt);

public sealed record MailerDbStorageInfo(
    bool SchemaMigrated,
    bool CanConnect,
    string? DatabaseFileName,
    long? DatabaseFileSizeBytes,
    long? WalFileSizeBytes,
    string? JournalMode,
    IReadOnlyList<AppliedSchemaMigration> AppliedMigrations,
    string? CurrentSchemaVersion);

public sealed class MailerDbStorageInfoReader(SqliteConnectionFactory connections)
{
    public async Task<MailerDbStorageInfo> LoadAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = ResolveDatabasePath();
        var databaseFileName = databasePath is null ? null : Path.GetFileName(databasePath);
        long? databaseFileSize = null;
        long? walFileSize = null;

        if (databasePath is not null && File.Exists(databasePath))
            databaseFileSize = new FileInfo(databasePath).Length;

        if (databasePath is not null)
        {
            var walPath = databasePath + "-wal";
            if (File.Exists(walPath))
                walFileSize = new FileInfo(walPath).Length;
        }

        var schemaMigrated = await CanReadMigratedSchemaAsync(cancellationToken);
        if (!schemaMigrated)
        {
            return new MailerDbStorageInfo(
                false,
                false,
                databaseFileName,
                databaseFileSize,
                walFileSize,
                null,
                [],
                null);
        }

        var canConnect = await connections.CanConnectAsync(cancellationToken);
        string? journalMode = null;
        IReadOnlyList<AppliedSchemaMigration> appliedMigrations = [];

        await using (var connection = await connections.OpenConnectionAsync(cancellationToken))
        {
            journalMode = await ReadJournalModeAsync(connection, cancellationToken);
            appliedMigrations = await ReadAppliedMigrationsAsync(connection, cancellationToken);
        }

        var currentVersion = appliedMigrations.Count == 0
            ? null
            : appliedMigrations[^1].Version;

        return new MailerDbStorageInfo(
            true,
            canConnect,
            databaseFileName,
            databaseFileSize,
            walFileSize,
            journalMode,
            appliedMigrations,
            currentVersion);
    }

    private async Task<bool> CanReadMigratedSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await connections.CanConnectToMigratedSchemaAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveDatabasePath()
    {
        var dataSource = new SqliteConnectionStringBuilder(connections.ConnectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(dataSource);
    }

    private static async Task<string?> ReadJournalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }

    private static async Task<IReadOnlyList<AppliedSchemaMigration>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, applied_at
            FROM schema_migrations
            ORDER BY version;
            """;

        var migrations = new List<AppliedSchemaMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(new AppliedSchemaMigration(
                reader.GetString(0),
                SqliteTime.FromStorage(reader.GetString(1))));
        }

        return migrations;
    }
}
