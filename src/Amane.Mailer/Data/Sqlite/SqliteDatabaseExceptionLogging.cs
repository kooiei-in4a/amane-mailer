namespace Amane.Mailer.Data.Sqlite;

internal static class SqliteDatabaseExceptionLogging
{
    internal static void LogError(
        ILogger logger,
        Exception exception,
        string storageFullMessage,
        string otherMessage)
    {
        if (SqliteDatabaseExceptionClassifier.IsStorageFull(exception))
        {
            logger.LogError(exception, storageFullMessage);
            return;
        }

        logger.LogError(exception, otherMessage);
    }

    internal static void LogWarning(
        ILogger logger,
        Exception exception,
        string storageFullMessage,
        string otherMessage)
    {
        if (SqliteDatabaseExceptionClassifier.IsStorageFull(exception))
        {
            logger.LogWarning(exception, storageFullMessage);
            return;
        }

        logger.LogWarning(exception, otherMessage);
    }
}
