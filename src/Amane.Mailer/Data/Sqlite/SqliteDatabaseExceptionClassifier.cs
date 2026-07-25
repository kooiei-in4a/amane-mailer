using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Classifies SQLite / DB exceptions for HTTP and worker handling.
/// SQLITE_FULL (13) is structural (disk exhaustion) and is not treated as a short-lived transient.
/// </summary>
public static class SqliteDatabaseExceptionClassifier
{
    public const int SqliteBusy = 5;
    public const int SqliteLocked = 6;
    public const int SqliteIoErr = 10;
    public const int SqliteFull = 13;
    public const int SqliteCantOpen = 14;

    /// <summary>
    /// True when the chain contains a <see cref="SqliteException"/>. Callers use this to decide
    /// whether an exception's own text is safe to log: SQLite messages are DB-level and PII-free,
    /// whereas arbitrary exceptions may embed URLs, payload fragments, or provider text (#389).
    /// </summary>
    public static bool IsDatabaseException(Exception exception) =>
        exception is SqliteException
        || (exception.InnerException is not null && IsDatabaseException(exception.InnerException));

    public static bool IsStorageFull(Exception exception)
    {
        if (exception is SqliteException sqlite)
        {
            return sqlite.SqliteErrorCode == SqliteFull;
        }

        return exception.InnerException is not null
            && IsStorageFull(exception.InnerException);
    }

    /// <summary>
    /// Short-lived DB conditions where callers may retry soon (busy/locked/ioerr/cantopen/timeout).
    /// Does not include SQLITE_FULL. If any exception in the chain is SQLITE_FULL, that wins
    /// over an outer TimeoutException / busy wrapper so consumers get STORAGE_FULL.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (IsStorageFull(exception))
        {
            return false;
        }

        if (exception is TimeoutException)
        {
            return true;
        }

        if (exception is SqliteException sqlite)
        {
            return sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked or SqliteIoErr or SqliteCantOpen;
        }

        return exception.InnerException is not null
            && IsTransient(exception.InnerException);
    }
}
