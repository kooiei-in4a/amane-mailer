using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerWorkerOptionsTests
{
    [Fact]
    public void Default_worker_options_are_lease_safe()
    {
        var options = new MailerWorkerOptions();

        options.Validate();
    }

    [Fact]
    public void Load_unset_keys_keep_defaults()
    {
        var options = MailerWorkerOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(MailerWorkerOptions.DefaultBatchClaimSize, options.BatchClaimSize);
        Assert.Equal(MailerWorkerOptions.DefaultMaxSendConcurrency, options.MaxSendConcurrency);
        Assert.Equal(MailerWorkerOptions.DefaultSendTimeoutSeconds, options.SendTimeoutSeconds);
        Assert.Equal(MailerWorkerOptions.DefaultLeaseDurationSeconds, options.LeaseDurationSeconds);
        options.Validate();
    }

    [Theory]
    [InlineData("Mailer:Worker:BatchClaimSize", "1", nameof(MailerWorkerOptions.BatchClaimSize), 1)]
    [InlineData("Mailer:Worker:BatchClaimSize", "100", nameof(MailerWorkerOptions.BatchClaimSize), 100)]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "1", nameof(MailerWorkerOptions.MaxSendConcurrency), 1)]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "64", nameof(MailerWorkerOptions.MaxSendConcurrency), 64)]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "1", nameof(MailerWorkerOptions.SendTimeoutSeconds), 1)]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "600", nameof(MailerWorkerOptions.SendTimeoutSeconds), 600)]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "12", nameof(MailerWorkerOptions.LeaseDurationSeconds), 12)]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "86400", nameof(MailerWorkerOptions.LeaseDurationSeconds), 86400)]
    public void Load_accepts_min_and_max_bounds(string key, string value, string propertyName, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Keep lease-safe companions when tightening one field.
                ["Mailer:Worker:BatchClaimSize"] = "1",
                ["Mailer:Worker:MaxSendConcurrency"] = "1",
                ["Mailer:Worker:SendTimeoutSeconds"] = "1",
                ["Mailer:Worker:LeaseDurationSeconds"] = "12",
                [key] = value,
            })
            .Build();

        var options = MailerWorkerOptions.Load(configuration);
        var actual = propertyName switch
        {
            nameof(MailerWorkerOptions.BatchClaimSize) => options.BatchClaimSize,
            nameof(MailerWorkerOptions.MaxSendConcurrency) => options.MaxSendConcurrency,
            nameof(MailerWorkerOptions.SendTimeoutSeconds) => options.SendTimeoutSeconds,
            nameof(MailerWorkerOptions.LeaseDurationSeconds) => options.LeaseDurationSeconds,
            _ => throw new InvalidOperationException(propertyName),
        };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Mailer:Worker:BatchClaimSize", "0")]
    [InlineData("Mailer:Worker:BatchClaimSize", "-1")]
    [InlineData("Mailer:Worker:BatchClaimSize", "101")]
    [InlineData("Mailer:Worker:BatchClaimSize", "abc")]
    [InlineData("Mailer:Worker:BatchClaimSize", "")]
    [InlineData("Mailer:Worker:BatchClaimSize", "  ")]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "0")]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "-3")]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "65")]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "nope")]
    [InlineData("Mailer:Worker:MaxSendConcurrency", "")]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "0")]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "-1")]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "601")]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "1.5")]
    [InlineData("Mailer:Worker:SendTimeoutSeconds", "")]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "0")]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "-1")]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "86401")]
    [InlineData("Mailer:Worker:LeaseDurationSeconds", "")]
    public void Load_rejects_invalid_values(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerWorkerOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_batch_waves_that_outlive_the_lease()
    {
        var options = new MailerWorkerOptions
        {
            BatchClaimSize = 10,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 120,
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void ShutdownDrainTimeout_covers_one_in_flight_wave_not_full_batch()
    {
        // Multi-wave batches cancel semaphore waiters on shutdown (#271);
        // drain budget stays one SendTimeout + FinalizeTimeout window.
        var options = new MailerWorkerOptions
        {
            BatchClaimSize = 8,
            MaxSendConcurrency = 2,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 400,
        };

        options.Validate();
        Assert.Equal(
            TimeSpan.FromSeconds(90 + MailerWorkerOptions.FinalizeTimeoutSeconds),
            options.ShutdownDrainTimeout);
    }

    [Fact]
    public void IsEnabled_unset_defaults_to_true()
    {
        Assert.True(MailerWorkerOptions.IsEnabled(new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    [InlineData("false", false)]
    public void IsEnabled_accepts_case_insensitive_booleans(string value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = value,
            })
            .Build();

        Assert.Equal(expected, MailerWorkerOptions.IsEnabled(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("yes")]
    [InlineData("0")]
    public void IsEnabled_rejects_empty_or_malformed(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerWorkerOptions.IsEnabled(configuration));
        Assert.Contains("Mailer:Worker:Enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("false", ex.Message, StringComparison.Ordinal);
    }
}
