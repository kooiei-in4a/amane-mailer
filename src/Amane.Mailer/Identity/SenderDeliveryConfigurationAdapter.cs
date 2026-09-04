using Amane.Mailer.Configuration;

namespace Amane.Mailer.Identity;

/// <summary>
/// Temporary #730 bridge: identity comes from DB Sender while provider/retry safety settings
/// remain sourced from the existing configuration until #731 replaces that configuration path.
/// </summary>
public sealed class SenderDeliveryConfigurationAdapter(MailerTenantRegistry tenantRegistry)
{
    public MailerTenant Resolve(SenderIdentity sender)
    {
        var template = tenantRegistry.ListTenants().First();
        return template with
        {
            TenantId = sender.SenderId,
            Name = "managed-sender",
            SourceServices = [V2PersistenceCompatibility.SourceService],
            DefaultFrom = new MailerAddress
            {
                Email = sender.Email,
                DisplayName = sender.DisplayName,
            },
            TokenEnv = "MANAGED_API_KEY",
            Webhook = null,
        };
    }
}
