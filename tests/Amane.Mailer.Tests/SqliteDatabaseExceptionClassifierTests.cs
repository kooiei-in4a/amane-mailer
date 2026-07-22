using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests;

public sealed class SqliteDatabaseExceptionClassifierTests
{
    [Theory]
    [InlineData(SqliteDatabaseExceptionClassifier.SqliteBusy)]
    [InlineData(SqliteDatabaseExceptionClassifier.SqliteLocked)]
    [InlineData(SqliteDatabaseExceptionClassifier.SqliteIoErr)]
    [InlineData(SqliteDatabaseExceptionClassifier.SqliteCantOpen)]
    public void Transient_sqlite_codes_are_transient_not_storage_full(int sqliteErrorCode)
    {
        var exception = new SqliteException("transient", sqliteErrorCode);

        Assert.True(SqliteDatabaseExceptionClassifier.IsTransient(exception));
        Assert.False(SqliteDatabaseExceptionClassifier.IsStorageFull(exception));
    }

    [Fact]
    public void Sqlite_full_is_storage_full_not_transient()
    {
        var exception = new SqliteException("database or disk is full", SqliteDatabaseExceptionClassifier.SqliteFull);

        Assert.True(SqliteDatabaseExceptionClassifier.IsStorageFull(exception));
        Assert.False(SqliteDatabaseExceptionClassifier.IsTransient(exception));
    }

    [Fact]
    public void Timeout_is_transient_not_storage_full()
    {
        var exception = new TimeoutException("db timeout");

        Assert.True(SqliteDatabaseExceptionClassifier.IsTransient(exception));
        Assert.False(SqliteDatabaseExceptionClassifier.IsStorageFull(exception));
    }

    [Fact]
    public void Nested_sqlite_full_is_detected()
    {
        var exception = new InvalidOperationException(
            "wrap",
            new SqliteException("database or disk is full", SqliteDatabaseExceptionClassifier.SqliteFull));

        Assert.True(SqliteDatabaseExceptionClassifier.IsStorageFull(exception));
        Assert.False(SqliteDatabaseExceptionClassifier.IsTransient(exception));
    }

    [Fact]
    public void Nested_busy_is_detected_as_transient()
    {
        var exception = new InvalidOperationException(
            "wrap",
            new SqliteException("database is locked", SqliteDatabaseExceptionClassifier.SqliteBusy));

        Assert.True(SqliteDatabaseExceptionClassifier.IsTransient(exception));
        Assert.False(SqliteDatabaseExceptionClassifier.IsStorageFull(exception));
    }
}
