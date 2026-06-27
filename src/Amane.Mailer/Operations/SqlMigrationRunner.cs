using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Amane.Mailer.Operations;

public sealed class SqlMigrationRunner
{
    private readonly SqliteConnectionFactory _connections;
    private readonly string _migrationDirectory;

    public SqlMigrationRunner(SqliteConnectionFactory connections)
        : this(connections, Path.Combine(AppContext.BaseDirectory, "Data", "Migrations"))
    {
    }

    internal SqlMigrationRunner(SqliteConnectionFactory connections, string migrationDirectory)
    {
        _connections = connections;
        _migrationDirectory = migrationDirectory;
    }

    public async Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_migrationDirectory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {_migrationDirectory}");
        }

        var migrations = await LoadMigrationsAsync(_migrationDirectory, cancellationToken);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaMigrationsTableAsync(connection, migrations, cancellationToken);

        var applied = new List<string>();
        foreach (var migration in migrations)
        {
            var appliedMigration = await GetAppliedMigrationAsync(connection, migration.Version, cancellationToken);
            if (appliedMigration is not null)
            {
                EnsureChecksumMatches(migration, appliedMigration);
                continue;
            }

            await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
            try
            {
                await using (var script = connection.CreateCommand())
                {
                    script.CommandText = migration.Sql;
                    await script.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.CommandText = """
                        INSERT INTO schema_migrations (version, applied_at, checksum)
                        VALUES (@Version, @AppliedAt, @Checksum);
                        """;
                    record.Parameters.AddWithValue("@Version", migration.Version);
                    record.Parameters.AddWithValue("@AppliedAt", SqliteTime.ToStorageUtc(SqliteTime.UtcNow));
                    record.Parameters.AddWithValue("@Checksum", migration.Checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(migration.Version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return applied;
    }

    private static async Task<IReadOnlyList<MigrationFile>> LoadMigrationsAsync(
        string migrationDirectory,
        CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var migrations = new List<MigrationFile>(files.Length);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var sql = await File.ReadAllTextAsync(file, cancellationToken);
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            migrations.Add(new MigrationFile(Path.GetFileName(file), sql, checksum));
        }

        return migrations;
    }

    private static async Task EnsureSchemaMigrationsTableAsync(
        SqliteConnection connection,
        IReadOnlyList<MigrationFile> migrations,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     TEXT NOT NULL PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                checksum    TEXT NOT NULL CHECK (length(checksum) = 64)
            );
            """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await HasChecksumColumnAsync(connection, cancellationToken))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE schema_migrations
                ADD COLUMN checksum TEXT CHECK (checksum IS NULL OR length(checksum) = 64);
                """;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var migration in migrations)
        {
            await using var backfill = connection.CreateCommand();
            backfill.CommandText = """
                UPDATE schema_migrations
                SET checksum = @Checksum
                WHERE version = @Version
                  AND checksum IS NULL;
                """;
            backfill.Parameters.AddWithValue("@Version", migration.Version);
            backfill.Parameters.AddWithValue("@Checksum", migration.Checksum);
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasChecksumColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(schema_migrations);";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "checksum", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<AppliedMigration?> GetAppliedMigrationAsync(
        SqliteConnection connection,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE version = @Version LIMIT 1;";
        command.Parameters.AddWithValue("@Version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var checksum = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new AppliedMigration(version, checksum);
    }

    private static void EnsureChecksumMatches(MigrationFile migration, AppliedMigration appliedMigration)
    {
        if (string.IsNullOrWhiteSpace(appliedMigration.Checksum))
        {
            throw new InvalidOperationException(
                $"Applied database migration '{appliedMigration.Version}' is missing a checksum.");
        }

        if (!string.Equals(migration.Checksum, appliedMigration.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Applied database migration '{migration.Version}' checksum mismatch. "
                + $"Stored checksum is {appliedMigration.Checksum}; current file checksum is {migration.Checksum}. "
                + "Migration SQL files are forward-only and must not be edited after release.");
        }
    }

    private sealed record MigrationFile(string Version, string Sql, string Checksum);

    private sealed record AppliedMigration(string Version, string? Checksum);
}
