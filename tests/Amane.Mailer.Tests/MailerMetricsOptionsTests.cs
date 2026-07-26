using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

public sealed class MailerMetricsOptionsTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Custom")]
    public void Validate_requires_bearer_when_enabled_outside_development(string environmentName)
    {
        var options = MailerMetricsOptions.Load(new ConfigurationBuilder().Build());

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate(environmentName));
        Assert.Contains("BearerToken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Development", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void Validate_allows_missing_bearer_in_development_or_testing(string environmentName)
    {
        var options = MailerMetricsOptions.Load(new ConfigurationBuilder().Build());

        options.Validate(environmentName);
        Assert.True(options.Enabled);
        Assert.Null(options.BearerToken);
    }

    [Fact]
    public void Validate_allows_missing_bearer_when_metrics_disabled_in_production()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Metrics:Enabled"] = "false",
            })
            .Build();

        var options = MailerMetricsOptions.Load(configuration);

        options.Validate(Environments.Production);
        Assert.False(options.Enabled);
        Assert.Null(options.BearerToken);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    public void Load_accepts_case_insensitive_enabled(string value, bool expected)
    {
        var options = MailerMetricsOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Metrics:Enabled"] = value,
            })
            .Build());

        Assert.Equal(expected, options.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Load_rejects_empty_or_malformed_enabled(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Metrics:Enabled"] = value,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerMetricsOptions.Load(configuration));
        Assert.Contains("Mailer:Metrics:Enabled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("false", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_unset_enabled_defaults_to_true()
    {
        var options = MailerMetricsOptions.Load(new ConfigurationBuilder().Build());
        Assert.True(options.Enabled);
    }

    [Fact]
    public void Validate_accepts_configured_bearer_in_production()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_METRICS_BEARER_TOKEN"] = " replace-with-scrape-token ",
            })
            .Build();

        var options = MailerMetricsOptions.Load(configuration);

        options.Validate(Environments.Production);
        Assert.Equal("replace-with-scrape-token", options.BearerToken);
    }

    [Fact]
    public void AllowsOptionalBearer_matches_development_and_testing_only()
    {
        Assert.True(MailerMetricsOptions.AllowsOptionalBearer(Environments.Development));
        Assert.True(MailerMetricsOptions.AllowsOptionalBearer("Testing"));
        Assert.False(MailerMetricsOptions.AllowsOptionalBearer(Environments.Production));
        Assert.False(MailerMetricsOptions.AllowsOptionalBearer(null));
        Assert.False(MailerMetricsOptions.AllowsOptionalBearer(string.Empty));
    }
}
