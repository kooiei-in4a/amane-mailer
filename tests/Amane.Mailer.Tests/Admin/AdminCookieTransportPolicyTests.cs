using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminCookieTransportPolicyTests
{
    [Theory]
    [InlineData("AMANE_ADMIN_ALLOW_HTTP", "true")]
    [InlineData("AMANE_ADMIN_ALLOW_HTTP", "TRUE")]
    [InlineData("MAILER_ADMIN_ALLOW_HTTP", "true")]
    public void IsAllowHttpRequested_true_for_explicit_true(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.True(AdminCookieTransportPolicy.IsAllowHttpRequested(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("ture")]
    [InlineData("1")]
    [InlineData("yes")]
    public void IsAllowHttpRequested_false_for_unset_false_or_typo(string? value)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null)
            settings["AMANE_ADMIN_ALLOW_HTTP"] = value;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        Assert.False(AdminCookieTransportPolicy.IsAllowHttpRequested(configuration));
    }

    [Fact]
    public void IsAllowHttpRequested_prefers_amane_over_mailer_alias()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ALLOW_HTTP"] = "false",
                ["MAILER_ADMIN_ALLOW_HTTP"] = "true",
            })
            .Build();

        Assert.False(AdminCookieTransportPolicy.IsAllowHttpRequested(configuration));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public void AllowsHttpTransport_only_in_development(string environmentName)
    {
        Assert.True(AdminCookieTransportPolicy.AllowsHttpTransport(environmentName));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("Custom")]
    [InlineData(null)]
    [InlineData("")]
    public void AllowsHttpTransport_rejects_non_development(string? environmentName)
    {
        Assert.False(AdminCookieTransportPolicy.AllowsHttpTransport(environmentName));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Validate_fails_when_admin_enabled_and_allow_http_outside_development(string environmentName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AdminCookieTransportPolicy.Validate(
                allowHttpRequested: true,
                environmentName,
                adminEnabled: true));

        Assert.Contains("AMANE_ADMIN_ALLOW_HTTP", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Development", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie=", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_allows_development_with_allow_http()
    {
        AdminCookieTransportPolicy.Validate(
            allowHttpRequested: true,
            Environments.Development,
            adminEnabled: true);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_ignores_allow_http_when_admin_disabled(string environmentName)
    {
        AdminCookieTransportPolicy.Validate(
            allowHttpRequested: true,
            environmentName,
            adminEnabled: false);
    }

    [Fact]
    public void Validate_allows_production_when_allow_http_false()
    {
        AdminCookieTransportPolicy.Validate(
            allowHttpRequested: false,
            Environments.Production,
            adminEnabled: true);
    }

    [Fact]
    public void Resolve_development_allow_http_uses_http_cookie_names()
    {
        var transport = AdminCookieTransportPolicy.Resolve(
            allowHttpRequested: true,
            Environments.Development);

        Assert.Equal(CookieSecurePolicy.SameAsRequest, transport.SecurePolicy);
        Assert.Equal(AdminCookieTransportPolicy.HttpAuthCookieName, transport.AuthCookieName);
        Assert.Equal(AdminCookieTransportPolicy.HttpCsrfCookieName, transport.CsrfCookieName);
    }

    [Fact]
    public void Resolve_development_without_allow_http_uses_secure_cookies()
    {
        var transport = AdminCookieTransportPolicy.Resolve(
            allowHttpRequested: false,
            Environments.Development);

        Assert.Equal(CookieSecurePolicy.Always, transport.SecurePolicy);
        Assert.Equal(AdminCookieTransportPolicy.SecureAuthCookieName, transport.AuthCookieName);
        Assert.Equal(AdminCookieTransportPolicy.SecureCsrfCookieName, transport.CsrfCookieName);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", true)]
    [InlineData("Staging", false)]
    [InlineData("Testing", true)]
    public void Resolve_non_development_always_uses_secure_host_cookies(
        string environmentName,
        bool allowHttpRequested)
    {
        var transport = AdminCookieTransportPolicy.Resolve(allowHttpRequested, environmentName);

        Assert.Equal(CookieSecurePolicy.Always, transport.SecurePolicy);
        Assert.Equal(AdminCookieTransportPolicy.SecureAuthCookieName, transport.AuthCookieName);
        Assert.Equal(AdminCookieTransportPolicy.SecureCsrfCookieName, transport.CsrfCookieName);
    }
}
