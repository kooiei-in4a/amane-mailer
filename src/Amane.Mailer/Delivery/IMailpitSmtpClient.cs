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

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}
