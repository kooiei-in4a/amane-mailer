using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

public sealed class SqliteConnectionFactory(IConfiguration configuration)
{
    public const string DefaultConnectionString = "Data Source=/app/data/mailer.db";

    private readonly string _connectionString =
        configuration.GetConnectionString("Mailer") ?? DefaultConnectionString;

    public string ConnectionString => _connectionString;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ApplyPragmasAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<bool> CanConnectToMigratedSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateSchemaProbeConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('schema_migrations', 'mail_requests', 'mail_attempts', 'worker_heartbeats');
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long tableCount && tableCount == 4L;
    }

    public async Task RunWalCheckpointTruncateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task BackupToAsync(string absoluteDestinationPath, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(absoluteDestinationPath))
        {
            throw new ArgumentException("Backup destination must be an absolute path.", nameof(absoluteDestinationPath));
        }

        if (IsConfiguredDatabasePath(absoluteDestinationPath))
        {
            throw new InvalidOperationException("Backup destination must not be the active mailer database.");
        }

        var directory = Path.GetDirectoryName(absoluteDestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(absoluteDestinationPath))
        {
            File.Delete(absoluteDestinationPath);
        }

        await using var source = await OpenConnectionAsync(cancellationToken);
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = absoluteDestinationPath,
        };

        await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    public bool IsConfiguredDatabasePath(string absolutePath)
    {
        if (!Path.IsPathRooted(absolutePath))
        {
            return false;
        }

        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(dataSource),
            Path.GetFullPath(absolutePath),
            comparison);
    }

    private SqliteConnection CreateSchemaProbeConnection()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (ShouldRequireExistingDatabase(builder))
        {
            builder.Mode = SqliteOpenMode.ReadWrite;
        }

        return new SqliteConnection(builder.ConnectionString);
    }

    private static bool ShouldRequireExistingDatabase(SqliteConnectionStringBuilder builder) =>
        builder.Mode == SqliteOpenMode.ReadWriteCreate
        && !string.IsNullOrWhiteSpace(builder.DataSource)
        && !string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);

    private static async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] pragmas =
        [
            "PRAGMA journal_mode = WAL;",
            "PRAGMA synchronous = NORMAL;",
            "PRAGMA busy_timeout = 5000;",
            "PRAGMA foreign_keys = ON;",
        ];

        foreach (var pragma in pragmas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
