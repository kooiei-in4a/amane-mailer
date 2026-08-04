using Amane.Mailer.Configuration;
using MimeKit;

namespace Amane.Mailer.Delivery;

internal static class OutboundMimeMessageFactory
{
    public static MimeMessage Create(MailSendJob job, MailerTenant tenant)
    {
        var message = new MimeMessage();
        message.From.Add(ToMailboxAddress(tenant.DefaultFrom.Email, tenant.DefaultFrom.DisplayName));
        message.To.Add(ToMailboxAddress(job.RecipientEmail, job.RecipientDisplayName));
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

    private static MailboxAddress ToMailboxAddress(string email, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? MailboxAddress.Parse(email)
            : new MailboxAddress(displayName, email);
}
