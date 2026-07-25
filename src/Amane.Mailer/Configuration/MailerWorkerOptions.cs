namespace Amane.Mailer.Configuration;

public sealed record MailerWorkerOptions
{
    public const int DefaultBatchClaimSize = 4;
    public const int DefaultMaxSendConcurrency = 4;
    public const int DefaultSendTimeoutSeconds = 90;
    public const int DefaultLeaseDurationSeconds = 120;
    public const int FinalizeTimeoutSeconds = 10;
    public const int HostShutdownSlackSeconds = 15;

    public const int MinBatchClaimSize = 1;
    public const int MaxBatchClaimSize = 100;
    public const int MinMaxSendConcurrency = 1;
    public const int MaxMaxSendConcurrency = 64;
    public const int MinSendTimeoutSeconds = 1;
    public const int MaxSendTimeoutSeconds = 600;
    public const int MinLeaseDurationSeconds = 1;
    public const int MaxLeaseDurationSeconds = 86400;

    public int BatchClaimSize { get; init; } = DefaultBatchClaimSize;

    public int MaxSendConcurrency { get; init; } = DefaultMaxSendConcurrency;

    public int SendTimeoutSeconds { get; init; } = DefaultSendTimeoutSeconds;

    public int LeaseDurationSeconds { get; init; } = DefaultLeaseDurationSeconds;

    public TimeSpan SendTimeout => TimeSpan.FromSeconds(SendTimeoutSeconds);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan FinalizeTimeout => TimeSpan.FromSeconds(FinalizeTimeoutSeconds);

    public TimeSpan ShutdownDrainTimeout => SendTimeout + FinalizeTimeout;

    public TimeSpan HostShutdownTimeout => ShutdownDrainTimeout + TimeSpan.FromSeconds(HostShutdownSlackSeconds);

    public static bool IsEnabled(IConfiguration configuration) =>
        ConfigurationBooleanReader.Read(configuration, "Mailer:Worker:Enabled", true);

    public static MailerWorkerOptions Load(IConfiguration configuration) =>
        new()
        {
            BatchClaimSize = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Worker:BatchClaimSize",
                DefaultBatchClaimSize,
                MinBatchClaimSize,
                MaxBatchClaimSize),
            MaxSendConcurrency = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Worker:MaxSendConcurrency",
                DefaultMaxSendConcurrency,
                MinMaxSendConcurrency,
                MaxMaxSendConcurrency),
            SendTimeoutSeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Worker:SendTimeoutSeconds",
                DefaultSendTimeoutSeconds,
                MinSendTimeoutSeconds,
                MaxSendTimeoutSeconds),
            LeaseDurationSeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Worker:LeaseDurationSeconds",
                DefaultLeaseDurationSeconds,
                MinLeaseDurationSeconds,
                MaxLeaseDurationSeconds),
        };

    public void Validate()
    {
        if (BatchClaimSize < MinBatchClaimSize || BatchClaimSize > MaxBatchClaimSize)
        {
            throw new InvalidOperationException(
                $"Mailer:Worker:BatchClaimSize must be an integer between {MinBatchClaimSize} and {MaxBatchClaimSize} (inclusive).");
        }

        if (MaxSendConcurrency < MinMaxSendConcurrency || MaxSendConcurrency > MaxMaxSendConcurrency)
        {
            throw new InvalidOperationException(
                $"Mailer:Worker:MaxSendConcurrency must be an integer between {MinMaxSendConcurrency} and {MaxMaxSendConcurrency} (inclusive).");
        }

        if (SendTimeoutSeconds < MinSendTimeoutSeconds || SendTimeoutSeconds > MaxSendTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Worker:SendTimeoutSeconds must be an integer between {MinSendTimeoutSeconds} and {MaxSendTimeoutSeconds} (inclusive).");
        }

        if (LeaseDurationSeconds < MinLeaseDurationSeconds || LeaseDurationSeconds > MaxLeaseDurationSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Worker:LeaseDurationSeconds must be an integer between {MinLeaseDurationSeconds} and {MaxLeaseDurationSeconds} (inclusive).");
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
