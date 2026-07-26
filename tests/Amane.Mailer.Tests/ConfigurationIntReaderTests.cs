using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class ConfigurationIntReaderTests
{
    [Fact]
    public void Read_unset_key_returns_default()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(42, ConfigurationIntReader.Read(configuration, "Ops:Value", 42, 1, 100));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("101")]
    public void Read_rejects_empty_malformed_and_out_of_range(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ops:Value"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationIntReader.Read(configuration, "Ops:Value", 42, 1, 100));

        Assert.Contains("Ops:Value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"={value}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_primary_present_empty_fails_without_falling_back()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Primary"] = "  ",
                ["Fallback"] = "10",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationIntReader.Read(configuration, 5, 1, 100, "Primary", "Fallback"));

        Assert.Contains("Primary", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_prefers_primary_over_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Primary"] = "7",
                ["Fallback"] = "99",
            })
            .Build();

        Assert.Equal(7, ConfigurationIntReader.Read(configuration, 5, 1, 100, "Primary", "Fallback"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    [InlineData("1025")]
    public void ReadPort_accepts_valid_ports(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Mailpit:SmtpPort"] = value,
            })
            .Build();

        Assert.Equal(
            int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            ConfigurationIntReader.ReadPort(configuration, "Mailer:Mailpit:SmtpPort", 1025));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("nope")]
    public void ReadPort_rejects_out_of_range_and_malformed(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Mailpit:SmtpPort"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationIntReader.ReadPort(configuration, "Mailer:Mailpit:SmtpPort", 1025));

        Assert.Contains("Mailer:Mailpit:SmtpPort", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("65535", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPort_uses_fallback_when_primary_absent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILPIT_SMTP_PORT"] = "2525",
            })
            .Build();

        Assert.Equal(
            2525,
            ConfigurationIntReader.ReadPort(configuration, 1025, "Mailer:Mailpit:SmtpPort", "MAILPIT_SMTP_PORT"));
    }
}
