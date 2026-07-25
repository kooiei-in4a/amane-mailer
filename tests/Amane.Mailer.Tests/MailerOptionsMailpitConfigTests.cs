using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerOptionsMailpitConfigTests
{
    [Fact]
    public void Load_unset_mailpit_settings_keep_defaults()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder().Build());

        Assert.Equal("mailpit", options.MailpitSmtpHost);
        Assert.Equal(1025, options.MailpitSmtpPort);
        Assert.False(options.MailpitUseSsl);
        Assert.Null(options.MailpitSmtpPortLoadError);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpHost", "smtp.example.test")]
    [InlineData("MAILPIT_SMTP_HOST", "127.0.0.1")]
    public void Load_accepts_mailpit_host_keys(string key, string value)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());

        Assert.Equal(value, options.MailpitSmtpHost);
        Assert.Equal(key, options.MailpitSmtpHostConfiguredKey);
    }

    [Fact]
    public void Load_prefers_structured_mailpit_host_over_env_alias()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Mailpit:SmtpHost"] = "structured-host",
                ["MAILPIT_SMTP_HOST"] = "env-host",
            })
            .Build());

        Assert.Equal("structured-host", options.MailpitSmtpHost);
        Assert.Equal("Mailer:Mailpit:SmtpHost", options.MailpitSmtpHostConfiguredKey);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpPort", "2525", 2525)]
    [InlineData("MAILPIT_SMTP_PORT", "1026", 1026)]
    [InlineData("Mailer:Mailpit:SmtpPort", "1", 1)]
    [InlineData("Mailer:Mailpit:SmtpPort", "65535", 65535)]
    [InlineData("Mailer:Mailpit:SmtpPort", "1025", 1025)]
    public void Load_accepts_mailpit_port_keys(string key, string value, int expected)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());

        Assert.Equal(expected, options.MailpitSmtpPort);
        Assert.Null(options.MailpitSmtpPortLoadError);
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
    public void Load_defers_invalid_mailpit_port_error(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var options = MailerOptions.Load(configuration);

        Assert.Equal(1025, options.MailpitSmtpPort);
        Assert.NotNull(options.MailpitSmtpPortLoadError);
        Assert.Contains(key, options.MailpitSmtpPortLoadError, StringComparison.Ordinal);
        Assert.Contains("1", options.MailpitSmtpPortLoadError, StringComparison.Ordinal);
        Assert.Contains("65535", options.MailpitSmtpPortLoadError, StringComparison.Ordinal);
        Assert.DoesNotContain($"={value}", options.MailpitSmtpPortLoadError, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpHost", "")]
    [InlineData("Mailer:Mailpit:SmtpHost", "   ")]
    [InlineData("MAILPIT_SMTP_HOST", "")]
    public void Validate_rejects_blank_mailpit_host_when_mailpit_in_use(string key, string value)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("non-empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"={value}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpPort", "0")]
    [InlineData("Mailer:Mailpit:SmtpPort", "65536")]
    [InlineData("MAILPIT_SMTP_PORT", "")]
    [InlineData("MAILPIT_SMTP_PORT", "abc")]
    public void Validate_rejects_invalid_mailpit_port_when_mailpit_in_use(string key, string value)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build());
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("65535", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"={value}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1025")]
    [InlineData("65535")]
    public void Validate_accepts_boundary_mailpit_ports_when_mailpit_in_use(string port)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Mailpit:SmtpPort"] = port,
            })
            .Build());
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Validate_accepts_default_mailpit_settings_when_mailpit_in_use()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder().Build());
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        options.ValidateEffectiveProviders(tenants);
        Assert.Equal("mailpit", options.MailpitSmtpHost);
        Assert.Equal(1025, options.MailpitSmtpPort);
    }

    [Theory]
    [InlineData("Mailer:Mailpit:SmtpHost", "")]
    [InlineData("Mailer:Mailpit:SmtpPort", "0")]
    [InlineData("MAILPIT_SMTP_PORT", "not-a-port")]
    public void Validate_ignores_unused_mailpit_host_port_when_acs_only(string key, string value)
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = value,
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://example.communication.azure.com/;AccessKey=test",
            })
            .Build());
        var tenants = new[] { CreateTenant(provider: "acs", liveSending: true) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Validate_rejects_blank_host_when_provider_override_forces_mailpit()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_PROVIDER"] = "mailpit",
                ["MAILPIT_SMTP_HOST"] = " ",
            })
            .Build());
        var tenants = new[] { CreateTenant(provider: "acs", liveSending: true) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains("MAILPIT_SMTP_HOST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("mailpit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ignores_blank_host_when_provider_override_forces_acs()
    {
        var options = MailerOptions.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_PROVIDER"] = "acs",
                ["MAILPIT_SMTP_HOST"] = "",
                ["ACS_CONNECTION_STRING"] = "Endpoint=https://example.communication.azure.com/;AccessKey=test",
            })
            .Build());
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Validate_rejects_programmatic_out_of_range_port_when_mailpit_in_use()
    {
        var options = new MailerOptions { MailpitSmtpHost = "mailpit", MailpitSmtpPort = 0 };
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains("65535", ex.Message, StringComparison.Ordinal);
    }

    private static MailerTenant CreateTenant(string provider, bool liveSending) =>
        new()
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            Name = "example-develop",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress
            {
                Email = "noreply@example.com",
                DisplayName = "Example Service",
            },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = provider,
            LiveSending = liveSending,
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
            },
        };
}
