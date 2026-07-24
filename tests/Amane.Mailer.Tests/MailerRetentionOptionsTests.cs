using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerRetentionOptionsTests
{
    [Fact]
    public void Load_unset_keys_keep_defaults()
    {
        var options = MailerRetentionOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(MailerRetentionOptions.DefaultRetentionDays, options.RetentionDays);
        Assert.Equal(MailerRetentionOptions.DefaultSweepIntervalHours, options.SweepIntervalHours);
        Assert.Null(options.SweepIntervalSeconds);
        Assert.Equal(MailerRetentionOptions.DefaultBatchSize, options.BatchSize);
        Assert.Equal(TimeSpan.FromHours(24), options.SweepInterval);
    }

    [Theory]
    [InlineData("Mailer:Retention:Days", "1", 1)]
    [InlineData("Mailer:Retention:Days", "3650", 3650)]
    [InlineData("Mailer:Retention:SweepIntervalHours", "1", 1)]
    [InlineData("Mailer:Retention:SweepIntervalHours", "168", 168)]
    [InlineData("Mailer:Retention:BatchSize", "1", 1)]
    [InlineData("Mailer:Retention:BatchSize", "250", 250)]
    public void Load_accepts_min_and_max(string key, string value, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var options = MailerRetentionOptions.Load(configuration);
        var actual = key switch
        {
            "Mailer:Retention:Days" => options.RetentionDays,
            "Mailer:Retention:SweepIntervalHours" => options.SweepIntervalHours,
            "Mailer:Retention:BatchSize" => options.BatchSize,
            _ => throw new InvalidOperationException(key),
        };
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("604800")]
    public void Load_optional_sweep_interval_seconds_accepts_bounds(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Retention:SweepIntervalSeconds"] = value,
            })
            .Build();

        var options = MailerRetentionOptions.Load(configuration);
        Assert.Equal(int.Parse(value), options.SweepIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(value)), options.SweepInterval);
    }

    [Theory]
    [InlineData("Mailer:Retention:Days", "0")]
    [InlineData("Mailer:Retention:Days", "-1")]
    [InlineData("Mailer:Retention:Days", "3651")]
    [InlineData("Mailer:Retention:Days", "abc")]
    [InlineData("Mailer:Retention:Days", "")]
    [InlineData("Mailer:Retention:SweepIntervalHours", "0")]
    [InlineData("Mailer:Retention:SweepIntervalHours", "169")]
    [InlineData("Mailer:Retention:SweepIntervalHours", "")]
    [InlineData("Mailer:Retention:SweepIntervalSeconds", "0")]
    [InlineData("Mailer:Retention:SweepIntervalSeconds", "-1")]
    [InlineData("Mailer:Retention:SweepIntervalSeconds", "604801")]
    [InlineData("Mailer:Retention:SweepIntervalSeconds", "")]
    [InlineData("Mailer:Retention:BatchSize", "0")]
    [InlineData("Mailer:Retention:BatchSize", "251")]
    [InlineData("Mailer:Retention:BatchSize", "")]
    public void Load_rejects_invalid_values(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerRetentionOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
    }
}
