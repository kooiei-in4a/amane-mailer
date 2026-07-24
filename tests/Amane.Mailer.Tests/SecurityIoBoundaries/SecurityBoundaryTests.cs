using Amane.Mailer.Admin;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #341 / #355 — environment-scoped Admin cookie
/// transport policy (Secure / __Host- vs Development HTTP allow-list).
/// </summary>
public sealed class SecurityBoundaryPolicyTests
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

/// <summary>
/// Startup / DI integration coverage for #341 Admin ALLOW_HTTP policy.
/// </summary>
public sealed class SecurityBoundaryStartupTests : IAsyncLifetime
{
    private MailerApiFixture? _fixture;

    public async ValueTask InitializeAsync()
    {
        _fixture = new MailerApiFixture();
        await _fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    [Theory]
    [InlineData("Production", "AMANE_ADMIN_ALLOW_HTTP")]
    [InlineData("Staging", "MAILER_ADMIN_ALLOW_HTTP")]
    public async Task Enabled_admin_with_allow_http_fails_startup_outside_development(
        string environmentName,
        string allowHttpKey)
    {
        using var factory = CreateAdminFactory(
            environmentName,
            new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_USERNAME"] = "admin",
                ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
                ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
                [allowHttpKey] = "true",
                ["MAILER_METRICS_BEARER_TOKEN"] = "test-metrics-scrape-token",
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        });

        Assert.Contains("AMANE_ADMIN_ALLOW_HTTP", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("Development", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Development_with_allow_http_uses_http_cookie_settings()
    {
        using var factory = CreateAdminFactory(
            Environments.Development,
            new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_USERNAME"] = "admin",
                ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
                ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
                ["AMANE_ADMIN_ALLOW_HTTP"] = "true",
            });

        _ = factory.CreateClient();
        AssertCookieTransport(
            factory,
            CookieSecurePolicy.SameAsRequest,
            AdminCookieTransportPolicy.HttpAuthCookieName,
            AdminCookieTransportPolicy.HttpCsrfCookieName);
    }

    [Fact]
    public async Task Development_without_allow_http_uses_secure_cookie_settings()
    {
        using var factory = CreateAdminFactory(
            Environments.Development,
            new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_USERNAME"] = "admin",
                ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
                ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
                ["AMANE_ADMIN_ALLOW_HTTP"] = "false",
            });

        _ = factory.CreateClient();
        AssertSecureHostCookies(factory);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Production_or_staging_without_allow_http_uses_secure_cookie_settings(
        string environmentName)
    {
        using var factory = CreateAdminFactory(
            environmentName,
            new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_USERNAME"] = "admin",
                ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
                ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
                ["MAILER_METRICS_BEARER_TOKEN"] = "test-metrics-scrape-token",
            });

        _ = factory.CreateClient();
        AssertSecureHostCookies(factory);
    }

    [Fact]
    public async Task Disabled_admin_with_allow_http_true_still_starts_in_production()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateAdminFactory(
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "false",
                ["AMANE_ADMIN_ALLOW_HTTP"] = "true",
                ["AMANE_ADMIN_MASK_RECIPIENTS"] = "yes",
                ["MAILER_METRICS_BEARER_TOKEN"] = "test-metrics-scrape-token",
            });

        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/healthz", ct);
        using var admin = await client.GetAsync("/admin", ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, admin.StatusCode);
        Assert.False(factory.Services.GetRequiredService<MailerAdminOptions>().Enabled);
        AssertSecureHostCookies(factory);
    }

    private WebApplicationFactory<global::Program> CreateAdminFactory(
        string environmentName,
        IReadOnlyDictionary<string, string?> extraConfiguration)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mailer"] = _fixture!.ConnectionString,
            ["MAILER_TENANTS_PATH"] = _fixture.TenantConfigPath,
            ["Mailer:Worker:Enabled"] = "false",
            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
        };

        foreach (var (key, value) in extraConfiguration)
            settings[key] = value;

        return new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });
    }

    private static void AssertSecureHostCookies(WebApplicationFactory<global::Program> factory) =>
        AssertCookieTransport(
            factory,
            CookieSecurePolicy.Always,
            AdminCookieTransportPolicy.SecureAuthCookieName,
            AdminCookieTransportPolicy.SecureCsrfCookieName);

    private static void AssertCookieTransport(
        WebApplicationFactory<global::Program> factory,
        CookieSecurePolicy expectedSecurePolicy,
        string expectedAuthCookieName,
        string expectedCsrfCookieName)
    {
        var auth = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationConstants.Scheme);
        var antiforgery = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        Assert.Equal(expectedAuthCookieName, auth.Cookie.Name);
        Assert.Equal(expectedSecurePolicy, auth.Cookie.SecurePolicy);
        Assert.Equal(expectedCsrfCookieName, antiforgery.Cookie.Name);
        Assert.Equal(expectedSecurePolicy, antiforgery.Cookie.SecurePolicy);
    }
}
