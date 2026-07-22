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
    /// Does not include SQLITE_FULL.
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
