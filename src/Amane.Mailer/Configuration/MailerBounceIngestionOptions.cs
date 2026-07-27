using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Configuration;

/// <summary>
/// Bounce inbox ingestion settings (issues #302 / #305 / ADR 0020). Default off preserves v1.0.0.
/// </summary>
public sealed record MailerBounceIngestionOptions
{
    public const string EnabledKey = "Mailer:BounceIngestion:Enabled";
    public const string ModeKey = "Mailer:BounceIngestion:Mode";
    public const string ModeEnvironmentKey = "MAILER_BOUNCE_INGESTION";
    public const string MaxAttemptsKey = "Mailer:BounceIngestion:MaxAttempts";
    public const string LeaseDurationSecondsKey = "Mailer:BounceIngestion:LeaseDurationSeconds";
    public const string InitialDelaySecondsKey = "Mailer:BounceIngestion:InitialDelaySeconds";
    public const string ReconcileBatchSizeKey = "Mailer:BounceIngestion:ReconcileBatchSize";
    public const string QueueConnectionStringKey = "Mailer:BounceIngestion:Queue:ConnectionString";
    public const string QueueConnectionStringEnvironmentKey = "MAILER_BOUNCE_QUEUE_CONNECTION_STRING";
    public const string QueueConnectionStringFileKey = "MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE";
    public const string QueueNameKey = "Mailer:BounceIngestion:Queue:Name";
    public const string QueueNameEnvironmentKey = "MAILER_BOUNCE_QUEUE_NAME";
    public const string QueuePollIntervalSecondsKey = "Mailer:BounceIngestion:Queue:PollIntervalSeconds";
    public const string QueueBatchSizeKey = "Mailer:BounceIngestion:Queue:BatchSize";
    public const string QueueVisibilityTimeoutSecondsKey = "Mailer:BounceIngestion:Queue:VisibilityTimeoutSeconds";

    public const int DefaultMaxAttempts = 10;
    public const int DefaultLeaseDurationSeconds = 60;
    public const int DefaultInitialDelaySeconds = 10;
    public const int DefaultReconcileBatchSize = 8;
    public const int DefaultQueuePollIntervalSeconds = 30;
    public const int DefaultQueueBatchSize = 16;
    public const int DefaultQueueVisibilityTimeoutSeconds = 60;

    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 50;
    public const int MinLeaseDurationSeconds = 1;
    public const int MaxLeaseDurationSeconds = 86400;
    public const int MinInitialDelaySeconds = 1;
    public const int MaxInitialDelaySeconds = 86400;
    public const int MinReconcileBatchSize = 1;
    public const int MaxReconcileBatchSize = 100;
    public const int MinQueuePollIntervalSeconds = 1;
    public const int MaxQueuePollIntervalSeconds = 3600;
    public const int MinQueueBatchSize = 1;
    public const int MaxQueueBatchSize = 32;
    public const int MinQueueVisibilityTimeoutSeconds = 1;
    public const int MaxQueueVisibilityTimeoutSeconds = 3600;

    public const string ProviderAcs = "acs";

    /// <summary>
    /// Legacy boolean gate from #302. Prefer <see cref="Mode"/> for new deployments.
    /// Worker/sweep register when Mode is Queue/Webhook or this flag is true.
    /// </summary>
    public bool Enabled { get; init; }

    public BounceIngestionMode Mode { get; init; } = BounceIngestionMode.Off;

    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    public int LeaseDurationSeconds { get; init; } = DefaultLeaseDurationSeconds;

    public int InitialDelaySeconds { get; init; } = DefaultInitialDelaySeconds;

    public int ReconcileBatchSize { get; init; } = DefaultReconcileBatchSize;

    /// <summary>Storage Queue connection string. Never log or serialize to metrics.</summary>
    public string QueueConnectionString { get; init; } = string.Empty;

    public string QueueName { get; init; } = string.Empty;

    public int QueuePollIntervalSeconds { get; init; } = DefaultQueuePollIntervalSeconds;

    public int QueueBatchSize { get; init; } = DefaultQueueBatchSize;

