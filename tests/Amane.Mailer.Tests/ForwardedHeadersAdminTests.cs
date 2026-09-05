using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

/// <summary>
/// Production HTTPS Admin behind a TLS-terminating reverse proxy requires
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> so antiforgery SecurePolicy.Always
/// sees <see cref="HttpRequest.IsHttps"/> via <c>X-Forwarded-Proto</c>.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class ForwardedHeadersAdminTests
{
    [Fact]
    public void Forwarded_headers_flag_defaults_to_false()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        Assert.False(ForwardedHeadersStartup.IsEnabled(configuration));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Forwarded_headers_flag_parses_strict_booleans(string raw, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ForwardedHeadersStartup.EnabledKey] = raw,
            })
            .Build();

        Assert.Equal(expected, ForwardedHeadersStartup.IsEnabled(configuration));
    }

    [Fact]
    public async Task Production_admin_login_over_http_upstream_succeeds_with_forwarded_proto()
    {
        await using var harness = await ProductionAdminHarness.CreateAsync(
            forwardedHeadersEnabled: true);

        using var client = harness.CreateHttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        using var response = await client.GetAsync("/admin/login", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("action=\"/admin/api/login\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_admin_login_over_http_upstream_fails_without_forwarded_headers()
    {
        await using var harness = await ProductionAdminHarness.CreateAsync(
            forwardedHeadersEnabled: false);

        using var client = harness.CreateHttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        // TestHost rethrows endpoint exceptions instead of converting them to HTTP 500.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("/admin/login", TestContext.Current.CancellationToken));
        Assert.Contains("SecurePolicy = Always", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untrusted_forwarded_proto_does_not_expose_first_run_setup_over_http()
    {
        var get = await ExecuteForwardedHeadersProbeAsync(
            IPAddress.Parse("203.0.113.10"),
            "203.0.113.11",
            HttpMethods.Get);
        Assert.False(get.IsHttps);
        Assert.Equal(StatusCodes.Status404NotFound, get.StatusCode);

        var post = await ExecuteForwardedHeadersProbeAsync(
            IPAddress.Parse("203.0.113.10"),
            "203.0.113.11",
            HttpMethods.Post);
        Assert.False(post.IsHttps);
    }

    [Fact]
    public async Task Configured_trusted_proxy_can_assert_forwarded_https_for_first_run_setup()
    {
        var trustedProxy = IPAddress.Parse("203.0.113.11");
        var result = await ExecuteForwardedHeadersProbeAsync(
            trustedProxy,
            trustedProxy.ToString(),
            HttpMethods.Get);

        Assert.True(result.IsHttps);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    private static async Task<(bool IsHttps, int StatusCode)> ExecuteForwardedHeadersProbeAsync(
        IPAddress remoteAddress,
        string trustedProxy,
        string method)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ForwardedHeadersStartup.EnabledKey] = "true",
                [ForwardedHeadersStartup.TrustedProxiesKey] = trustedProxy,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        ForwardedHeadersStartup.ConfigureServices(services, configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var application = new ApplicationBuilder(serviceProvider);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            context.Response.StatusCode = context.Request.IsHttps
                ? StatusCodes.Status200OK
                : StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Method = method;
        context.Request.Path = method == HttpMethods.Get ? "/setup" : "/setup/auth";
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        // The direct context is intentional: TestServer otherwise defaults the peer to loopback,
        // which would make an untrusted X-Forwarded-Proto look trusted in this regression test.
        await application.Build()(context);
        return (context.Request.IsHttps, context.Response.StatusCode);
    }

    private sealed class ProductionAdminHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;
        private readonly string? _previousForwardedHeadersFlag;

        private ProductionAdminHarness(
            string root,
            WebApplicationFactory<global::Program> factory,
            string? previousForwardedHeadersFlag)
        {
            _root = root;
            _factory = factory;
            _previousForwardedHeadersFlag = previousForwardedHeadersFlag;
        }

        internal HttpClient CreateHttpClient() =>
            _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                // Simulate TLS-terminated reverse proxy: plain HTTP to Kestrel.
                BaseAddress = new Uri("http://localhost"),
            });

        internal static async Task<ProductionAdminHarness> CreateAsync(bool forwardedHeadersEnabled)
        {
            var previousFlag = Environment.GetEnvironmentVariable(ForwardedHeadersStartup.EnabledKey);
            // WebApplication.CreateBuilder reads env before WebApplicationFactory config callbacks;
            // set the compose-contract key here so ConfigureServices/UseIfEnabled see it.
            Environment.SetEnvironmentVariable(
                ForwardedHeadersStartup.EnabledKey,
                forwardedHeadersEnabled ? "true" : "false");

            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var tenantDir = Path.Combine(root, "config");
            Directory.CreateDirectory(tenantDir);
            var tenantPath = Path.Combine(tenantDir, "tenants.json");
            await File.WriteAllTextAsync(tenantPath, MailerAdminFixtureHelpers.TenantConfigJson);

            var connections = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    })
                    .Build());
            await new SqlMigrationRunner(connections).ApplyPendingAsync();

            var passwordHash = AdminPasswordHasher.Hash("forwarded-headers-qual-password");
            try
            {
                var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment(Environments.Production);
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                            ["MAILER_TENANTS_PATH"] = tenantPath,
                            ["Mailer:Worker:Enabled"] = "false",
                            ["Mailer:Metrics:Enabled"] = "false",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                            ["AMANE_ADMIN_ENABLED"] = "true",
                            ["AMANE_ADMIN_USERNAME"] = "admin",
                            ["AMANE_ADMIN_PASSWORD_HASH"] = passwordHash,
                            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "0.0.0.0",
                            ["AMANE_ADMIN_ALLOW_HTTP"] = "false",
                            [ForwardedHeadersStartup.EnabledKey] = forwardedHeadersEnabled ? "true" : "false",
                        });
                    });
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.AddSingleton<IStartupFilter>(
                            new TestLocalAddressStartupFilter(IPAddress.Parse("172.18.0.2")));
                    });
                });

                return new ProductionAdminHarness(root, factory, previousFlag);
            }
            catch
            {
                RestoreForwardedHeadersEnv(previousFlag);
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            RestoreForwardedHeadersEnv(_previousForwardedHeadersFlag);
            MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
        }

        private static void RestoreForwardedHeadersEnv(string? previous) =>
            Environment.SetEnvironmentVariable(ForwardedHeadersStartup.EnabledKey, previous);
    }

    private sealed class TestLocalAddressStartupFilter(IPAddress localAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.LocalIpAddress ??= localAddress;
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await nextMiddleware();
                });

                next(app);
            };
    }
}
