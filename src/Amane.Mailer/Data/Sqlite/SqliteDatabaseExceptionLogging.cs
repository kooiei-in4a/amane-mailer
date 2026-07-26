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

    /// <summary>
    /// Same SQLITE_FULL split as <see cref="LogError(ILogger, Exception, string, string)"/>,
    /// with structured message templates (callers must keep args PII-free).
    /// </summary>
    internal static void LogError(
        ILogger logger,
        Exception exception,
        string storageFullMessageTemplate,
        string otherMessageTemplate,
        params object?[] args)
    {
        if (SqliteDatabaseExceptionClassifier.IsStorageFull(exception))
        {
            logger.LogError(exception, storageFullMessageTemplate, args);
            return;
        }

        logger.LogError(exception, otherMessageTemplate, args);
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
