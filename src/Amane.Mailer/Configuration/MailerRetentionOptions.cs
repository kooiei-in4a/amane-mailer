namespace Amane.Mailer.Configuration;

public sealed record MailerRetentionOptions
{
    public const int DefaultRetentionDays = 90;
    public const int DefaultSweepIntervalHours = 24;
    public const int DefaultBatchSize = 100;
    // Caps bind variables for multi-row delivery_events DELETE (3 params/row) under classic SQLite limits.
    public const int MaxBatchSize = 250;

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    public int SweepIntervalHours { get; init; } = DefaultSweepIntervalHours;

    public int? SweepIntervalSeconds { get; init; }

    public int BatchSize { get; init; } = DefaultBatchSize;

    public TimeSpan SweepInterval =>
        SweepIntervalSeconds is int seconds
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : TimeSpan.FromHours(SweepIntervalHours);

    public static MailerRetentionOptions Load(IConfiguration configuration) =>
        new()
        {
            RetentionDays = Math.Max(
                1,
                configuration.GetValue("Mailer:Retention:Days", DefaultRetentionDays)),
            SweepIntervalHours = Math.Max(
                1,
                configuration.GetValue("Mailer:Retention:SweepIntervalHours", DefaultSweepIntervalHours)),
            SweepIntervalSeconds = configuration.GetValue<int?>("Mailer:Retention:SweepIntervalSeconds"),
            BatchSize = Math.Clamp(
                configuration.GetValue("Mailer:Retention:BatchSize", DefaultBatchSize),
                1,
                MaxBatchSize),
        };
}