    public int QueueVisibilityTimeoutSeconds { get; init; } = DefaultQueueVisibilityTimeoutSeconds;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseDurationSeconds);

    public TimeSpan QueuePollInterval => TimeSpan.FromSeconds(QueuePollIntervalSeconds);

    public TimeSpan QueueVisibilityTimeout => TimeSpan.FromSeconds(QueueVisibilityTimeoutSeconds);

    public bool IsProcessingEnabled =>
        Mode is BounceIngestionMode.Queue or BounceIngestionMode.Webhook || Enabled;

    public bool IsQueuePollingEnabled => Mode == BounceIngestionMode.Queue;

    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = Load(configuration);
        return options.IsProcessingEnabled;
    }

    public static bool IsQueuePollingConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Load(configuration).IsQueuePollingEnabled;
    }

    public static MailerBounceIngestionOptions Load(IConfiguration configuration) =>
        Load(configuration, logger: null);

    public static MailerBounceIngestionOptions Load(IConfiguration configuration, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ = logger;

        return new()
        {
            Enabled = ConfigurationBooleanReader.Read(configuration, EnabledKey, defaultValue: false),
            Mode = ParseMode(ReadModeRaw(configuration)),
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
            QueueConnectionString = ResolveQueueConnectionString(configuration),
            QueueName = ReadFirstNonEmpty(
                configuration,
                QueueNameEnvironmentKey,
                QueueNameKey) ?? string.Empty,
            QueuePollIntervalSeconds = ConfigurationIntReader.Read(
                configuration,
                QueuePollIntervalSecondsKey,
                DefaultQueuePollIntervalSeconds,
                MinQueuePollIntervalSeconds,
                MaxQueuePollIntervalSeconds),
            QueueBatchSize = ConfigurationIntReader.Read(
                configuration,
                QueueBatchSizeKey,
                DefaultQueueBatchSize,
                MinQueueBatchSize,
                MaxQueueBatchSize),
            QueueVisibilityTimeoutSeconds = ConfigurationIntReader.Read(
                configuration,
                QueueVisibilityTimeoutSecondsKey,
                DefaultQueueVisibilityTimeoutSeconds,
                MinQueueVisibilityTimeoutSeconds,
                MaxQueueVisibilityTimeoutSeconds),
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

        if (QueuePollIntervalSeconds < MinQueuePollIntervalSeconds
            || QueuePollIntervalSeconds > MaxQueuePollIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"{QueuePollIntervalSecondsKey} must be an integer between {MinQueuePollIntervalSeconds} and {MaxQueuePollIntervalSeconds} (inclusive).");
        }

        if (QueueBatchSize < MinQueueBatchSize || QueueBatchSize > MaxQueueBatchSize)
        {
            throw new InvalidOperationException(
                $"{QueueBatchSizeKey} must be an integer between {MinQueueBatchSize} and {MaxQueueBatchSize} (inclusive).");
        }

        if (QueueVisibilityTimeoutSeconds < MinQueueVisibilityTimeoutSeconds
            || QueueVisibilityTimeoutSeconds > MaxQueueVisibilityTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"{QueueVisibilityTimeoutSecondsKey} must be an integer between {MinQueueVisibilityTimeoutSeconds} and {MaxQueueVisibilityTimeoutSeconds} (inclusive).");
        }

        if (Mode == BounceIngestionMode.Webhook)
        {
            throw new InvalidOperationException(
                $"{ModeKey}='webhook' is not implemented in this build (reserved for issue #304). Use 'queue' or 'off'.");
        }

        if (Mode != BounceIngestionMode.Queue)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(QueueConnectionString))
        {
            throw new InvalidOperationException(
                $"{ModeKey}='queue' requires {QueueConnectionStringEnvironmentKey} "
                + $"(or {QueueConnectionStringFileKey} / {QueueConnectionStringKey}).");
        }

        if (string.IsNullOrWhiteSpace(QueueName))
        {
            throw new InvalidOperationException(
                $"{ModeKey}='queue' requires {QueueNameEnvironmentKey} (or {QueueNameKey}).");
        }
    }

    internal static BounceIngestionMode ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BounceIngestionMode.Off;
        }

        if (raw.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return BounceIngestionMode.Off;
        }

        if (raw.Equals("queue", StringComparison.OrdinalIgnoreCase))
        {
            return BounceIngestionMode.Queue;
        }

        if (raw.Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return BounceIngestionMode.Webhook;
        }

        // Never echo the raw value: operators may paste connection strings or secrets by mistake.
        throw new InvalidOperationException(
            $"{ModeKey} must be one of: unset/empty, 'off', 'queue', 'webhook'.");
    }

    private static string? ReadModeRaw(IConfiguration configuration) =>
        // Blank / whitespace env values are treated as "not supplied" and fall through to
        // Mailer:BounceIngestion:Mode (they do not force Off over a configured appsetting).
        ReadFirstNonEmpty(configuration, ModeEnvironmentKey, ModeKey);

    private static string ResolveQueueConnectionString(IConfiguration configuration)
    {
        var filePath = configuration[QueueConnectionStringFileKey];
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            return File.ReadAllText(filePath).Trim();
        }

        return ReadFirstNonEmpty(
                configuration,
                QueueConnectionStringEnvironmentKey,
                QueueConnectionStringKey)
            ?? string.Empty;
    }

    private static string? ReadFirstNonEmpty(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

public enum BounceIngestionMode
{
    Off = 0,
    Queue = 1,
    Webhook = 2,
}
