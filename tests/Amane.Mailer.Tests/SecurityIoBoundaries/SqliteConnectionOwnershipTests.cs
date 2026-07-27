using System.Data;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #344 / #355 — SqliteConnection ownership stays with the
/// factory until a successful open/PRAGMA return; failures dispose the connection.
/// </summary>
public sealed class SqliteConnectionOwnershipTests
{
    [Fact]
    public async Task OpenConnectionAsync_open_failure_disposes_connection_and_preserves_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingDirectory = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-sqlite-open-miss",
            Guid.NewGuid().ToString("N"),
            "does-not-exist");
        var databasePath = Path.Combine(missingDirectory, "mailer.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        SqliteConnection? disposed = null;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = connection => disposed = connection;

        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => factory.OpenConnectionAsync(ct));

            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.Equal(ConnectionState.Closed, created.State);
            Assert.False(exception is ObjectDisposedException);
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_pragma_fault_disposes_connection_and_preserves_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-pragma-fault", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        SqliteConnection? disposed = null;
        var applied = 0;
        ConnectionState? stateBeforeFault = null;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = connection => disposed = connection;
        factory.AfterPragmaAppliedForTests = (pragma, _) =>
        {
            applied++;
            if (applied == 1)
            {
                stateBeforeFault = created?.State;
                throw new InvalidOperationException($"injected pragma fault after {pragma}");
            }

            return Task.CompletedTask;
        };

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => factory.OpenConnectionAsync(ct));

            Assert.StartsWith("injected pragma fault after", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, applied);
            Assert.Equal(ConnectionState.Open, stateBeforeFault);
            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.Equal(ConnectionState.Closed, created.State);
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_success_returns_open_connection_with_pragmas_and_does_not_factory_dispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-open-ok", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        var factoryDisposed = false;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = _ => factoryDisposed = true;

        try
        {
            await using var connection = await factory.OpenConnectionAsync(ct);

            Assert.Same(created, connection);
            Assert.False(factoryDisposed);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal("wal", await ReadPragmaAsync(connection, "journal_mode", ct));
            Assert.Equal("1", await ReadPragmaAsync(connection, "synchronous", ct));
            Assert.Equal("5000", await ReadPragmaAsync(connection, "busy_timeout", ct));
            Assert.Equal("1", await ReadPragmaAsync(connection, "foreign_keys", ct));
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenSchemaProbeConnectionAsync_open_failure_disposes_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-probe-miss", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "missing.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        SqliteConnection? disposed = null;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = connection => disposed = connection;

        try
        {
            // Default ReadWriteCreate is rewritten to ReadOnly for file paths, so a missing
            // file fails open without creating an empty database.
            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => factory.OpenSchemaProbeConnectionAsync(ct));

            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.Equal(ConnectionState.Closed, created.State);
            Assert.False(exception is ObjectDisposedException);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenSchemaProbeConnectionAsync_success_returns_open_connection_for_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-probe-ok", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        // Create the file first so ReadOnly probe can open it.
        await using (var bootstrap = new SqliteConnection($"Data Source={databasePath}"))
        {
            await bootstrap.OpenAsync(ct);
        }

        var factory = CreateFactory(databasePath);
        SqliteConnection? created = null;
        var factoryDisposed = false;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = _ => factoryDisposed = true;

        try
        {
            await using var connection = await factory.OpenSchemaProbeConnectionAsync(ct);

            Assert.Same(created, connection);
            Assert.False(factoryDisposed);
            Assert.Equal(ConnectionState.Open, connection.State);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(ct));
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_cancelled_during_pragma_disposes_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-pragma-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        SqliteConnection? disposed = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = connection => disposed = connection;
        factory.AfterPragmaAppliedForTests = (_, _) =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => factory.OpenConnectionAsync(cts.Token));

            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.Equal(ConnectionState.Closed, created.State);
        }
        finally
        {
            ClearTestHooks(factory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_cancelled_before_open_disposes_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sqlite-open-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        SqliteConnection? created = null;
        SqliteConnection? disposed = null;
        factory.ConnectionCreatedForTests = connection => created = connection;
        factory.ConnectionDisposedForTests = connection => disposed = connection;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => factory.OpenConnectionAsync(cts.Token));

            Assert.NotNull(created);
            Assert.Same(created, disposed);
            Assert.Equal(ConnectionState.Closed, created.State);
        }
        finally
        {
            ClearTestHooks(factory);
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

    private static void ClearTestHooks(SqliteConnectionFactory factory)
    {
        factory.ConnectionCreatedForTests = null;
        factory.ConnectionDisposedForTests = null;
        factory.AfterPragmaAppliedForTests = null;
    }

    private static async Task<string> ReadPragmaAsync(
        SqliteConnection connection,
        string pragmaName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragmaName};";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }
}
