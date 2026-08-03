using MimeKit;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// Spike-only (#525) MIME/envelope builder. Deliberately separate from the production
/// <c>OutboundMimeMessageFactory</c> (which today only supports a single To recipient and
/// no attachments) so this spike never exercises or pre-commits a public CC/BCC/attachment
/// contract. Used only to probe ACS/Mailpit provider mechanics.
///
/// BCC handling here uses the explicit-envelope pattern (Bcc recipients added only to the
/// envelope recipient list passed to MailKit's
/// <c>SmtpClient.SendAsync(message, sender, recipients, ...)</c> overload, never to
/// <see cref="MimeMessage.Bcc"/>) for S-02. Per #525 Agent B review follow-up (S-02b),
/// populating <see cref="MimeMessage.Bcc"/> directly and sending via the actual production
/// single-argument <c>SendAsync(message, ct)</c> path was separately verified NOT to leak a
/// Bcc header onto the wire either — MailKit's single-argument overload already omits Bcc from
/// the serialized DATA while still using it to compute the envelope. Both patterns are
/// wire-safe for Mailpit/SMTP; see Spike525MailpitEnvelopeTests for both verifications.
/// </summary>
internal static class SpikeMimeFactory
{
    internal sealed record RecipientSet(
        IReadOnlyList<string> To,
        IReadOnlyList<string> Cc,
        IReadOnlyList<string> Bcc);

    internal sealed record SyntheticAttachment(
        string FileName,
        string ContentType,
        byte[] Content);

    internal static MimeMessage BuildMessage(
        string fromAddress,
        RecipientSet recipients,
        string subject,
        string textBody,
        IReadOnlyList<SyntheticAttachment>? attachments = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(fromAddress));

        foreach (var to in recipients.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        foreach (var cc in recipients.Cc)
        {
            message.Cc.Add(MailboxAddress.Parse(cc));
        }

        // Intentionally NOT adding recipients.Bcc to message.Bcc here — see type doc.

        message.Subject = subject;

        var builder = new BodyBuilder { TextBody = textBody };
        if (attachments is not null)
        {
            foreach (var attachment in attachments)
            {
                var part = builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));

                // Force Base64 regardless of MimeKit's content-based auto-selection (#525 S-15
                // finding: MimeKit picks 7bit for text-safe content, and 7bit/8bit/quoted-printable
                // transport canonicalizes line endings (LF -> CRLF), silently changing the decoded
                // byte digest for attachments whose original bytes used bare LF. Base64 is the only
                // encoding here that is a pure binary-safe round trip.)
                if (part is MimePart mimePart)
                {
                    mimePart.ContentTransferEncoding = ContentEncoding.Base64;
                }
            }
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>Full envelope recipient list (To+Cc+Bcc) for the explicit-recipients SmtpClient overload.</summary>
    internal static IReadOnlyList<MailboxAddress> EnvelopeRecipients(RecipientSet recipients) =>
        recipients.To
            .Concat(recipients.Cc)
            .Concat(recipients.Bcc)
            .Select(MailboxAddress.Parse)
            .ToList();
}
