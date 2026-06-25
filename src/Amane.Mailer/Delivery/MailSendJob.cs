namespace Amane.Mailer.Delivery;

public sealed record MailSendJob(
    Guid MailRequestId,
    string SourceService,
    string Subject,
    string? HtmlBody,
    string? TextBody,
    string? ReplyTo,
    string RecipientEmail,
    string? RecipientDisplayName);
