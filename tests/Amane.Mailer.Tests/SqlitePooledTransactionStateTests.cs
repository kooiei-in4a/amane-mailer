using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using static SQLitePCL.raw;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #474 — pooled handles must not retain an open SQLite transaction
/// across OpenConnectionAsync / PRAGMA application.
/// </summary>
public sealed class SqlitePooledTransactionStateTests
{
    [Fact]
    public async Task OpenConnectionAsync_recovers_when_pooled_handle_was_left_inside_raw_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-sqlite-pooled-tx",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var factory = CreateFactory(databasePath);

        try
        {
            // Reproduce Microsoft.Data.Sqlite pool behavior: raw BEGIN is not tracked, so Close
            // returns the sqlite3 handle to the pool still inside a transaction. The next open
            // would fail on PRAGMA synchronous without EnsureAutocommitAsync (#474).
            await using (var leaker = new SqliteConnection(connectionString))
            {
                await leaker.OpenAsync(ct);
                await using (var begin = leaker.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE;";
                    await begin.ExecuteNonQueryAsync(ct);
                }

                Assert.Equal(0, sqlite3_get_autocommit(leaker.Handle!));
            }

            await using var connection = await factory.OpenConnectionAsync(ct);

            Assert.NotEqual(0, sqlite3_get_autocommit(connection.Handle!));
            Assert.Equal("wal", await ReadPragmaAsync(connection, "journal_mode", ct));
            Assert.Equal("1", await ReadPragmaAsync(connection, "synchronous", ct));
            Assert.Equal("5000", await ReadPragmaAsync(connection, "busy_timeout", ct));
            Assert.Equal("1", await ReadPragmaAsync(connection, "foreign_keys", ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteImmediateTransaction_exception_path_does_not_poison_pooled_handle()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-sqlite-immediate-poison",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        try
        {
            await using (var connection = await factory.OpenConnectionAsync(ct))
            {
                await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, ct);
                try
                {
                    throw new InvalidOperationException("force-rollback");
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message == "force-rollback")
        {
            // Expected — exercise catch-path rollback then connection dispose / pool return.
        }

        try
        {
            await using var reused = await factory.OpenConnectionAsync(ct);
            Assert.NotEqual(0, sqlite3_get_autocommit(reused.Handle!));
            Assert.Equal("1", await ReadPragmaAsync(reused, "synchronous", ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Connection_close_while_immediate_transaction_open_does_not_poison_pool()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-sqlite-close-mid-tx",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var factory = CreateFactory(databasePath);

        try
        {
            // Abnormal teardown: dispose the connection while the write TX is still open.
            // ADO.NET-tracked BeginTransaction must roll back on Close so the pool stays clean.
            var connection = await factory.OpenConnectionAsync(ct);
            _ = await SqliteImmediateTransaction.BeginAsync(connection, ct);
            Assert.Equal(0, sqlite3_get_autocommit(connection.Handle!));
            await connection.DisposeAsync();

            await using var reused = await factory.OpenConnectionAsync(ct);
            Assert.NotEqual(0, sqlite3_get_autocommit(reused.Handle!));
            Assert.Equal("1", await ReadPragmaAsync(reused, "synchronous", ct));
            Assert.Equal("wal", await ReadPragmaAsync(reused, "journal_mode", ct));
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
