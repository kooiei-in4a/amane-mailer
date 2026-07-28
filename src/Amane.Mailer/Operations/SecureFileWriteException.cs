namespace Amane.Mailer.Operations;

/// <summary>
/// Raised when a secure create-new write fails after the target file was created.
/// <see cref="CreatedFileCleanupFailed"/> is true when the incomplete file could not be deleted.
/// </summary>
internal sealed class SecureFileWriteException : IOException
{
    public SecureFileWriteException(string message, Exception? innerException, bool createdFileCleanupFailed)
        : base(message, innerException)
    {
        CreatedFileCleanupFailed = createdFileCleanupFailed;
    }

    public bool CreatedFileCleanupFailed { get; }
}
