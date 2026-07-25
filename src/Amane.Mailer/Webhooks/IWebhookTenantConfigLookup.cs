using Amane.Mailer.Configuration;

namespace Amane.Mailer.Webhooks;

/// <summary>
/// Tenant webhook URL/secret lookup used by <see cref="WebhookDeliveryWorker"/>.
/// Allows test doubles to inject resolve_config faults.
/// </summary>
internal interface IWebhookTenantConfigLookup
{
    MailerTenant? Find(Guid tenantId);

    string? GetWebhookSecret(Guid tenantId);
}
