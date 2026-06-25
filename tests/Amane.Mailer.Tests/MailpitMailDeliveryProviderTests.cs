using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;

namespace Amane.Mailer.Tests;

public sealed class MailpitMailDeliveryProviderTests
{
    [Fact]
    public async Task SendAsync_uses_mailkit_and_returns_retryable_failure_for_unreachable_host()
    {
        var provider = new MailpitMailDeliveryProvider(new MailerOptions
        {
            MailpitSmtpHost = "127.0.0.1",
            MailpitSmtpPort = 1,
            MailpitUseSsl = false,
        });

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
            Provider = "mailpit",
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await provider.SendAsync(job, tenant, "mailpit", cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }
}
