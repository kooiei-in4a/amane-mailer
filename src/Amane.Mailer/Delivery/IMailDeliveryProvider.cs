using Amane.Mailer.Configuration;

namespace Amane.Mailer.Delivery;

public interface IMailDeliveryProvider
{
    Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken);
}
