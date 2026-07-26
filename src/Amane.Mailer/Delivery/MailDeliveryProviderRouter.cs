using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;

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
                MailDeliveryErrorCodes.LiveSendingDisabled,
                "ACS delivery is disabled for this tenant because live_sending is false.",
                retryable: false));
        }

        return provider switch
        {
            "mailpit" => mailpit.SendAsync(job, tenant, provider, cancellationToken),
            "acs" => acs.SendAsync(job, tenant, provider, cancellationToken),
            _ => Task.FromResult(MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.UnknownProvider,
                $"Unknown mail provider '{provider}'.",
                retryable: false)),
        };
    }
}
