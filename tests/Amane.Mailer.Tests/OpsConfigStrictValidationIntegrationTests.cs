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

public sealed class OpsConfigStrictValidationIntegrationTests
{
    [Fact]
    public async Task Host_startup_rejects_zero_worker_batch_claim_size()
    {
        await using var harness = await OpsConfigHostHarness.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "true",
                ["Mailer:Worker:BatchClaimSize"] = "0",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => harness.CreateClient());
        Assert.Contains("Mailer:Worker:BatchClaimSize", ex.Message, StringComparison.Ordinal);
        Assert.Contains("between", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MailerWebApplicationFixtureBase.Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_startup_rejects_empty_retention_days_with_worker_disabled()
    {
        await using var harness = await OpsConfigHostHarness.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Retention:Days"] = "",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => harness.CreateClient());
        Assert.Contains("Mailer:Retention:Days", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_startup_rejects_lease_unsafe_worker_combination_when_worker_enabled()
    {
        await using var harness = await OpsConfigHostHarness.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "true",
                ["Mailer:Worker:BatchClaimSize"] = "10",
                ["Mailer:Worker:MaxSendConcurrency"] = "4",
                ["Mailer:Worker:SendTimeoutSeconds"] = "90",
                ["Mailer:Worker:LeaseDurationSeconds"] = "120",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => harness.CreateClient());
        Assert.Contains("Mailer:Worker:LeaseDurationSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_startup_allows_lease_unsafe_combination_when_worker_disabled()
    {
        await using var harness = await OpsConfigHostHarness.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Worker:BatchClaimSize"] = "10",
                ["Mailer:Worker:MaxSendConcurrency"] = "4",
                ["Mailer:Worker:SendTimeoutSeconds"] = "90",
                ["Mailer:Worker:LeaseDurationSeconds"] = "120",
            });

        using var client = harness.CreateClient();
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Host_startup_accepts_default_ops_configuration()
    {
        await using var harness = await OpsConfigHostHarness.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
            });

        using var client = harness.CreateClient();
        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var worker = harness.Factory.Services.GetRequiredService<MailerWorkerOptions>();
        Assert.Equal(MailerWorkerOptions.DefaultBatchClaimSize, worker.BatchClaimSize);
    }

    [Fact]
    public void AddAmaneMailerServices_rejects_invalid_webhook_max_attempts_on_resolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Webhook:MaxAttempts"] = "0",
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new IntegrationHostEnvironment());
        services.AddLogging();
        services.AddAmaneMailerServices(configuration);

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<MailerWebhookOptions>());
        Assert.Contains("Mailer:Webhook:MaxAttempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAmaneMailerServices_registers_http_client_factory_when_worker_disabled()
    {
        // Native AOT path smoke: Development + Worker:Enabled=false (#341).
        // WebhookDeliveryClient stays registered; IHttpClientFactory must too.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new IntegrationHostEnvironment
        {
            EnvironmentName = Environments.Development,
        });
        services.AddLogging();
        services.AddAmaneMailerServices(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<System.Net.Http.IHttpClientFactory>());
    }

    private sealed class IntegrationHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Amane.Mailer.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class OpsConfigHostHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private OpsConfigHostHarness(string root, WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            _factory = factory;
        }

        public WebApplicationFactory<global::Program> Factory => _factory;

        public static async Task<OpsConfigHostHarness> CreateAsync(
            IReadOnlyDictionary<string, string?> extraConfiguration)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var tenantConfigDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(tenantConfigDirectory);
            var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
            await File.WriteAllTextAsync(
                tenantConfigPath,
                """
                {
                  "version": 1,
                  "environment": "develop",
                  "tenants": [
                    {
                      "tenant_id": "00000000-0000-0000-0000-000000000101",
                      "name": "example-develop",
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
                """);

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
                ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                ["Mailer:Metrics:Enabled"] = "false",
            };
            foreach (var (key, value) in extraConfiguration)
            {
                settings[key] = value;
            }

            var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(settings);
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll(typeof(IHostedService));
                });
            });

            return new OpsConfigHostHarness(root, factory);
        }

        public HttpClient CreateClient() =>
            _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });

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
}
