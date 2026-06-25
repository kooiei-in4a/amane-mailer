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

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static MailboxAddress ToMailboxAddress(string email, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? MailboxAddress.Parse(email)
            : new MailboxAddress(displayName, email);
}
