using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailDeliveryProviderRouterTests
{
    [Fact]
    public async Task Acs_provider_is_blocked_when_live_sending_is_disabled()
    {
        var router = new MailDeliveryProviderRouter(
            new MailpitMailDeliveryProvider(new MailerOptions()),
            new AcsMailDeliveryProvider(new MailerOptions()));
        var tenant = new MailerTenant
        {
            TenantId = MailerWebApplicationFixtureBase.TenantId,
            Name = "example-develop",
            SourceServices = [MailerWebApplicationFixtureBase.SourceService],
            DefaultFrom = new MailerAddress
            {
                Email = "noreply@example.com",
                DisplayName = "Example Service",
            },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "acs",
            LiveSending = false,
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
            },
        };
        var request = MailRequestTestData.CreateRequest();
        var job = new MailSendJob(
            request.MailRequestId,
            request.SourceService,
            request.Subject,
            request.HtmlBody,
            request.TextBody,
            request.ReplyTo,
            request.To[0].Email,
            request.To[0].DisplayName);

        var result = await router.SendAsync(
            job,
            tenant,
            "acs",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("LIVE_SENDING_DISABLED", result.ErrorCode);
    }
}
