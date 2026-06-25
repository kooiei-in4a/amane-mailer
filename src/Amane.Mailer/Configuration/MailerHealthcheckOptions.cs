namespace Amane.Mailer.Configuration;

public sealed record MailerHealthcheckOptions
{
    public const int DefaultMaxHeartbeatStalenessSeconds = 300;
    public const int DefaultWorkerHeartbeatIntervalSeconds = 60;

    public int MaxHeartbeatStalenessSeconds { get; init; } = DefaultMaxHeartbeatStalenessSeconds;

    public int WorkerHeartbeatIntervalSeconds { get; init; } = DefaultWorkerHeartbeatIntervalSeconds;

    public TimeSpan MaxHeartbeatStaleness => TimeSpan.FromSeconds(MaxHeartbeatStalenessSeconds);

    public TimeSpan WorkerHeartbeatInterval => TimeSpan.FromSeconds(WorkerHeartbeatIntervalSeconds);

    public static MailerHealthcheckOptions Load(IConfiguration configuration) =>
        new()
        {
            MaxHeartbeatStalenessSeconds = Math.Max(
                30,
                configuration.GetValue("Mailer:Healthcheck:MaxHeartbeatStalenessSeconds", DefaultMaxHeartbeatStalenessSeconds)),
            WorkerHeartbeatIntervalSeconds = Math.Max(
                10,
                configuration.GetValue("Mailer:Healthcheck:WorkerHeartbeatIntervalSeconds", DefaultWorkerHeartbeatIntervalSeconds)),
        };

    public void Validate(MailerWorkerOptions workerOptions, MailerSweepOptions sweepOptions)
    {
        var sendWaves = (workerOptions.BatchClaimSize + workerOptions.MaxSendConcurrency - 1)
            / workerOptions.MaxSendConcurrency;
        var minimumForDrain = checked((sendWaves * workerOptions.SendTimeoutSeconds)
            + MailerWorkerOptions.FinalizeTimeoutSeconds + 30);
        if (MaxHeartbeatStalenessSeconds < minimumForDrain)
        {
            throw new InvalidOperationException(
                $"Mailer:Healthcheck:MaxHeartbeatStalenessSeconds ({MaxHeartbeatStalenessSeconds}) must be at least "
                + $"ceil(BatchClaimSize / MaxSendConcurrency) * SendTimeoutSeconds + FinalizeTimeoutSeconds + 30 ({minimumForDrain}) "
                + "to avoid stale false-positives during legitimate long sends.");
        }

        if (MaxHeartbeatStalenessSeconds <= WorkerHeartbeatIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Healthcheck:MaxHeartbeatStalenessSeconds ({MaxHeartbeatStalenessSeconds}) must be greater than "
                + $"WorkerHeartbeatIntervalSeconds ({WorkerHeartbeatIntervalSeconds}) "
                + "to avoid false unhealthy during normal idle.");
        }

        if (MaxHeartbeatStalenessSeconds <= sweepOptions.IntervalSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Healthcheck:MaxHeartbeatStalenessSeconds ({MaxHeartbeatStalenessSeconds}) must be greater than "
                + $"Mailer:Sweep:IntervalSeconds ({sweepOptions.IntervalSeconds}) "
                + "to avoid false unhealthy during normal sweep interval.");
        }
    }
}
