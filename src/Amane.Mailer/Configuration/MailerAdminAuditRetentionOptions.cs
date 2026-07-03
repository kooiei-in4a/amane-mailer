namespace Amane.Mailer.Configuration;

public sealed record MailerAdminAuditRetentionOptions
{
    public const int DefaultRetentionDays = 180;
    public const int MinRetentionDaysNonLocal = 30;
    public const int DefaultSweepIntervalHours = 24;
    public const int DefaultBatchSize = 100;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public int SweepIntervalHours { get; init; } = DefaultSweepIntervalHours;

    public int? SweepIntervalSeconds { get; init; }

    public int BatchSize { get; init; } = DefaultBatchSize;

    public bool IsLocalDevelopment { get; init; }

    public TimeSpan SweepInterval =>
        SweepIntervalSeconds is int seconds
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : TimeSpan.FromHours(SweepIntervalHours);

    public static MailerAdminAuditRetentionOptions Load(IConfiguration configuration) =>
        new()
        {
            RetentionDays = ReadRetentionDays(configuration),
            SweepIntervalHours = Math.Max(
                1,
                ReadPositiveInt(
                    configuration,
                    DefaultSweepIntervalHours,
                    "AMANE_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_HOURS",
                    "MAILER_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_HOURS")),
            SweepIntervalSeconds = ReadOptionalPositiveInt(
                configuration,
                "AMANE_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_SECONDS",
                "MAILER_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_SECONDS"),
            BatchSize = Math.Max(
                1,
                ReadPositiveInt(
                    configuration,
                    DefaultBatchSize,
                    "AMANE_ADMIN_AUDIT_RETENTION_BATCH_SIZE",
                    "MAILER_ADMIN_AUDIT_RETENTION_BATCH_SIZE")),
            IsLocalDevelopment = IsLocalDevelopmentEnvironment(configuration),
        };

    public void Validate()
    {
        if (RetentionDays < 1)
        {
            throw new InvalidOperationException(
                "MAILER_ADMIN_AUDIT_RETENTION_DAYS must be greater than zero.");
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
        if (string.IsNullOrWhiteSpace(value))
            value = configuration["AMANE_ADMIN_AUDIT_RETENTION_DAYS"];

        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : DefaultRetentionDays;
    }

    private static int ReadPositiveInt(
        IConfiguration configuration,
        int defaultValue,
        string primaryKey,
        string fallbackKey)
    {
        var value = configuration[primaryKey];
        if (string.IsNullOrWhiteSpace(value))
            value = configuration[fallbackKey];

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static int? ReadOptionalPositiveInt(
        IConfiguration configuration,
        string primaryKey,
        string fallbackKey)
    {
        var value = configuration[primaryKey];
        if (string.IsNullOrWhiteSpace(value))
            value = configuration[fallbackKey];

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    internal static bool IsLocalDevelopmentEnvironment(IConfiguration configuration) =>
        string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"],
            "Development",
            StringComparison.OrdinalIgnoreCase);
}
