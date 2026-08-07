using Amane.Mailer.Configuration;
using MimeKit;

namespace Amane.Mailer.Delivery;

internal static class OutboundMimeMessageFactory
{
    public static MimeMessage Create(MailSendJob job, MailerTenant tenant)
    {
        var message = new MimeMessage();
        message.From.Add(ToMailboxAddress(tenant.DefaultFrom.Email, tenant.DefaultFrom.DisplayName));

        // Global provider order To -> Cc -> Bcc (ADR 0023 D-01). Bcc is deliberately never added
        // to this MimeMessage at all -- Issue #546 review finding F4: a literal "Bcc:" header
        // (which MimeMessage.WriteTo() always emits verbatim once message.Bcc is populated) must
        // never exist, not merely be stripped by whichever transport happens to send this object.
        // The SMTP envelope recipient list (including Bcc) is built separately by
        // BuildEnvelopeRecipients and passed to MailKit's explicit-envelope SendAsync overload
        // (MailpitMailDeliveryProvider), which computes RCPT TO from that list, not from this
        // message's To/Cc/Bcc headers. Never fold Bcc into To/Cc here either.
        AddRecipients(message.To, job.To);
        AddRecipients(message.Cc, job.Cc);
        message.Subject = job.Subject;

        if (!string.IsNullOrWhiteSpace(job.ReplyTo))
        {
            message.ReplyTo.Add(MailboxAddress.Parse(job.ReplyTo));
        }

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(job.TextBody))
        {
            builder.TextBody = job.TextBody;
        }

        if (!string.IsNullOrWhiteSpace(job.HtmlBody))
        {
            builder.HtmlBody = job.HtmlBody;
        }

        if (job.Attachments is { Count: > 0 })
        {
            foreach (var attachment in job.Attachments)
            {
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(attachment.FilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new AttachmentSpoolFileReadException();
                }

                var part = builder.Attachments.Add(
                    attachment.FileName,
                    content,
                    ContentType.Parse(attachment.ContentType));

                // Force Base64 regardless of MimeKit's content-based auto-selection (#525 S-15
                // finding: MimeKit picks 7bit for text-safe content, and 7bit/8bit/quoted-
                // printable transport canonicalizes line endings (LF -> CRLF), silently changing
                // the decoded byte digest for attachments whose original bytes used bare LF.
                // Base64 is the only encoding here that is a pure binary-safe round trip.)
                if (part is MimePart mimePart)
                {
                    mimePart.ContentTransferEncoding = ContentEncoding.Base64;
                }
            }
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>
    /// Full SMTP envelope recipient list (To + Cc + Bcc, global order) for MailKit's explicit-
    /// envelope <c>SendAsync(message, sender, recipients, ct)</c> overload. This is the only
    /// place Bcc addresses are represented for an SMTP send -- they are never added to the
    /// <see cref="MimeMessage"/> itself (see <see cref="Create"/>).
    /// </summary>
    public static IReadOnlyList<MailboxAddress> BuildEnvelopeRecipients(MailSendJob job)
    {
        var recipients = new List<MailboxAddress>(job.To.Count + job.Cc.Count + job.Bcc.Count);
        AppendMailboxes(recipients, job.To);
        AppendMailboxes(recipients, job.Cc);
        AppendMailboxes(recipients, job.Bcc);
        return recipients;
    }

    private static void AppendMailboxes(List<MailboxAddress> destination, IReadOnlyList<MailSendRecipient> recipients)
    {
        foreach (var recipient in recipients)
        {
            destination.Add(ToMailboxAddress(recipient.Address, recipient.DisplayName));
        }
    }

    private static void AddRecipients(InternetAddressList list, IReadOnlyList<MailSendRecipient> recipients)
    {
        foreach (var recipient in recipients)
        {
            list.Add(ToMailboxAddress(recipient.Address, recipient.DisplayName));
        }
    }

    private static MailboxAddress ToMailboxAddress(string email, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? MailboxAddress.Parse(email)
            : new MailboxAddress(displayName, email);
}
