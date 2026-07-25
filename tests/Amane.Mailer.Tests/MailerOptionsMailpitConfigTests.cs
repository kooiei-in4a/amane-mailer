using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerOptionsMailpitConfigTests
{
    [Fact]
    public void Load_unset_mailpit_settings_keep_defaults()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal(1025, options.MailpitSmtpPort);
        Assert.False(options.MailpitUseSsl);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpPort", "2525", 2525)]
    [InlineData("MAILPIT_SMTP_PORT", "1026", 1026)]
    public void Load_accepts_mailpit_port_keys(string key, string value, int expected)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());

        Assert.Equal(expected, options.MailpitSmtpPort);
    }

    [Fact]
    public void Load_prefers_structured_mailpit_port_over_env_alias()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Mailpit:SmtpPort"] = "2525",
                ["MAILPIT_SMTP_PORT"] = "1025",
            })
            .Build());

        Assert.Equal(2525, options.MailpitSmtpPort);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpPort", "0")]
    [InlineData("Mailer:Mailpit:SmtpPort", "65536")]
    [InlineData("MAILPIT_SMTP_PORT", "")]
    [InlineData("MAILPIT_SMTP_PORT", "abc")]
    public void Load_rejects_invalid_mailpit_port(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"={value}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:UseSsl", "true", true)]
    [InlineData("Mailer:Mailpit:UseSsl", "FALSE", false)]
    [InlineData("MAILPIT_SMTP_USE_SSL", "True", true)]
    public void Load_accepts_mailpit_ssl_booleans(string key, string value, bool expected)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());

        Assert.Equal(expected, options.MailpitUseSsl);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:UseSsl", "yes")]
    [InlineData("MAILPIT_SMTP_USE_SSL", "1")]
    [InlineData("MAILPIT_SMTP_USE_SSL", " ")]
    public void Load_rejects_invalid_mailpit_ssl(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => MailerOptions.Load(configuration));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("false", ex.Message, StringComparison.Ordinal);
    }
}
