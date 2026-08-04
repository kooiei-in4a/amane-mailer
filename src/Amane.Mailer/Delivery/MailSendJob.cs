namespace Amane.Mailer.Delivery;

public sealed record MailSendJob(
    Guid MailRequestId,
    string SourceService,
    string Subject,
    string? HtmlBody,
    string? TextBody,
    string? ReplyTo,
    string RecipientEmail,
    string? RecipientDisplayName,
    IReadOnlyList<MailSendAttachment>? Attachments = null);

/// <summary>
/// A validated attachment ready for provider send (ADR 0022 D-11). <see cref="FilePath"/> is
/// the committed spool file; providers read bytes from disk rather than holding them in the job.
/// </summary>
public sealed record MailSendAttachment(
    string FileName,
    string ContentType,
    long ByteLength,
    string FilePath);
