using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests;

public sealed class SqliteImmediateTransactionTests
{
    [Fact]
    public async Task RollbackAsync_succeeds_when_the_request_token_is_already_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenMemoryConnectionAsync(ct);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, ct);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t(x) VALUES (1);";
            await command.ExecuteNonQueryAsync(ct);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await transaction.RollbackAsync(cts.Token);

        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 't';";
            var count = (long)(await verify.ExecuteScalarAsync(ct))!;
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task Catch_path_preserves_non_cancellation_exception_when_token_is_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenMemoryConnectionAsync(ct);
        var cancelled = new CancellationToken(canceled: true);
        var original = new InvalidOperationException("original-failure");

        async Task ActAsync()
        {
            await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, ct);
            try
            {
                throw original;
            }
            catch
            {
                // Mirrors repository catch paths that pass the request CancellationToken.
                await transaction.RollbackAsync(cancelled);
                throw;
            }
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(ActAsync);
        Assert.Same(original, thrown);
    }

    private static async Task<SqliteConnection> OpenMemoryConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
