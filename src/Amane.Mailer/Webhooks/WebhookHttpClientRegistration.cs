namespace Amane.Mailer.Webhooks;

using Amane.Mailer.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class WebhookHttpClientRegistration
{
    public const string ClientName = "mailer-webhook-delivery";

    public static IServiceCollection AddWebhookHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(provider =>
        {
            var options = provider.GetRequiredService<MailerWebhookOptions>();
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = options.DeliveryTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = WebhookConnectCallback.ConnectAsync,
            };
        });

        return services;
    }
}
