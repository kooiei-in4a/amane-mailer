namespace Amane.Mailer.Configuration;

public sealed record MailerRetentionOptions
{
    public const int DefaultRetentionDays = 90;
    public const int DefaultSweepIntervalHours = 24;
    public const int DefaultBatchSize = 100;
    // Caps bind variables for multi-row delivery_events DELETE (3 params/row) under classic SQLite limits.
    public const int MaxBatchSize = 250;

    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const int MinSweepIntervalHours = 1;
    public const int MaxSweepIntervalHours = 168;
    public const int MinSweepIntervalSeconds = 1;
    public const int MaxSweepIntervalSeconds = 604800;
    public const int MinBatchSize = 1;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public int SweepIntervalHours { get; init; } = DefaultSweepIntervalHours;

    public int? SweepIntervalSeconds { get; init; }

    public int BatchSize { get; init; } = DefaultBatchSize;

    public TimeSpan SweepInterval =>
        SweepIntervalSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(SweepIntervalHours);

    public static MailerRetentionOptions Load(IConfiguration configuration) =>
        new()
        {
            RetentionDays = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Retention:Days",
                DefaultRetentionDays,
                MinRetentionDays,
                MaxRetentionDays),
            SweepIntervalHours = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Retention:SweepIntervalHours",
                DefaultSweepIntervalHours,
                MinSweepIntervalHours,
                MaxSweepIntervalHours),
            SweepIntervalSeconds = ConfigurationIntReader.ReadOptional(
                configuration,
                "Mailer:Retention:SweepIntervalSeconds",
                MinSweepIntervalSeconds,
                MaxSweepIntervalSeconds),
            BatchSize = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Retention:BatchSize",
                DefaultBatchSize,
                MinBatchSize,
                MaxBatchSize),
        };
}
