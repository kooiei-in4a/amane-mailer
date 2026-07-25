using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class ConfigurationBooleanReaderTests
{
    [Fact]
    public void Read_unset_key_returns_default()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.True(ConfigurationBooleanReader.Read(configuration, "Feature:Enabled", true));
        Assert.False(ConfigurationBooleanReader.Read(configuration, "Feature:Enabled", false));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    public void Read_accepts_case_insensitive_true_false(string value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feature:Enabled"] = value,
            })
            .Build();

        Assert.Equal(expected, ConfigurationBooleanReader.Read(configuration, "Feature:Enabled", !expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("ture")]
    public void Read_rejects_empty_whitespace_and_malformed(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feature:Enabled"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBooleanReader.Read(configuration, "Feature:Enabled", false));

        Assert.Contains("Feature:Enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("false", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"={value}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{value}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_primary_present_empty_fails_without_falling_back()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Primary"] = "",
                ["Fallback"] = "true",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationBooleanReader.Read(configuration, defaultValue: false, "Primary", "Fallback"));

        Assert.Contains("Primary", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_prefers_primary_over_fallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Primary"] = "false",
                ["Fallback"] = "true",
            })
            .Build();

        Assert.False(ConfigurationBooleanReader.Read(configuration, defaultValue: true, "Primary", "Fallback"));
    }

    [Fact]
    public void Read_uses_fallback_when_primary_absent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fallback"] = "true",
            })
            .Build();

        Assert.True(ConfigurationBooleanReader.Read(configuration, defaultValue: false, "Primary", "Fallback"));
    }

    [Fact]
    public void ReadOptional_returns_null_when_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Null(ConfigurationBooleanReader.ReadOptional(configuration, "Feature:Enabled"));
        Assert.Null(ConfigurationBooleanReader.ReadOptional(configuration, "Primary", "Fallback"));
    }
}
