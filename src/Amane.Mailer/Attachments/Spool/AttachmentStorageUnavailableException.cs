namespace Amane.Mailer.Attachments.Spool;

/// <summary>
/// Thrown when attachment acceptance must fail closed because the backup maintenance lease is
/// held (ADR 0022 D-09) or the spool is otherwise unavailable. Mapped to HTTP 503
/// ATTACHMENT_STORAGE_UNAVAILABLE (retryable).
/// </summary>
public sealed class AttachmentStorageUnavailableException(string message) : Exception(message);
