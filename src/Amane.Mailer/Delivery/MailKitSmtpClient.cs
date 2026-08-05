using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Amane.Mailer.Delivery;

internal sealed class MailKitSmtpClient : IMailpitSmtpClient
{
    private readonly SmtpClient _client = new();

    public Task ConnectAsync(
        string host,
        int port,
        SecureSocketOptions socketOptions,
        CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, socketOptions, cancellationToken);

    public Task SendAsync(
        MimeMessage message,
        MailboxAddress sender,
        IReadOnlyList<MailboxAddress> recipients,
        CancellationToken cancellationToken) =>
        _client.SendAsync(message, sender, recipients, cancellationToken);

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken) =>
        _client.DisconnectAsync(quit, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
