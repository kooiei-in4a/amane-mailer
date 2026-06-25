using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Tests;

public sealed class AcsMailDeliveryProviderTests
{
    [Fact]
    public async Task SendAsync_returns_not_configured_when_connection_string_is_missing()
    {
        var provider = new AcsMailDeliveryProvider(new MailerOptions());
        var tenant = new MailerTenant
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            Name = "tenant",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress { Email = "noreply@example.com" },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "acs",
            LiveSending = true,
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

        var result = await provider.SendAsync(job, tenant, "acs", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("ACS_NOT_CONFIGURED", result.ErrorCode);
    }
}
