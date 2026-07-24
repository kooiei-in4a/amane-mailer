namespace Amane.Mailer.Configuration;

public sealed record MailerWebhookOptions
{
    public const int DefaultMaxAttempts = 10;
    public const int DefaultInitialDelaySeconds = 10;
    public const int DefaultMaxDelaySeconds = 300;
    public const int DefaultBatchClaimSize = 8;
    public const int DefaultDeliveryTimeoutSeconds = 30;
    public const int DefaultLeaseDurationSeconds = 60;
    public const int FinalizeTimeoutSeconds = 10;
    public const int HostShutdownSlackSeconds = MailerWorkerOptions.HostShutdownSlackSeconds;

    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 50;
    public const int MinInitialDelaySeconds = 1;
    public const int MaxInitialDelaySeconds = 86400;
    public const int MinMaxDelaySeconds = 1;
    public const int MaxMaxDelaySeconds = 86400;
    public const int MinBatchClaimSize = 1;
    public const int MaxBatchClaimSize = 100;
    public const int MinDeliveryTimeoutSeconds = 1;
    public const int MaxDeliveryTimeoutSeconds = 600;
    public const int MinLeaseDurationSeconds = 1;
    public const int MaxLeaseDurationSeconds = 86400;

    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public int InitialDelaySeconds { get; init; } = DefaultInitialDelaySeconds;

    public int MaxDelaySeconds { get; init; } = DefaultMaxDelaySeconds;

    public int BatchClaimSize { get; init; } = DefaultBatchClaimSize;

    public int DeliveryTimeoutSeconds { get; init; } = DefaultDeliveryTimeoutSeconds;

    public int LeaseDurationSeconds { get; init; } = DefaultLeaseDurationSeconds;

    public TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(DeliveryTimeoutSeconds);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan FinalizeTimeout => TimeSpan.FromSeconds(FinalizeTimeoutSeconds);

    public TimeSpan ShutdownDrainTimeout => DeliveryTimeout + FinalizeTimeout;

    public TimeSpan HostShutdownTimeout =>
        ShutdownDrainTimeout + TimeSpan.FromSeconds(HostShutdownSlackSeconds);

    public static MailerWebhookOptions Load(IConfiguration configuration) =>
        new()
        {
            MaxAttempts = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:MaxAttempts",
                DefaultMaxAttempts,
                MinMaxAttempts,
                MaxMaxAttempts),
            InitialDelaySeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:InitialDelaySeconds",
                DefaultInitialDelaySeconds,
                MinInitialDelaySeconds,
                MaxInitialDelaySeconds),
            MaxDelaySeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:MaxDelaySeconds",
                DefaultMaxDelaySeconds,
                MinMaxDelaySeconds,
                MaxMaxDelaySeconds),
            BatchClaimSize = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:BatchClaimSize",
                DefaultBatchClaimSize,
                MinBatchClaimSize,
                MaxBatchClaimSize),
            DeliveryTimeoutSeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:DeliveryTimeoutSeconds",
                DefaultDeliveryTimeoutSeconds,
                MinDeliveryTimeoutSeconds,
                MaxDeliveryTimeoutSeconds),
            LeaseDurationSeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Webhook:LeaseDurationSeconds",
                DefaultLeaseDurationSeconds,
                MinLeaseDurationSeconds,
                MaxLeaseDurationSeconds),
        };

    public void Validate()
    {
        if (MaxAttempts < MinMaxAttempts || MaxAttempts > MaxMaxAttempts)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:MaxAttempts must be an integer between {MinMaxAttempts} and {MaxMaxAttempts} (inclusive).");
        }

        if (InitialDelaySeconds < MinInitialDelaySeconds || InitialDelaySeconds > MaxInitialDelaySeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:InitialDelaySeconds must be an integer between {MinInitialDelaySeconds} and {MaxInitialDelaySeconds} (inclusive).");
        }

        if (MaxDelaySeconds < MinMaxDelaySeconds || MaxDelaySeconds > MaxMaxDelaySeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:MaxDelaySeconds must be an integer between {MinMaxDelaySeconds} and {MaxMaxDelaySeconds} (inclusive).");
        }

        if (InitialDelaySeconds > MaxDelaySeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:InitialDelaySeconds ({InitialDelaySeconds}) must be less than or equal to "
                + $"Mailer:Webhook:MaxDelaySeconds ({MaxDelaySeconds}).");
        }

        if (BatchClaimSize < MinBatchClaimSize || BatchClaimSize > MaxBatchClaimSize)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:BatchClaimSize must be an integer between {MinBatchClaimSize} and {MaxBatchClaimSize} (inclusive).");
        }

        if (DeliveryTimeoutSeconds < MinDeliveryTimeoutSeconds || DeliveryTimeoutSeconds > MaxDeliveryTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:DeliveryTimeoutSeconds must be an integer between {MinDeliveryTimeoutSeconds} and {MaxDeliveryTimeoutSeconds} (inclusive).");
        }

        if (LeaseDurationSeconds < MinLeaseDurationSeconds || LeaseDurationSeconds > MaxLeaseDurationSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:LeaseDurationSeconds must be an integer between {MinLeaseDurationSeconds} and {MaxLeaseDurationSeconds} (inclusive).");
        }

        if (LeaseDurationSeconds <= DeliveryTimeoutSeconds + FinalizeTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Mailer:Webhook:LeaseDurationSeconds ({LeaseDurationSeconds}) must be greater than "
                + $"DeliveryTimeoutSeconds + {FinalizeTimeoutSeconds} "
                + $"({DeliveryTimeoutSeconds + FinalizeTimeoutSeconds}).");
        }
    }

    public DateTimeOffset ComputeNextAttemptAt(int attemptCount, DateTimeOffset completedAt)
    {
        var delaySeconds = Math.Min(
            MaxDelaySeconds,
            InitialDelaySeconds * Math.Pow(2, Math.Max(0, attemptCount - 1)));
        return completedAt.AddSeconds(delaySeconds);
    }
}
