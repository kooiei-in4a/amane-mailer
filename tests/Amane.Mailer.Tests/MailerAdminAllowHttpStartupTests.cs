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

public sealed class MailerAdminAllowHttpStartupTests : IAsyncLifetime
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
