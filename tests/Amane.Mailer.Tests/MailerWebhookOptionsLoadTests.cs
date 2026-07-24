using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerWebhookOptionsLoadTests
{
    [Fact]
    public void Load_unset_keys_keep_defaults()
    {
        var options = MailerWebhookOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(MailerWebhookOptions.DefaultMaxAttempts, options.MaxAttempts);
        Assert.Equal(MailerWebhookOptions.DefaultInitialDelaySeconds, options.InitialDelaySeconds);
        Assert.Equal(MailerWebhookOptions.DefaultMaxDelaySeconds, options.MaxDelaySeconds);
        Assert.Equal(MailerWebhookOptions.DefaultBatchClaimSize, options.BatchClaimSize);
        Assert.Equal(MailerWebhookOptions.DefaultDeliveryTimeoutSeconds, options.DeliveryTimeoutSeconds);
        Assert.Equal(MailerWebhookOptions.DefaultLeaseDurationSeconds, options.LeaseDurationSeconds);
        options.Validate();
    }

    [Theory]
    [InlineData("Mailer:Webhook:MaxAttempts", "1")]
    [InlineData("Mailer:Webhook:MaxAttempts", "50")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "1")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "86400")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "1")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "86400")]
    [InlineData("Mailer:Webhook:BatchClaimSize", "1")]
    [InlineData("Mailer:Webhook:BatchClaimSize", "100")]
    [InlineData("Mailer:Webhook:DeliveryTimeoutSeconds", "1")]
    [InlineData("Mailer:Webhook:DeliveryTimeoutSeconds", "600")]
    [InlineData("Mailer:Webhook:LeaseDurationSeconds", "12")]
    [InlineData("Mailer:Webhook:LeaseDurationSeconds", "86400")]
    public void Load_accepts_min_and_max_bounds(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Webhook:MaxAttempts"] = "1",
                ["Mailer:Webhook:InitialDelaySeconds"] = "1",
                ["Mailer:Webhook:MaxDelaySeconds"] = "86400",
                ["Mailer:Webhook:BatchClaimSize"] = "1",
                ["Mailer:Webhook:DeliveryTimeoutSeconds"] = "1",
                ["Mailer:Webhook:LeaseDurationSeconds"] = "620",
                [key] = value,
            })
            .Build();

        var options = MailerWebhookOptions.Load(configuration);
        options.Validate();
    }

    [Theory]
    [InlineData("Mailer:Webhook:MaxAttempts", "0")]
    [InlineData("Mailer:Webhook:MaxAttempts", "-1")]
    [InlineData("Mailer:Webhook:MaxAttempts", "51")]
    [InlineData("Mailer:Webhook:MaxAttempts", "abc")]
    [InlineData("Mailer:Webhook:MaxAttempts", "")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "0")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "-1")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "86401")]
    [InlineData("Mailer:Webhook:InitialDelaySeconds", "")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "0")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "-5")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "86401")]
    [InlineData("Mailer:Webhook:MaxDelaySeconds", "")]
    [InlineData("Mailer:Webhook:BatchClaimSize", "0")]
    [InlineData("Mailer:Webhook:BatchClaimSize", "101")]
    [InlineData("Mailer:Webhook:BatchClaimSize", "")]
    [InlineData("Mailer:Webhook:DeliveryTimeoutSeconds", "0")]
    [InlineData("Mailer:Webhook:DeliveryTimeoutSeconds", "601")]
    [InlineData("Mailer:Webhook:DeliveryTimeoutSeconds", "")]
    [InlineData("Mailer:Webhook:LeaseDurationSeconds", "0")]
    [InlineData("Mailer:Webhook:LeaseDurationSeconds", "86401")]
    [InlineData("Mailer:Webhook:LeaseDurationSeconds", "")]
    public void Load_rejects_invalid_values(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerWebhookOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_initial_delay_greater_than_max_delay()
    {
        var options = new MailerWebhookOptions
        {
            InitialDelaySeconds = 100,
            MaxDelaySeconds = 50,
            LeaseDurationSeconds = 60,
            DeliveryTimeoutSeconds = 30,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("InitialDelaySeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MaxDelaySeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_lease_not_greater_than_delivery_plus_finalize()
    {
        var options = new MailerWebhookOptions
        {
            DeliveryTimeoutSeconds = 30,
            LeaseDurationSeconds = 40,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }
}
