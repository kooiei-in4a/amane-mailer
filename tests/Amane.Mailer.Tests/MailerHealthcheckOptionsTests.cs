using Amane.Mailer.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerHealthcheckOptionsTests
{
    private static readonly MailerSweepOptions DefaultSweepOptions = new();

    [Fact]
    public void validate_rejects_staleness_below_drain_wave_floor()
    {
        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = 100,
        };
        var workerOptions = new MailerWorkerOptions
        {
            SendTimeoutSeconds = 90,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void validate_rejects_staleness_below_multi_wave_floor()
    {
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 10,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 600,
        };
        var sendWaves = (10 + 4 - 1) / 4; // = 3
        var floor = (sendWaves * 90) + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = floor - 1,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void validate_accepts_staleness_at_multi_wave_floor()
    {
        var workerOptions = new MailerWorkerOptions
        {
            BatchClaimSize = 10,
            MaxSendConcurrency = 4,
            SendTimeoutSeconds = 90,
            LeaseDurationSeconds = 600,
        };
        var sendWaves = (10 + 4 - 1) / 4;
        var floor = (sendWaves * 90) + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = floor,
        };

        healthcheckOptions.Validate(workerOptions, DefaultSweepOptions);
    }

    [Fact]
    public void validate_rejects_staleness_not_greater_than_worker_heartbeat_interval()
    {
        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = 60,
            WorkerHeartbeatIntervalSeconds = 60,
        };
        var workerOptions = new MailerWorkerOptions
        {
            SendTimeoutSeconds = 10,
            LeaseDurationSeconds = 60,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, DefaultSweepOptions));
    }

    [Fact]
    public void validate_rejects_staleness_not_greater_than_sweep_interval()
    {
        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = 300,
        };
        var workerOptions = new MailerWorkerOptions
        {
            SendTimeoutSeconds = 10,
            LeaseDurationSeconds = 60,
        };
        var sweepOptions = new MailerSweepOptions
        {
            IntervalSeconds = 600,
        };

        Assert.Throws<InvalidOperationException>(
            () => healthcheckOptions.Validate(workerOptions, sweepOptions));
    }

    [Fact]
    public void validate_accepts_staleness_above_floor()
    {
        var sendWaves = (MailerWorkerOptions.DefaultBatchClaimSize + MailerWorkerOptions.DefaultMaxSendConcurrency - 1)
            / MailerWorkerOptions.DefaultMaxSendConcurrency;
        var minimumStaleness = (sendWaves * MailerWorkerOptions.DefaultSendTimeoutSeconds)
            + MailerWorkerOptions.FinalizeTimeoutSeconds + 30;

        var healthcheckOptions = new MailerHealthcheckOptions
        {
            MaxHeartbeatStalenessSeconds = minimumStaleness,
        };
        var workerOptions = new MailerWorkerOptions();

        healthcheckOptions.Validate(workerOptions, DefaultSweepOptions);
    }

    [Fact]
    public void default_options_pass_validation()
    {
        var healthcheckOptions = new MailerHealthcheckOptions();
        var workerOptions = new MailerWorkerOptions();

        healthcheckOptions.Validate(workerOptions, DefaultSweepOptions);
    }
}
