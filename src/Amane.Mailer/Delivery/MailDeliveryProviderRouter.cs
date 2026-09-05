using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Delivery;

public sealed class MailDeliveryProviderRouter(
    MailpitMailDeliveryProvider mailpit,
    AcsMailDeliveryProvider acs,
    InstanceConfigurationRepository? instanceConfiguration = null,
    InstanceRuntimeState? instanceRuntimeState = null) : IMailDeliveryProvider
{
    public async Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        if (provider.Equals("acs", StringComparison.Ordinal))
        {
            var liveSending = await ReadLiveSendingAsync(tenant, cancellationToken);
            if (!liveSending)
            {
                return MailDeliveryResult.Failure(
                    MailDeliveryErrorCodes.LiveSendingDisabled,
                    "ACS delivery is disabled because live_sending is false or unavailable.",
                    retryable: false);
            }
        }

        return provider switch
        {
            "mailpit" => await mailpit.SendAsync(job, tenant, provider, cancellationToken),
            "acs" => await acs.SendAsync(job, tenant, provider, cancellationToken),
            _ => MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.UnknownProvider,
                $"Unknown mail provider '{provider}'.",
                retryable: false),
        };
    }

    private async Task<bool> ReadLiveSendingAsync(
        MailerTenant tenant,
        CancellationToken cancellationToken)
    {
        if (instanceRuntimeState?.IsInitialized != true)
            return tenant.LiveSending;

        if (instanceConfiguration is null)
            return false;

        try
        {
            var configuration = await instanceConfiguration.GetAsync(cancellationToken);
            return configuration?.InitializedAt is not null && configuration.LiveSending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Managed delivery must fail closed when the durable runtime gate cannot be read.
            return false;
        }
    }
}
