using System.Net;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Tests;

public sealed class WebhookUrlValidatorTests
{
    [Theory]
    [InlineData("https://8.8.8.8/mailer")]
    [InlineData("https://93.184.216.34/webhook")]
    public async Task ValidateAsync_accepts_public_https_url(string url)
    {
        var validator = new WebhookUrlValidator();
        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = url,
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Uri);
    }

    [Theory]
    [InlineData("http://hooks.example.com/mailer", "WEBHOOK_URL_NOT_HTTPS")]
    [InlineData("https://127.0.0.1/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://10.0.0.1/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://169.254.169.254/latest/meta-data", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://localhost/webhook", "WEBHOOK_URL_HOST_BLOCKED")]
    public async Task ValidateAsync_rejects_unsafe_urls(string url, string expectedErrorCode)
    {
        var validator = new WebhookUrlValidator();
        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = url,
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_enforces_allowed_host_suffixes()
    {
        var validator = new WebhookUrlValidator();
        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = "https://93.184.216.34/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
                AllowedHostSuffixes = ["example.com"],
            },
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("WEBHOOK_URL_HOST_NOT_ALLOWED", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_pins_first_public_dns_address()
    {
        var validator = new WebhookUrlValidator((_, _) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]));

        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = "https://hooks.example.com/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("hooks.example.com", result.OriginalHost);
        Assert.Equal("93.184.216.34", result.ConnectAddress!.ToString());
    }

    [Fact]
    public async Task ValidateAsync_rejects_when_dns_returns_private_address()
    {
        var validator = new WebhookUrlValidator((_, _) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.1")]));

        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = "https://hooks.example.com/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("WEBHOOK_URL_IP_BLOCKED", result.ErrorCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.10")]
    [InlineData("169.254.1.1")]
    public void IsBlockedIpAddress_blocks_private_ranges(string ipText)
    {
        Assert.True(WebhookUrlValidator.IsBlockedIpAddress(IPAddress.Parse(ipText)));
    }
}
