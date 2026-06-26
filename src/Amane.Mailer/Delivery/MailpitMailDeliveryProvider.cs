using Amane.Mailer.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Amane.Mailer.Delivery;

public sealed class MailpitMailDeliveryProvider(MailerOptions options)
{
    public async Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = OutboundMimeMessageFactory.Create(job, tenant);
            using var client = new SmtpClient();
            var socketOptions = options.MailpitUseSsl
                ? SecureSocketOptions.StartTlsWhenAvailable
                : SecureSocketOptions.None;

            await client.ConnectAsync(
                options.MailpitSmtpHost,
                options.MailpitSmtpPort,
                socketOptions,
                cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return MailDeliveryResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MailDeliveryResult.Failure(
                ex.GetType().Name,
                ProviderErrorSanitizer.Sanitize(ex.Message),
                retryable: true);
        }
    }
}
