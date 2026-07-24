using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerSweepOptionsTests
{
    [Fact]
    public void Load_unset_key_keeps_default()
    {
        var options = MailerSweepOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(MailerSweepOptions.DefaultIntervalSeconds, options.IntervalSeconds);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3600")]
    public void Load_accepts_min_and_max(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Sweep:IntervalSeconds"] = value,
            })
            .Build();

        var options = MailerSweepOptions.Load(configuration);
        Assert.Equal(int.Parse(value), options.IntervalSeconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("  ")]
    public void Load_rejects_invalid_values(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Sweep:IntervalSeconds"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerSweepOptions.Load(configuration));
        Assert.Contains("Mailer:Sweep:IntervalSeconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
    }
}
