using Amane.Mailer.Configuration;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
        Assert.Equal(MailerWebhookOptions.DefaultReconcileBatchSize, options.ReconcileBatchSize);
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
    [InlineData(MailerWebhookOptions.ReconcileBatchSizeKey, "1")]
    [InlineData(MailerWebhookOptions.ReconcileBatchSizeKey, "100")]
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
                [MailerWebhookOptions.ReconcileBatchSizeKey] = "1",
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
    [InlineData(MailerWebhookOptions.ReconcileBatchSizeKey, "0")]
    [InlineData(MailerWebhookOptions.ReconcileBatchSizeKey, "101")]
    [InlineData(MailerWebhookOptions.ReconcileBatchSizeKey, "")]
    [InlineData(MailerWebhookOptions.LegacyBatchClaimSizeKey, "0")]
    [InlineData(MailerWebhookOptions.LegacyBatchClaimSizeKey, "101")]
    [InlineData(MailerWebhookOptions.LegacyBatchClaimSizeKey, "")]
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
    public void Load_new_reconcile_batch_size_key_only()
    {
        var options = MailerWebhookOptions.Load(BuildConfiguration(
            (MailerWebhookOptions.ReconcileBatchSizeKey, "17")));

        Assert.Equal(17, options.ReconcileBatchSize);
    }

    [Fact]
    public void Load_legacy_batch_claim_size_key_only_still_works()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var logger = loggerFactory.CreateLogger(typeof(MailerWebhookOptions));

        var options = MailerWebhookOptions.Load(
            BuildConfiguration((MailerWebhookOptions.LegacyBatchClaimSizeKey, "23")),
            logger);

        Assert.Equal(23, options.ReconcileBatchSize);
        Assert.Contains(
            loggerProvider.Snapshot(),
            entry => entry.Level == LogLevel.Warning
                && entry.FormattedMessage.Contains(
                    MailerWebhookOptions.LegacyBatchClaimSizeKey,
                    StringComparison.Ordinal)
                && entry.FormattedMessage.Contains(
                    MailerWebhookOptions.ReconcileBatchSizeKey,
                    StringComparison.Ordinal)
                && entry.FormattedMessage.Contains("deprecated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_prefers_new_key_when_both_reconcile_batch_keys_are_set()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var logger = loggerFactory.CreateLogger(typeof(MailerWebhookOptions));

        var options = MailerWebhookOptions.Load(
            BuildConfiguration(
                (MailerWebhookOptions.ReconcileBatchSizeKey, "31"),
                (MailerWebhookOptions.LegacyBatchClaimSizeKey, "7")),
            logger);

        Assert.Equal(31, options.ReconcileBatchSize);
        Assert.Contains(
            loggerProvider.Snapshot(),
            entry => entry.Level == LogLevel.Warning
                && entry.FormattedMessage.Contains("ignored", StringComparison.OrdinalIgnoreCase)
                && entry.FormattedMessage.Contains(
                    MailerWebhookOptions.LegacyBatchClaimSizeKey,
                    StringComparison.Ordinal)
                && entry.FormattedMessage.Contains(
                    MailerWebhookOptions.ReconcileBatchSizeKey,
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Load_when_both_keys_set_invalid_legacy_is_ignored_if_new_key_is_valid()
    {
        var options = MailerWebhookOptions.Load(BuildConfiguration(
            (MailerWebhookOptions.ReconcileBatchSizeKey, "12"),
            (MailerWebhookOptions.LegacyBatchClaimSizeKey, "0")));

        Assert.Equal(12, options.ReconcileBatchSize);
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

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] pairs)
    {
        var values = pairs.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
