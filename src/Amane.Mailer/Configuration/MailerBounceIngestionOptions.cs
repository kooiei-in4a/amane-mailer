using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Bounce inbox ingestion worker settings (issue #302 / ADR 0020). Default off preserves v1.0.0.
/// </summary>
public sealed record MailerBounceIngestionOptions
{
    public const string EnabledKey = "Mailer:BounceIngestion:Enabled";
    public const string MaxAttemptsKey = "Mailer:BounceIngestion:MaxAttempts";
    public const string LeaseDurationSecondsKey = "Mailer:BounceIngestion:LeaseDurationSeconds";
    public const string InitialDelaySecondsKey = "Mailer:BounceIngestion:InitialDelaySeconds";
    public const string ReconcileBatchSizeKey = "Mailer:BounceIngestion:ReconcileBatchSize";

    public const int DefaultMaxAttempts = 10;
    public const int DefaultLeaseDurationSeconds = 60;
    public const int DefaultInitialDelaySeconds = 10;
    public const int DefaultReconcileBatchSize = 8;

    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 50;
    public const int MinLeaseDurationSeconds = 1;
    public const int MaxLeaseDurationSeconds = 86400;
    public const int MinInitialDelaySeconds = 1;
    public const int MaxInitialDelaySeconds = 86400;
    public const int MinReconcileBatchSize = 1;
    public const int MaxReconcileBatchSize = 100;

    public bool Enabled { get; init; }

    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public int LeaseDurationSeconds { get; init; } = DefaultLeaseDurationSeconds;

    public int InitialDelaySeconds { get; init; } = DefaultInitialDelaySeconds;

    public int ReconcileBatchSize { get; init; } = DefaultReconcileBatchSize;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public static bool IsEnabled(IConfiguration configuration) =>
        ConfigurationBooleanReader.Read(configuration, EnabledKey, defaultValue: false);

    public static MailerBounceIngestionOptions Load(IConfiguration configuration) =>
        Load(configuration, logger: null);

    public static MailerBounceIngestionOptions Load(IConfiguration configuration, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = logger;

        return new()
        {
            Enabled = IsEnabled(configuration),
            MaxAttempts = ConfigurationIntReader.Read(
                configuration,
                MaxAttemptsKey,
                DefaultMaxAttempts,
                MinMaxAttempts,
                MaxMaxAttempts),
            LeaseDurationSeconds = ConfigurationIntReader.Read(
                configuration,
                LeaseDurationSecondsKey,
                DefaultLeaseDurationSeconds,
                MinLeaseDurationSeconds,
                MaxLeaseDurationSeconds),
            InitialDelaySeconds = ConfigurationIntReader.Read(
                configuration,
                InitialDelaySecondsKey,
                DefaultInitialDelaySeconds,
                MinInitialDelaySeconds,
                MaxInitialDelaySeconds),
            ReconcileBatchSize = ConfigurationIntReader.Read(
                configuration,
                ReconcileBatchSizeKey,
                DefaultReconcileBatchSize,
                MinReconcileBatchSize,
                MaxReconcileBatchSize),
        };
    }

    public void Validate()
    {
        if (MaxAttempts < MinMaxAttempts || MaxAttempts > MaxMaxAttempts)
        {
            throw new InvalidOperationException(
                $"{MaxAttemptsKey} must be an integer between {MinMaxAttempts} and {MaxMaxAttempts} (inclusive).");
        }

        if (LeaseDurationSeconds < MinLeaseDurationSeconds || LeaseDurationSeconds > MaxLeaseDurationSeconds)
        {
            throw new InvalidOperationException(
                $"{LeaseDurationSecondsKey} must be an integer between {MinLeaseDurationSeconds} and {MaxLeaseDurationSeconds} (inclusive).");
        }

        if (InitialDelaySeconds < MinInitialDelaySeconds || InitialDelaySeconds > MaxInitialDelaySeconds)
        {
            throw new InvalidOperationException(
                $"{InitialDelaySecondsKey} must be an integer between {MinInitialDelaySeconds} and {MaxInitialDelaySeconds} (inclusive).");
        }

        if (ReconcileBatchSize < MinReconcileBatchSize || ReconcileBatchSize > MaxReconcileBatchSize)
        {
            throw new InvalidOperationException(
                $"{ReconcileBatchSizeKey} must be an integer between {MinReconcileBatchSize} and {MaxReconcileBatchSize} (inclusive).");
        }
    }
}
