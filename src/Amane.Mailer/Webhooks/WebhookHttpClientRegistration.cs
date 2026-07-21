namespace Amane.Mailer.Webhooks;

using Amane.Mailer.Configuration;

public static class WebhookHttpClientRegistration
{
    public const string ClientName = "mailer-webhook-delivery";

    public static IServiceCollection AddWebhookHttpClient(
        this IServiceCollection services,
        MailerWebhookOptions options)
    {
        services.AddHttpClient(ClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.DeliveryTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = WebhookConnectCallback.ConnectAsync,
        });

        return services;
    }
}
