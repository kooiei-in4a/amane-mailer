using System.Net;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

public sealed class WebhookDeliveryClientTests
{
    [Fact]
    public async Task DeliverAsync_returns_timeout_instead_of_throwing_when_delivery_exceeds_limit()
    {
        var factory = new SingleHandlerClientFactory(new SlowWebhookMessageHandler(TimeSpan.FromSeconds(5)));

        var client = new WebhookDeliveryClient(
            factory,
            new WebhookSignatureService(),
            new WebhookUrlValidator(),
            TimeProvider.System,
            NullLogger<WebhookDeliveryClient>.Instance);

        var tenant = new MailerTenant
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            Name = "example",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress { Email = "noreply@example.com" },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
            },
            Webhook = new MailerWebhookConfig
            {
                Url = "https://93.184.216.34/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
        };

        var payload = new MailDeliveryEventPayload
        {
            EventId = Guid.NewGuid(),
            EventType = MailDeliveryEventType.Delivered,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = tenant.TenantId,
            SourceService = "example-service",
            MailRequestId = Guid.NewGuid(),
            Status = MailDeliveryEventType.Delivered,
            AttemptCount = 1,
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await client.DeliverAsync(
            tenant,
            "secret",
            payload,
            """{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""",
            timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TIMEOUT", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    private sealed class SlowWebhookMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }
}
