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

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("64:ff9b::192.168.1.1")]
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("64:ff9b::127.0.0.1")]
    [InlineData("2002:0a00:0001::1")]
    [InlineData("2002:c0a8:0001::1")]
    public void IsBlockedIpAddress_blocks_ipv6_special_and_embedded_private(string ipText)
    {
        Assert.True(WebhookUrlValidator.IsBlockedIpAddress(IPAddress.Parse(ipText)));
    }

    [Theory]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::8.8.8.8")]
    [InlineData("64:ff9b::93.184.216.34")]
    [InlineData("2002:0808:0808::1")]
    public void IsBlockedIpAddress_allows_public_ipv6_and_public_embeddings(string ipText)
    {
        Assert.False(WebhookUrlValidator.IsBlockedIpAddress(IPAddress.Parse(ipText)));
    }

    [Theory]
    [InlineData("https://[::1]/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://[fc00::1]/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://[64:ff9b::10.0.0.1]/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    [InlineData("https://[2002:0a00:0001::1]/webhook", "WEBHOOK_URL_IP_BLOCKED")]
    public async Task ValidateAsync_rejects_unsafe_ipv6_urls(string url, string expectedErrorCode)
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
    public async Task ValidateAsync_accepts_public_ipv6_literal()
    {
        var validator = new WebhookUrlValidator();
        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = "https://[2001:4860:4860::8888]/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Uri);
    }

    [Fact]
    public async Task ValidateAsync_rejects_when_dns_returns_nat64_private_embedding()
    {
        var validator = new WebhookUrlValidator((_, _) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("64:ff9b::10.0.0.1")]));

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

    [Fact]
    public async Task ValidateAsync_pins_public_ipv6_dns_address()
    {
        var validator = new WebhookUrlValidator((_, _) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("2001:4860:4860::8888")]));

        var result = await validator.ValidateAsync(
            new Configuration.MailerWebhookConfig
            {
                Url = "https://hooks.example.com/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("hooks.example.com", result.OriginalHost);
        Assert.Equal("2001:4860:4860::8888", result.ConnectAddress!.ToString());
    }
}
