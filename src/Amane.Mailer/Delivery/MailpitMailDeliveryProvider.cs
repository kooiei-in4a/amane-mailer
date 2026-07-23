using Amane.Mailer.Configuration;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Delivery;

public sealed class MailpitMailDeliveryProvider
{
    private readonly MailerOptions _options;
    private readonly ILogger<MailpitMailDeliveryProvider> _logger;
    private readonly Func<IMailpitSmtpClient> _clientFactory;

    public MailpitMailDeliveryProvider(
        MailerOptions options,
        ILogger<MailpitMailDeliveryProvider> logger)
        : this(options, logger, static () => new MailKitSmtpClient())
    {
    }

    // Tests construct without DI logging.
    public MailpitMailDeliveryProvider(MailerOptions options)
        : this(options, NullLogger<MailpitMailDeliveryProvider>.Instance)
    {
    }

    internal MailpitMailDeliveryProvider(
        MailerOptions options,
        ILogger<MailpitMailDeliveryProvider> logger,
        Func<IMailpitSmtpClient> clientFactory)
    {
        _options = options;
        _logger = logger;
        _clientFactory = clientFactory;
    }

    public async Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = OutboundMimeMessageFactory.Create(job, tenant);
            await using var client = _clientFactory();
            var socketOptions = _options.MailpitUseSsl
                ? SecureSocketOptions.StartTlsWhenAvailable
                : SecureSocketOptions.None;

            await client.ConnectAsync(
                _options.MailpitSmtpHost,
                _options.MailpitSmtpPort,
                socketOptions,
                cancellationToken);
            await client.SendAsync(message, cancellationToken);

            // SMTP DATA accepted: do not convert later disconnect failures into retryable
            // Failure (that would schedule a duplicate send). (#275)
            try
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Mailpit SMTP disconnect failed after the message was accepted; treating send as success. ErrorType={ErrorType}",
                    ex.GetType().Name);
            }

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
