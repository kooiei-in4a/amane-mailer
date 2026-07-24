namespace Amane.Mailer.Configuration;

public sealed record MailerSweepOptions
{
    public const int DefaultIntervalSeconds = 30;
    public const int MinIntervalSeconds = 1;
    public const int MaxIntervalSeconds = 3600;

    public int IntervalSeconds { get; init; } = DefaultIntervalSeconds;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    public static MailerSweepOptions Load(IConfiguration configuration) =>
        new()
        {
            IntervalSeconds = ConfigurationIntReader.Read(
                configuration,
                "Mailer:Sweep:IntervalSeconds",
                DefaultIntervalSeconds,
                MinIntervalSeconds,
                MaxIntervalSeconds),
        };
}
