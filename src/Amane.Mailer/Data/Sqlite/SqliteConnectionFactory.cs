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
        try
        {
            ConnectionCreatedForTests?.Invoke(connection);
            await connection.OpenAsync(cancellationToken);
            await ApplyPragmasAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            // Ownership stays with the factory until a successful return.
            await DisposeOwnedConnectionAsync(connection);
            throw;
        }
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
        await using var connection = await OpenSchemaProbeConnectionAsync(cancellationToken);
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

    /// <summary>
    /// Opens the configured database without creating a missing file (when using the default
    /// ReadWriteCreate mode). Used by readiness probes that must not create an empty DB.
    /// </summary>
    public async Task<SqliteConnection> OpenSchemaProbeConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateSchemaProbeConnection();
        try
        {
            ConnectionCreatedForTests?.Invoke(connection);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // Ownership stays with the factory until a successful return.
            await DisposeOwnedConnectionAsync(connection);
            throw;
        }
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
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Backup destination must include a directory.", nameof(absoluteDestinationPath));
        }

        Directory.CreateDirectory(directory);

        // Write to a same-directory temp file, verify it opens, then replace the destination.
        // Never delete the existing destination first — a mid-flight failure must leave the
        // previous good backup intact (fixed-path overwrite / CLI reuse).
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(absoluteDestinationPath)}.tmp-{Guid.NewGuid():N}");
        var replaced = false;

        try
        {
            await using (var source = await OpenConnectionAsync(cancellationToken))
            {
                var destinationBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = tempPath,
                    // Windows keeps pooled handles open after dispose; File.Move would fail.
                    Pooling = false,
                };

                await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
                await destination.OpenAsync(cancellationToken);
                await using (var journalMode = destination.CreateCommand())
                {
                    // Avoid leaving temp-wal/temp-shm beside the staging file on Windows.
                    journalMode.CommandText = "PRAGMA journal_mode = DELETE;";
                    await journalMode.ExecuteNonQueryAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                source.BackupDatabase(destination);
            }

            await VerifyBackupFileAsync(tempPath, cancellationToken);
            if (BeforeAtomicReplaceForTests is { } beforeReplace)
            {
                await beforeReplace(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Drop pooled handles to the destination before replace:
            // - Windows: an idle pooled handle keeps the file locked and Move fails.
            // - Linux: rename can succeed while pooled handles keep the previous inode,
            //   so later opens of the same path would read stale content.
            InvalidatePooledConnectionsTo(absoluteDestinationPath);
            File.Move(tempPath, absoluteDestinationPath, overwrite: true);
            replaced = true;
            // Move renames only the main file; drop any leftover temp sidecars.
            TryDeleteBackupArtifacts(tempPath);
        }
        finally
        {
            if (!replaced)
            {
                TryDeleteBackupArtifacts(tempPath);
            }
        }
    }

    /// <summary>
    /// Test-only gate invoked after the temp backup is verified and before atomic replace.
    /// Instance-scoped so parallel tests cannot observe another fixture's injected fault.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeAtomicReplaceForTests { get; set; }

    /// <summary>
    /// Test-only hook invoked immediately after a connection instance is created and before open.
    /// Instance-scoped so parallel tests cannot observe another fixture's capture.
    /// </summary>
    internal Action<SqliteConnection>? ConnectionCreatedForTests { get; set; }

    /// <summary>
    /// Test-only hook invoked after each successful PRAGMA in <see cref="ApplyPragmasAsync"/>.
    /// Instance-scoped so parallel tests cannot observe another fixture's injected fault.
    /// </summary>
    internal Func<string, CancellationToken, Task>? AfterPragmaAppliedForTests { get; set; }

    /// <summary>
    /// Test-only hook invoked after the factory disposes a connection it still owns
    /// (open/PRAGMA failure path). Instance-scoped for parallel test isolation.
    /// </summary>
    internal Action<SqliteConnection>? ConnectionDisposedForTests { get; set; }

    private static async Task VerifyBackupFileAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not long value || value != 1L)
        {
            throw new InvalidOperationException("Backup verification failed.");
        }
    }

    private static void InvalidatePooledConnectionsTo(string absolutePath)
    {
        // Pool keys include the full connection string. Clear common variants so a prior
        // open of the backup path cannot keep the file locked (Windows) or pinned to a
        // replaced inode after rename (Linux). Do not ClearAllPools — backup can run in a
        // live process that still needs the active mailer DB pool.
        var fullPath = Path.GetFullPath(absolutePath);
        SqliteConnectionStringBuilder[] builders =
        [
            new() { DataSource = fullPath },
            new() { DataSource = fullPath, Pooling = true },
            new() { DataSource = fullPath, Pooling = false },
            new() { DataSource = fullPath, Mode = SqliteOpenMode.ReadOnly },
            new() { DataSource = fullPath, Mode = SqliteOpenMode.ReadOnly, Pooling = true },
            new() { DataSource = fullPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false },
        ];
        foreach (var builder in builders)
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            SqliteConnection.ClearPool(connection);
        }
    }

    private static void TryDeleteBackupArtifacts(string absolutePath)
    {
        TryDeleteFile(absolutePath);
        TryDeleteFile(absolutePath + "-wal");
        TryDeleteFile(absolutePath + "-shm");
        TryDeleteFile(absolutePath + "-journal");
    }

    private static void TryDeleteFile(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of an incomplete temp backup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of an incomplete temp backup.
        }
    }

    public string? GetConfiguredDatabasePath()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(dataSource);
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

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
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
            if (AfterPragmaAppliedForTests is { } afterPragma)
            {
                await afterPragma(pragma, cancellationToken);
            }
        }
    }

    private async Task DisposeOwnedConnectionAsync(SqliteConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch
        {
            // Prefer the original open/PRAGMA exception; do not replace it.
        }

        try
        {
            ConnectionDisposedForTests?.Invoke(connection);
        }
        catch
        {
            // Test hook must not replace the original open/PRAGMA exception.
        }
    }
}
