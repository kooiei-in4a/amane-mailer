using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

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
            using var message = OutboundMimeMessageFactory.Create(job, tenant);
            var sender = (MailboxAddress)message.From[0];
            var recipients = OutboundMimeMessageFactory.BuildEnvelopeRecipients(job);
            await using var client = _clientFactory();
            var socketOptions = _options.MailpitUseSsl
                ? SecureSocketOptions.StartTlsWhenAvailable
                : SecureSocketOptions.None;

            await client.ConnectAsync(
                _options.MailpitSmtpHost,
                _options.MailpitSmtpPort,
                socketOptions,
                cancellationToken);
            // Explicit envelope (Issue #546 review finding F4): recipients (including Bcc) are
            // passed here, not read from message.To/Cc/Bcc -- message never carries a Bcc header.
            await client.SendAsync(message, sender, recipients, cancellationToken);

            // SMTP DATA accepted: do not convert later disconnect failures (including send-timeout
            // cancellation) into retryable Failure — that would schedule a duplicate send. (#275)
            try
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Mailpit SMTP disconnect failed after the message was accepted; treating send as success. ErrorType={ErrorType}",
                    ex.GetType().Name);
            }

            return MailDeliveryResult.Success();
        }
        catch (AttachmentSpoolFileReadException)
        {
            // Never let the underlying file I/O exception (which embeds the private spool path,
            // ADR 0022 D-08/D-14) reach the generic sanitizer below.
            return MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.AttachmentStorageMissing,
                "Attachment spool file could not be read.",
                retryable: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (errorCode, retryable) = ProviderErrorClassifier.Classify(ex);
            return MailDeliveryResult.Failure(
                errorCode,
                ProviderErrorSanitizer.Sanitize(ex.Message),
                retryable);
        }
    }
}
