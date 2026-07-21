using System.Net;

namespace Amane.Mailer.Webhooks;

internal static class WebhookConnectionOptions
{
    internal static readonly HttpRequestOptionsKey<IPAddress> PinnedConnectAddress =
        new("Amane.Mailer.WebhookPinnedConnectAddress");
}
