using Amane.Mailer.Configuration;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerOptionsProviderValidationTests
{
    [Fact]
    public void Unknown_provider_override_fails_validation()
    {
        var options = new MailerOptions { ProviderOverride = "acss" };
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains("MAILER_PROVIDER", ex.Message, StringComparison.Ordinal);
        Assert.Contains("acss", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Effective_acs_with_live_sending_requires_acs_connection_string()
    {
        var options = new MailerOptions
        {
            ProviderOverride = "acs",
            AcsConnectionString = string.Empty,
        };
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: true) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));

        Assert.Contains("ACS connection string is required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("live_sending=true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Effective_acs_without_live_sending_allows_missing_acs_connection_string()
    {
        var options = new MailerOptions
        {
            ProviderOverride = "acs",
            AcsConnectionString = string.Empty,
        };
        var tenants = new[] { CreateTenant(provider: "acs", liveSending: false) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Tenant_acs_with_live_sending_and_secret_passes()
    {
        var options = new MailerOptions
        {
            AcsConnectionString = "Endpoint=https://example.communication.azure.com/;AccessKey=test",
        };
        var tenants = new[] { CreateTenant(provider: "acs", liveSending: true) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Mailpit_override_passes_without_acs_secret()
    {
        var options = new MailerOptions { ProviderOverride = "mailpit" };
        var tenants = new[] { CreateTenant(provider: "acs", liveSending: true) };

        options.ValidateEffectiveProviders(tenants);
    }

    [Fact]
    public void Load_and_validate_rejects_unknown_MAILER_PROVIDER()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_PROVIDER"] = "acss",
            })
            .Build();

        var options = MailerOptions.Load(configuration);
        var tenants = new[] { CreateTenant(provider: "mailpit", liveSending: false) };

        Assert.Throws<InvalidOperationException>(
            () => options.ValidateEffectiveProviders(tenants));
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
