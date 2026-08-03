using System.Data;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Write transactions must use BEGIN IMMEDIATE (not DEFERRED) to avoid SQLITE_BUSY on lock upgrade.
/// Uses ADO.NET <see cref="SqliteConnection.BeginTransaction()"/> so the transaction is tracked and
/// rolled back on connection Close / pool return (raw BEGIN is invisible to the pool reset path).
/// </summary>
public sealed class SqliteImmediateTransaction : IAsyncDisposable
{
    private readonly SqliteTransaction _transaction;
    private bool _completed;

    private SqliteImmediateTransaction(SqliteTransaction transaction)
    {
        _transaction = transaction;
    }

    public static Task<SqliteImmediateTransaction> BeginAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be open before BEGIN IMMEDIATE.");
        }

        // Serializable + non-deferred => BEGIN IMMEDIATE inside Microsoft.Data.Sqlite.
        var transaction = connection.BeginTransaction();
        return Task.FromResult(new SqliteImmediateTransaction(transaction));
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _transaction.Commit();
        _completed = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rolls back the open transaction. Request cancellation is ignored so catch-path rollback
    /// cannot replace the original exception with <see cref="OperationCanceledException"/> (#277).
    /// </summary>
    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        // Keep the parameter for call-site compatibility; never forward a cancelled token.
        _ = cancellationToken;

        if (_completed)
        {
            return Task.CompletedTask;
        }

        // Dispose rolls back when still open; no-ops when Close/external rollback already completed it.
        _transaction.Dispose();
        _completed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_completed)
        {
            return ValueTask.CompletedTask;
        }

        // Dispose rolls back when the transaction is still open and tracked.
        _transaction.Dispose();
        _completed = true;
        return ValueTask.CompletedTask;
    }
}
