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
            MaxAttempts = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:MaxAttempts", DefaultMaxAttempts)),
            InitialDelaySeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:InitialDelaySeconds", DefaultInitialDelaySeconds)),
            MaxDelaySeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:MaxDelaySeconds", DefaultMaxDelaySeconds)),
            BatchClaimSize = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:BatchClaimSize", DefaultBatchClaimSize)),
            DeliveryTimeoutSeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:DeliveryTimeoutSeconds", DefaultDeliveryTimeoutSeconds)),
            LeaseDurationSeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Webhook:LeaseDurationSeconds", DefaultLeaseDurationSeconds)),
        };

    public void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException("Mailer:Webhook:MaxAttempts must be at least 1.");
        }

        if (InitialDelaySeconds < 1)
        {
            throw new InvalidOperationException("Mailer:Webhook:InitialDelaySeconds must be at least 1.");
        }

        if (MaxDelaySeconds < 1)
        {
            throw new InvalidOperationException("Mailer:Webhook:MaxDelaySeconds must be at least 1.");
        }

        if (BatchClaimSize < 1)
        {
            throw new InvalidOperationException("Mailer:Webhook:BatchClaimSize must be at least 1.");
        }

        if (DeliveryTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Mailer:Webhook:DeliveryTimeoutSeconds must be at least 1.");
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
