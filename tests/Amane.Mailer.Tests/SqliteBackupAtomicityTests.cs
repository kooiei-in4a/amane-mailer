using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class SqliteBackupAtomicityTests
{
    [Fact]
    public async Task BackupToAsync_overwrite_replaces_destination_with_current_source()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-backup-overwrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");

        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            await SeedMarkerAsync(databasePath, "v1", ct);

            await factory.BackupToAsync(backupPath, ct);
            Assert.Equal("v1", await ReadMarkerAsync(backupPath, ct));

            await SeedMarkerAsync(databasePath, "v2", ct);
            await factory.BackupToAsync(backupPath, ct);

            Assert.Equal("v2", await ReadMarkerAsync(backupPath, ct));
            Assert.Empty(ListTempBackupArtifacts(backupPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupToAsync_overwrite_invalidates_pooled_readers_of_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-backup-pool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");

        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            await SeedMarkerAsync(databasePath, "v1", ct);
            await factory.BackupToAsync(backupPath, ct);

            // Intentionally pool a handle to the destination, then overwrite. Without
            // InvalidatePooledConnectionsTo before Move, Linux can keep serving v1 and
            // Windows can fail the replace with a sharing violation.
            Assert.Equal("v1", await ReadMarkerAsync(backupPath, ct, pooling: true));

            await SeedMarkerAsync(databasePath, "v2", ct);
            await factory.BackupToAsync(backupPath, ct);

            Assert.Equal("v2", await ReadMarkerAsync(backupPath, ct, pooling: true));
            Assert.Equal("v2", await ReadMarkerAsync(backupPath, ct, pooling: false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupToAsync_cancelled_before_replace_preserves_existing_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-backup-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");

        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            await SeedMarkerAsync(databasePath, "good-backup", ct);
            await factory.BackupToAsync(backupPath, ct);

            await SeedMarkerAsync(databasePath, "should-not-land", ct);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => factory.BackupToAsync(backupPath, cts.Token));

            Assert.Equal("good-backup", await ReadMarkerAsync(backupPath, ct));
            Assert.Empty(ListTempBackupArtifacts(backupPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupToAsync_fault_after_temp_verify_preserves_destination_and_cleans_temp()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-backup-midflight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");

        try
        {
            var factory = CreateFactory(databasePath);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            await SeedMarkerAsync(databasePath, "good-backup", ct);
            await factory.BackupToAsync(backupPath, ct);

            await SeedMarkerAsync(databasePath, "should-not-land", ct);

            var sawTempBeforeReplace = false;
            factory.BeforeAtomicReplaceForTests = _ =>
            {
                Assert.NotEmpty(ListTempBackupArtifacts(backupPath));
                sawTempBeforeReplace = true;
                throw new IOException("injected replace failure");
            };

            try
            {
                var exception = await Assert.ThrowsAsync<IOException>(
                    () => factory.BackupToAsync(backupPath, ct));

                Assert.Equal("injected replace failure", exception.Message);
                Assert.True(sawTempBeforeReplace);
                Assert.Equal("good-backup", await ReadMarkerAsync(backupPath, ct));
                Assert.Empty(ListTempBackupArtifacts(backupPath));
            }
            finally
            {
                factory.BeforeAtomicReplaceForTests = null;
            }
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

    private static IReadOnlyList<string> ListTempBackupArtifacts(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        Assert.False(string.IsNullOrEmpty(directory));
        var prefix = "." + Path.GetFileName(destinationPath) + ".tmp-";
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
    }

    private static async Task SeedMarkerAsync(string databasePath, string marker, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS backup_atomicity_marker (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    marker TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO backup_atomicity_marker (id, marker) VALUES (1, @Marker)
            ON CONFLICT(id) DO UPDATE SET marker = excluded.marker;
            """;
        upsert.Parameters.AddWithValue("@Marker", marker);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadMarkerAsync(
        string databasePath,
        CancellationToken cancellationToken,
        bool pooling = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = pooling,
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT marker FROM backup_atomicity_marker WHERE id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<string>(result);
    }
}
