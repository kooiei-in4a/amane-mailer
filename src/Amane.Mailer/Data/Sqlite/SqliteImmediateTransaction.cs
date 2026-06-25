using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Write transactions must use BEGIN IMMEDIATE (not DEFERRED) to avoid SQLITE_BUSY on lock upgrade.
/// </summary>
public sealed class SqliteImmediateTransaction(SqliteConnection connection) : IAsyncDisposable
{
    private bool _completed;

    public static async Task<SqliteImmediateTransaction> BeginAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be open before BEGIN IMMEDIATE.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new SqliteImmediateTransaction(connection);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        await using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await RollbackAsync();
        }
    }
}
