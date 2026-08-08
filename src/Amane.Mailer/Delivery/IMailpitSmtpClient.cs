using MailKit.Security;
using MimeKit;

namespace Amane.Mailer.Delivery;

internal interface IMailpitSmtpClient : IAsyncDisposable
{
    Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions socketOptions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Explicit-envelope send (Issue #546 review finding F4): <paramref name="recipients"/> is
    /// the full SMTP envelope recipient list (To + Cc + Bcc). <paramref name="message"/> itself
    /// never carries a Bcc header/address -- see <see cref="OutboundMimeMessageFactory.Create"/>.
    /// </summary>
    Task SendAsync(
        MimeMessage message,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}
