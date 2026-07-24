namespace Amane.Mailer.Configuration;

public sealed record MailerAdminAuditRetentionOptions
{
    public const int DefaultRetentionDays = 180;
    public const int MinRetentionDaysNonLocal = 30;
    public const int DefaultSweepIntervalHours = 24;
    public const int DefaultBatchSize = 100;

    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const int MinSweepIntervalHours = 1;
    public const int MaxSweepIntervalHours = 168;
    public const int MinSweepIntervalSeconds = 1;
    public const int MaxSweepIntervalSeconds = 604800;
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 1000;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public int SweepIntervalHours { get; init; } = DefaultSweepIntervalHours;

    public int? SweepIntervalSeconds { get; init; }

    public int BatchSize { get; init; } = DefaultBatchSize;

    public bool IsLocalDevelopment { get; init; }

    public TimeSpan SweepInterval =>
        SweepIntervalSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(SweepIntervalHours);

    public static MailerAdminAuditRetentionOptions Load(IConfiguration configuration) =>
        new()
        {
            RetentionDays = ReadRetentionDays(configuration),
            SweepIntervalHours = ReadPositiveInt(
                configuration,
                DefaultSweepIntervalHours,
                MinSweepIntervalHours,
                MaxSweepIntervalHours,
                "AMANE_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_HOURS",
                "MAILER_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_HOURS"),
            SweepIntervalSeconds = ReadOptionalPositiveInt(
                configuration,
                MinSweepIntervalSeconds,
                MaxSweepIntervalSeconds,
                "AMANE_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_SECONDS",
                "MAILER_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_SECONDS"),
            BatchSize = ReadPositiveInt(
                configuration,
                DefaultBatchSize,
                MinBatchSize,
                MaxBatchSize,
                "AMANE_ADMIN_AUDIT_RETENTION_BATCH_SIZE",
                "MAILER_ADMIN_AUDIT_RETENTION_BATCH_SIZE"),
            IsLocalDevelopment = IsLocalDevelopmentEnvironment(configuration),
        };

    public void Validate()
    {
        if (RetentionDays < MinRetentionDays || RetentionDays > MaxRetentionDays)
        {
            throw new InvalidOperationException(
                $"MAILER_ADMIN_AUDIT_RETENTION_DAYS must be an integer between {MinRetentionDays} and {MaxRetentionDays} (inclusive).");
        }

        if (!IsLocalDevelopment && RetentionDays < MinRetentionDaysNonLocal)
        {
            throw new InvalidOperationException(
                $"MAILER_ADMIN_AUDIT_RETENTION_DAYS must be at least {MinRetentionDaysNonLocal} outside local development.");
        }
    }

    public void ValidatePurgeDays(int olderThanDays)
    {
        if (olderThanDays < 1)
        {
            throw new InvalidOperationException("--older-than-days must be greater than zero.");
        }

        if (!IsLocalDevelopment && olderThanDays < MinRetentionDaysNonLocal)
        {
            throw new InvalidOperationException(
                $"--older-than-days must be at least {MinRetentionDaysNonLocal} outside local development.");
        }
    }

    private static int ReadRetentionDays(IConfiguration configuration)
    {
        var value = configuration["MAILER_ADMIN_AUDIT_RETENTION_DAYS"];
        var key = "MAILER_ADMIN_AUDIT_RETENTION_DAYS";
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["AMANE_ADMIN_AUDIT_RETENTION_DAYS"];
            key = "AMANE_ADMIN_AUDIT_RETENTION_DAYS";
        }

        if (string.IsNullOrWhiteSpace(value))
            return DefaultRetentionDays;

        return ConfigurationIntReader.ParseInRange(value, key, MinRetentionDays, MaxRetentionDays);
    }

    private static int ReadPositiveInt(
        IConfiguration configuration,
        int defaultValue,
        int minInclusive,
        int maxInclusive,
        string primaryKey,
        string fallbackKey)
    {
        var value = configuration[primaryKey];
        var key = primaryKey;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[fallbackKey];
            key = fallbackKey;
        }

        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return ConfigurationIntReader.ParseInRange(value, key, minInclusive, maxInclusive);
    }

    private static int? ReadOptionalPositiveInt(
        IConfiguration configuration,
        int minInclusive,
        int maxInclusive,
        string primaryKey,
        string fallbackKey)
    {
        var value = configuration[primaryKey];
        var key = primaryKey;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[fallbackKey];
            key = fallbackKey;
        }

        if (string.IsNullOrWhiteSpace(value))
            return null;

        return ConfigurationIntReader.ParseInRange(value, key, minInclusive, maxInclusive);
    }

    internal static bool IsLocalDevelopmentEnvironment(IConfiguration configuration) =>
        string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"],
            "Development",
            StringComparison.OrdinalIgnoreCase);
}
