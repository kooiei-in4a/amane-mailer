using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerHealthcheckOptionsTests
{
    private static readonly MailerSweepOptions DefaultSweepOptions = new();

    [Fact]
    public void Load_unset_keys_keep_defaults()
    {
        var options = MailerHealthcheckOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(
            MailerHealthcheckOptions.DefaultMaxHeartbeatStalenessSeconds,
            options.MaxHeartbeatStalenessSeconds);
        Assert.Equal(
            MailerHealthcheckOptions.DefaultWorkerHeartbeatIntervalSeconds,
            options.WorkerHeartbeatIntervalSeconds);
    }

    [Theory]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "1")]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "86400")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "1")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "3600")]
    public void Load_accepts_min_and_max(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        _ = MailerHealthcheckOptions.Load(configuration);
    }

    [Theory]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "0")]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "-1")]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "86401")]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "abc")]
    [InlineData("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", "")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "0")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "-1")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "3601")]
    [InlineData("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", "")]
    public void Load_rejects_invalid_values(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerHealthcheckOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_staleness_below_drain_floor()
    {
        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = 100,
            WorkerHeartbeatIntervalSeconds = 60,
        };
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 4,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 120,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void Validate_accepts_staleness_at_drain_floor()
    {
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 4,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 120,
        };
        var sendWaves = (workerOptions.BatchClaimSize + workerOptions.MaxSendConcurrency - 1)
            / workerOptions.MaxSendConcurrency;
        var floor = (sendWaves * 90) + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = floor,
            WorkerHeartbeatIntervalSeconds = 60,
        };

        healthcheckOptions.Validate(workerOptions, DefaultSweepOptions);
    }

    [Fact]
    public void Validate_rejects_staleness_not_greater_than_heartbeat_interval()
    {
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 1,
            MaxSendConcurrency = 1,
            SendTimeoutSeconds = 1,
            LeaseDurationSeconds = 50,
        };
        var sendWaves = (workerOptions.BatchClaimSize + workerOptions.MaxSendConcurrency - 1)
            / workerOptions.MaxSendConcurrency;
        var floor = (sendWaves * workerOptions.SendTimeoutSeconds)
            + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = Math.Max(floor, 60),
            WorkerHeartbeatIntervalSeconds = Math.Max(floor, 60),
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void Validate_rejects_staleness_not_greater_than_sweep_interval()
    {
        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = 300,
            WorkerHeartbeatIntervalSeconds = 60,
        };
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 1,
            MaxSendConcurrency = 1,
            SendTimeoutSeconds = 1,
            LeaseDurationSeconds = 50,
        };
        var sweepOptions = new MailerSweepOptions
        {
            IntervalSeconds = 300,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, sweepOptions));
    }

    [Fact]
    public void Validate_rejects_staleness_below_default_worker_drain_floor()
    {
        var sendWaves = (MailerWorkerOptions.DefaultBatchClaimSize + MailerWorkerOptions.DefaultMaxSendConcurrency - 1)
            / MailerWorkerOptions.DefaultMaxSendConcurrency;
        var minimumStaleness = (sendWaves * MailerWorkerOptions.DefaultSendTimeoutSeconds)
            + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = minimumStaleness - 1,
            WorkerHeartbeatIntervalSeconds = 60,
        };
        var workerOptions = new MailerWorkerOptions();

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void Default_healthcheck_options_are_valid_with_default_worker_and_sweep()
    {
        var healthcheckOptions = new MailerHealthcheckOptions();
        var workerOptions = new MailerWorkerOptions();

        healthcheckOptions.Validate(workerOptions, DefaultSweepOptions);
    }
}
