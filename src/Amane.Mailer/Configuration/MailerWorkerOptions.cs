namespace Amane.Mailer.Configuration;

public sealed record MailerWorkerOptions
{
    public const int DefaultBatchClaimSize = 4;
    public const int DefaultMaxSendConcurrency = 4;
    public const int DefaultSendTimeoutSeconds = 90;
    public const int DefaultLeaseDurationSeconds = 120;
    public const int FinalizeTimeoutSeconds = 10;
    public const int HostShutdownSlackSeconds = 15;

    public int BatchClaimSize { get; init; } = DefaultBatchClaimSize;

    public int MaxSendConcurrency { get; init; } = DefaultMaxSendConcurrency;

    public int SendTimeoutSeconds { get; init; } = DefaultSendTimeoutSeconds;

    public int LeaseDurationSeconds { get; init; } = DefaultLeaseDurationSeconds;

    public TimeSpan SendTimeout => TimeSpan.FromSeconds(SendTimeoutSeconds);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan FinalizeTimeout => TimeSpan.FromSeconds(FinalizeTimeoutSeconds);

    public TimeSpan ShutdownDrainTimeout => SendTimeout + FinalizeTimeout;

    public TimeSpan HostShutdownTimeout => ShutdownDrainTimeout + TimeSpan.FromSeconds(HostShutdownSlackSeconds);

    public static MailerWorkerOptions Load(IConfiguration configuration) =>
        new()
        {
            BatchClaimSize = Math.Max(
                1,
                configuration.GetValue("Mailer:Worker:BatchClaimSize", DefaultBatchClaimSize)),
            MaxSendConcurrency = Math.Max(
                1,
                configuration.GetValue("Mailer:Worker:MaxSendConcurrency", DefaultMaxSendConcurrency)),
            SendTimeoutSeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Worker:SendTimeoutSeconds", DefaultSendTimeoutSeconds)),
            LeaseDurationSeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Worker:LeaseDurationSeconds", DefaultLeaseDurationSeconds)),
        };

    public void Validate()
    {
        if (BatchClaimSize < 1)
        {
            throw new InvalidOperationException("Mailer:Worker:BatchClaimSize must be at least 1.");
        }

        if (MaxSendConcurrency < 1)
        {
            throw new InvalidOperationException("Mailer:Worker:MaxSendConcurrency must be at least 1.");
        }

        if (SendTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Mailer:Worker:SendTimeoutSeconds must be at least 1.");
        }

        var sendWaves = (BatchClaimSize + MaxSendConcurrency - 1) / MaxSendConcurrency;
        var minimumLeaseSeconds = checked((sendWaves * SendTimeoutSeconds) + FinalizeTimeoutSeconds);
        if (LeaseDurationSeconds <= minimumLeaseSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Worker:LeaseDurationSeconds ({LeaseDurationSeconds}) must be greater than "
                + $"ceil(BatchClaimSize / MaxSendConcurrency) * SendTimeoutSeconds + {FinalizeTimeoutSeconds} "
                + $"({minimumLeaseSeconds}) to avoid expiring claimed mail before finalize.");
        }
    }
}
