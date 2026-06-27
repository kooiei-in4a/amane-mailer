using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace Amane.Mailer.Tests;

public sealed class SqlMigrationRunnerChecksumTests
{
    [Fact]
    public async Task Db_migrate_records_checksums_for_new_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var runner = new SqlMigrationRunner(CreateFactory(databasePath));
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.Contains("001_initial.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await HasChecksumColumnAsync(connection, ct));

            var stored = await GetStoredChecksumsAsync(connection, ct);
            var expected = await GetExpectedChecksumsAsync(GetCurrentMigrationDirectory(), ct);

            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), stored.Keys.Order(StringComparer.Ordinal));
            foreach (var (version, checksum) in expected)
            {
                Assert.Equal(checksum, stored[version]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_fails_fast_when_applied_migration_file_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyCurrentMigrations(migrationDirectory);

            var runner = new SqlMigrationRunner(CreateFactory(databasePath), migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            await File.AppendAllTextAsync(
                Path.Combine(migrationDirectory, "002_worker_heartbeats.sql"),
                $"{Environment.NewLine}-- edited after apply{Environment.NewLine}",
                ct);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ApplyPendingAsync(ct));

            Assert.Contains("002_worker_heartbeats.sql", exception.Message, StringComparison.Ordinal);
            Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_backfills_checksums_for_legacy_schema_migrations_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            await CreateLegacyV010DatabaseAsync(databasePath, ct);

            var runner = new SqlMigrationRunner(CreateFactory(databasePath));
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.DoesNotContain("001_initial.sql", applied);
            Assert.DoesNotContain("002_worker_heartbeats.sql", applied);
            Assert.Contains("003_admin_indexes.sql", applied);
            Assert.Contains("004_admin_audit_events.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await HasChecksumColumnAsync(connection, ct));

            var stored = await GetStoredChecksumsAsync(connection, ct);
            var expected = await GetExpectedChecksumsAsync(GetCurrentMigrationDirectory(), ct);

            foreach (var (version, checksum) in expected)
            {
                Assert.Equal(checksum, stored[version]);
            }

            Assert.Empty(await runner.ApplyPendingAsync(ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_fails_fast_when_legacy_backfilled_migration_file_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyCurrentMigrations(migrationDirectory);
            await CreateLegacyV010DatabaseAsync(databasePath, migrationDirectory, ct);

            var runner = new SqlMigrationRunner(CreateFactory(databasePath), migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            await File.AppendAllTextAsync(
                Path.Combine(migrationDirectory, "001_initial.sql"),
                $"{Environment.NewLine}-- edited after legacy backfill{Environment.NewLine}",
                ct);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ApplyPendingAsync(ct));

            Assert.Contains("001_initial.sql", exception.Message, StringComparison.Ordinal);
            Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_fails_fast_when_applied_migration_file_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyCurrentMigrations(migrationDirectory);

            var runner = new SqlMigrationRunner(CreateFactory(databasePath), migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            File.Delete(Path.Combine(migrationDirectory, "002_worker_heartbeats.sql"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ApplyPendingAsync(ct));

            Assert.Contains("002_worker_heartbeats.sql", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not present in the migration directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnectionFactory CreateFactory(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();

        return new SqliteConnectionFactory(configuration);
    }

    private static Task CreateLegacyV010DatabaseAsync(string databasePath, CancellationToken cancellationToken) =>
        CreateLegacyV010DatabaseAsync(databasePath, GetCurrentMigrationDirectory(), cancellationToken);

    private static async Task CreateLegacyV010DatabaseAsync(
        string databasePath,
        string migrationDirectory,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE schema_migrations (
                    version     TEXT NOT NULL PRIMARY KEY,
                    applied_at  TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteSqlFileAsync(connection, migrationDirectory, "001_initial.sql", cancellationToken);
        await InsertLegacyMigrationRecordAsync(connection, "001_initial.sql", cancellationToken);
        await ExecuteSqlFileAsync(connection, migrationDirectory, "002_worker_heartbeats.sql", cancellationToken);
        await InsertLegacyMigrationRecordAsync(connection, "002_worker_heartbeats.sql", cancellationToken);
    }

    private static async Task ExecuteSqlFileAsync(
        SqliteConnection connection,
        string migrationDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var sql = await File.ReadAllTextAsync(
            Path.Combine(migrationDirectory, fileName),
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLegacyMigrationRecordAsync(
        SqliteConnection connection,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO schema_migrations (version, applied_at)
            VALUES (@Version, @AppliedAt);
            """;
        command.Parameters.AddWithValue("@Version", version);
        command.Parameters.AddWithValue("@AppliedAt", SqliteTime.ToStorageUtc(SqliteTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<IReadOnlyDictionary<string, string>> GetStoredChecksumsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            checksums.Add(reader.GetString(0), reader.GetString(1));
        }

        return checksums;
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetExpectedChecksumsAsync(
        string migrationDirectory,
        CancellationToken cancellationToken)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = Directory.GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            checksums.Add(Path.GetFileName(file), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        return checksums;
    }

    private static void CopyCurrentMigrations(string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(GetCurrentMigrationDirectory(), "*.sql", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static string GetCurrentMigrationDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
}
