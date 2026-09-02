using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

public sealed class MailerProductionPlaceholderTokenValidationTests
{
    private const string CanonicalPlaceholderToken = "replace-with-token";
    private const string PatternPlaceholderToken = "prod-mail-change-me-token";
    private const string ValidProductionToken = "rotated-production-mail-token-7f3a";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Production_rejects_empty_or_whitespace_tenant_token(string token)
    {
        using var harness = CreateValidationHarness(token);

        var result = MailerConfigurationSnapshot.TryLoad(
            harness.Configuration,
            Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Equal(MailerConfigurationSnapshot.LoadFailureKind.TokenMissing, result.FailureKind);
    }

    [Fact]
    public void Production_rejects_canonical_placeholder_tenant_token()
    {
        using var harness = CreateValidationHarness(CanonicalPlaceholderToken);

        var result = MailerConfigurationSnapshot.TryLoad(
            harness.Configuration,
            Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Equal(MailerConfigurationSnapshot.LoadFailureKind.TokenMissing, result.FailureKind);
    }

    [Fact]
    public void Production_rejects_supported_placeholder_pattern_tenant_token()
    {
        using var harness = CreateValidationHarness(PatternPlaceholderToken);

        var result = MailerConfigurationSnapshot.TryLoad(
            harness.Configuration,
            Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Equal(MailerConfigurationSnapshot.LoadFailureKind.TokenMissing, result.FailureKind);
    }

    [Fact]
    public void Production_accepts_valid_non_placeholder_tenant_token()
    {
        using var harness = CreateValidationHarness(ValidProductionToken);

        var result = MailerConfigurationSnapshot.TryLoad(
            harness.Configuration,
            Environments.Production);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Snapshot);
    }

    [Fact]
    public void Production_failure_message_does_not_echo_secret_literal()
    {
        using var harness = CreateValidationHarness(CanonicalPlaceholderToken);

        var ex = Assert.Throws<MailerConfigurationLoadException>(() =>
            MailerConfigurationSnapshot.Load(
                harness.Configuration,
                Environments.Production));

        Assert.Equal(MailerConfigurationLoadFailureKind.TokenMissing, ex.Kind);
        Assert.Contains("known placeholder value", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CanonicalPlaceholderToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_doctor_and_runtime_share_placeholder_detection_for_production()
    {
        var tokens = new[]
        {
            CanonicalPlaceholderToken,
            PatternPlaceholderToken,
            "<token>",
            "replace-with-your-token",
        };

        foreach (var token in tokens)
        {
            Assert.True(
                ConfigurationPlaceholderDetector.LooksLikePlaceholder(token),
                $"Expected placeholder detection for token pattern '{token}'.");

            using var harness = CreateValidationHarness(token);
            var result = MailerConfigurationSnapshot.TryLoad(
                harness.Configuration,
                Environments.Production);
            Assert.False(result.Succeeded);
            Assert.Equal(MailerConfigurationSnapshot.LoadFailureKind.TokenMissing, result.FailureKind);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("SomeUnknownEnvironment")]
    public async Task Host_strict_environments_reject_placeholder_token(string environmentName)
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            environmentName,
            CanonicalPlaceholderToken);

        Assert.Equal(environmentName, harness.HostEnvironmentName);

        var ex = Assert.Throws<MailerConfigurationLoadException>(() => harness.CreateClient());
        Assert.Equal(MailerConfigurationLoadFailureKind.TokenMissing, ex.Kind);
        Assert.Contains("known placeholder value", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CanonicalPlaceholderToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_development_allows_documented_local_placeholder_token()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            Environments.Development,
            "local-mail-service-token");

        Assert.Equal(Environments.Development, harness.HostEnvironmentName);

        using var client = harness.CreateClient();
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_testing_allows_test_fixture_token()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            "Testing",
            MailerWebApplicationFixtureBase.Token);

        Assert.Equal("Testing", harness.HostEnvironmentName);

        using var client = harness.CreateClient();
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_production_rejects_production_placeholder_token()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            Environments.Production,
            CanonicalPlaceholderToken);

        Assert.Equal(Environments.Production, harness.HostEnvironmentName);

        var ex = Assert.Throws<MailerConfigurationLoadException>(() => harness.CreateClient());
        Assert.Equal(MailerConfigurationLoadFailureKind.TokenMissing, ex.Kind);
        Assert.Contains("known placeholder value", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CanonicalPlaceholderToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_production_accepts_production_valid_token()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            Environments.Production,
            ValidProductionToken);

        Assert.Equal(Environments.Production, harness.HostEnvironmentName);

        using var client = harness.CreateClient();
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_production_with_config_aspnetcore_development_still_rejects_placeholder()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            Environments.Production,
            CanonicalPlaceholderToken,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = Environments.Development,
            });

        Assert.Equal(Environments.Production, harness.HostEnvironmentName);

        var ex = Assert.Throws<MailerConfigurationLoadException>(() => harness.CreateClient());
        Assert.Equal(MailerConfigurationLoadFailureKind.TokenMissing, ex.Kind);
        Assert.Contains("known placeholder value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_production_with_config_dotnet_development_still_rejects_placeholder()
    {
        await using var harness = await PlaceholderHostHarness.CreateAsync(
            Environments.Production,
            CanonicalPlaceholderToken,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = Environments.Development,
            });

        Assert.Equal(Environments.Production, harness.HostEnvironmentName);

        var ex = Assert.Throws<MailerConfigurationLoadException>(() => harness.CreateClient());
        Assert.Equal(MailerConfigurationLoadFailureKind.TokenMissing, ex.Kind);
        Assert.Contains("known placeholder value", ex.Message, StringComparison.Ordinal);
    }

    private static ValidationHarness CreateValidationHarness(string token)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tenantsPath = Path.Combine(root, "tenants.json");
        File.WriteAllText(tenantsPath, TenantConfigJson);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:TenantsPath"] = tenantsPath,
                ["MAIL_SERVICE_TOKEN"] = token,
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
            })
            .Build();

        return new ValidationHarness(root, configuration);
    }

    private sealed class ValidationHarness(string root, IConfiguration configuration) : IDisposable
    {
        public IConfiguration Configuration { get; } = configuration;

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
            }
        }
    }

    private sealed class PlaceholderHostHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private PlaceholderHostHarness(
            string root,
            WebApplicationFactory<global::Program> factory,
            string hostEnvironmentName)
        {
            _root = root;
            _factory = factory;
            HostEnvironmentName = hostEnvironmentName;
        }

        public string HostEnvironmentName { get; }

        public static async Task<PlaceholderHostHarness> CreateAsync(
            string environmentName,
            string token,
            IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson);

            var connectionString = $"Data Source={databasePath}";
            var migrateFactory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());
            await new SqlMigrationRunner(migrateFactory).ApplyPendingAsync();

            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = connectionString,
                ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                ["MAIL_SERVICE_TOKEN"] = token,
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
            };

            if (extraConfiguration is not null)
            {
                foreach (var (key, value) in extraConfiguration)
                {
                    settings[key] = value;
                }
            }

            var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environmentName);
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(settings);
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll(typeof(IHostedService));
                });
            });

            return new PlaceholderHostHarness(root, factory, environmentName);
        }

        public HttpClient CreateClient()
        {
            _ = _factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
            return _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
            }
        }
    }

    private const string TenantConfigJson =
        """
        {
          "version": 1,
          "environment": "production",
          "tenants": [
            {
              "tenant_id": "00000000-0000-0000-0000-000000000101",
              "name": "example-production",
              "source_services": ["example-service"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            }
          ]
        }
        """;
}
