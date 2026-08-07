namespace Amane.Mailer.Delivery;

/// <summary>
/// A single provider-bound recipient built from canonical <c>mail_request_recipients</c> rows
/// (ADR 0023 D-01/D-03/D-10), never from the legacy <c>mail_requests.recipient_email</c> shadow.
/// </summary>
public sealed record MailSendRecipient(string Address, string? DisplayName);

/// <summary>
/// <see cref="To"/>/<see cref="Cc"/>/<see cref="Bcc"/> preserve role-internal submission order;
/// provider adapters and <see cref="OutboundMimeMessageFactory"/> apply the global
/// To -&gt; Cc -&gt; Bcc order themselves (ADR 0023 D-01). One <see cref="MailSendJob"/> is always
/// sent as exactly one provider message -- callers must never split recipients across multiple
/// invocations.
/// </summary>
public sealed record MailSendJob(
    Guid MailRequestId,
    string SourceService,
    string Subject,
    string? HtmlBody,
    string? TextBody,
    string? ReplyTo,
    IReadOnlyList<MailSendRecipient> To,
    IReadOnlyList<MailSendRecipient> Cc,
    IReadOnlyList<MailSendRecipient> Bcc,
    IReadOnlyList<MailSendAttachment>? Attachments = null)
{
    /// <summary>
    /// Convenience for the single-recipient shape, which remains the only shape reachable
    /// through the public HTTP contract while <c>IsLegacySingleTo</c> gates runtime acceptance
    /// (ADR 0023 D-11 issue split) and the only shape the attachment send path (ADR 0022, out of
    /// Issue #546 scope) constructs.
    /// </summary>
    public static MailSendJob ForSingleRecipient(
        Guid mailRequestId,
        string sourceService,
        string subject,
        string? htmlBody,
        string? textBody,
        string? replyTo,
        string recipientEmail,
        string? recipientDisplayName,
        IReadOnlyList<MailSendAttachment>? attachments = null) =>
        new(
            mailRequestId,
            sourceService,
            subject,
            htmlBody,
            textBody,
            replyTo,
            To: [new MailSendRecipient(recipientEmail, recipientDisplayName)],
            Cc: [],
            Bcc: [],
            Attachments: attachments);
}

/// <summary>
/// A validated attachment ready for provider send (ADR 0022 D-11). <see cref="FilePath"/> is
/// the committed spool file; providers read bytes from disk rather than holding them in the job.
/// </summary>
public sealed record MailSendAttachment(
    string FileName,
    string ContentType,
    long ByteLength,
    string FilePath);
