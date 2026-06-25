using Amane.Mailer.Configuration;

namespace Amane.Mailer.Delivery;

public sealed class MailDeliveryProviderRouter(
    MailpitMailDeliveryProvider mailpit,
    AcsMailDeliveryProvider acs) : IMailDeliveryProvider
{
    public Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        if (provider.Equals("acs", StringComparison.Ordinal)
            && !tenant.LiveSending)
        {
            return Task.FromResult(MailDeliveryResult.Failure(
                "LIVE_SENDING_DISABLED",
                "ACS delivery is disabled for this tenant because live_sending is false.",
                retryable: false));
        }

        return provider switch
        {
            "mailpit" => mailpit.SendAsync(job, tenant, provider, cancellationToken),
            "acs" => acs.SendAsync(job, tenant, provider, cancellationToken),
            _ => Task.FromResult(MailDeliveryResult.Failure(
                "UNKNOWN_PROVIDER",
                $"Unknown mail provider '{provider}'.",
                retryable: false)),
        };
    }
}
