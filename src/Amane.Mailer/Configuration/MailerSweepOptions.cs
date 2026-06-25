namespace Amane.Mailer.Configuration;

public sealed record MailerSweepOptions
{
    public const int DefaultIntervalSeconds = 30;

    public int IntervalSeconds { get; init; } = DefaultIntervalSeconds;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);

    public static MailerSweepOptions Load(IConfiguration configuration) =>
        new()
        {
            IntervalSeconds = Math.Max(
                1,
                configuration.GetValue("Mailer:Sweep:IntervalSeconds", DefaultIntervalSeconds)),
        };
}
