using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

public sealed class ReadyzHeartbeatTests
{
    [Fact]
    public async Task Readyz_returns_503_when_worker_heartbeat_is_stale_despite_running_flags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now.AddMinutes(-10), ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_returns_503_when_sweep_heartbeat_is_stale_despite_running_flags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now.AddMinutes(-10), ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_returns_ok_when_heartbeats_are_fresh_and_services_are_running()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkerHeartbeatFreshness_rejects_missing_or_stale_rows()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var maxStaleness = TimeSpan.FromSeconds(300);

        Assert.False(WorkerHeartbeatFreshness.AreFresh([], maxStaleness, now));

        Assert.False(WorkerHeartbeatFreshness.AreFresh(
            [new("worker", now)],
            maxStaleness,
            now));

        Assert.False(WorkerHeartbeatFreshness.AreFresh(
            [
                new("worker", now.AddMinutes(-10)),
                new("sweep", now),
            ],
            maxStaleness,
            now));

        Assert.True(WorkerHeartbeatFreshness.AreFresh(
            [
                new("worker", now.AddSeconds(-30)),
                new("sweep", now.AddSeconds(-45)),
            ],
            maxStaleness,
            now));
    }

    private sealed class ReadyzHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private ReadyzHarness(
            string root,
            WebApplicationFactory<global::Program> factory,
            HttpClient client,
            MailRequestRepository repository,
            WorkerServiceStatus serviceStatus)
        {
            _root = root;
            _factory = factory;
            Client = client;
            Repository = repository;
            ServiceStatus = serviceStatus;
        }

        public HttpClient Client { get; }

        public MailRequestRepository Repository { get; }

        public WorkerServiceStatus ServiceStatus { get; }

        public static async Task<ReadyzHarness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-readyz-heartbeat", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var tenantConfigDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(tenantConfigDirectory);
            var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, cancellationToken);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var factoryForMigrate = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factoryForMigrate).ApplyPendingAsync(cancellationToken);

            var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["Mailer:Worker:Enabled"] = "true",
                        ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    // Keep Worker enabled for readiness gating, but do not run loops that
                    // would overwrite intentionally stale heartbeats.
                    services.RemoveAll<IHostedService>();
                });
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            var repository = factory.Services.GetRequiredService<MailRequestRepository>();
            var serviceStatus = factory.Services.GetRequiredService<WorkerServiceStatus>();
            return new ReadyzHarness(root, factory, client, repository, serviceStatus);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
        }

        private static string TenantConfigJson =>
            $$"""
            {
              "version": 1,
              "environment": "develop",
              "tenants": [
                {
                  "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
                  "name": "example-develop",
                  "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
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
}
