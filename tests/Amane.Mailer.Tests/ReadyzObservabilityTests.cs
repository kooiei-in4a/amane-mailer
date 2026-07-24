using System.Net;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

/// <summary>
/// Coverage for #330: /readyz keeps the public HTTP contract while recording
/// a fixed primary failure reason via transition-only logs and readiness gauges.
/// </summary>
public sealed class ReadyzObservabilityTests
{
    [Fact]
    public async Task Readyz_records_worker_not_running_without_changing_http_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertReadyBody(body, ready: false);
        AssertPrimaryReason(harness, MailerReadinessReasons.WorkerNotRunning);
        AssertSingleNotReadyWarning(harness, MailerReadinessReasons.WorkerNotRunning);
    }

    [Fact]
    public async Task Readyz_records_sweep_not_running()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(false);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertPrimaryReason(harness, MailerReadinessReasons.SweepNotRunning);
    }

    [Fact]
    public async Task Readyz_records_heartbeat_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatMissing);
    }

    [Fact]
    public async Task Readyz_records_worker_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now.AddMinutes(-10), ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatStale);
    }

    [Fact]
    public async Task Readyz_records_sweep_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now.AddMinutes(-10), ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatStale);
    }

    [Fact]
    public async Task Readyz_recovers_to_ready_and_clears_failure_gauge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);
        using (var notReady = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        }

        harness.LogCapture.Clear();
        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var ready = await harness.Client.GetAsync("/readyz", ct);
        var body = await ready.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        AssertReadyBody(body, ready: true);
        AssertReadyMetrics(harness);
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            static entry =>
                entry.Level == LogLevel.Information &&
                entry.FormattedMessage.Contains("Mailer readiness recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readyz_suppresses_repeated_warning_for_same_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);

        using var first = await harness.Client.GetAsync("/readyz", ct);
        using var second = await harness.Client.GetAsync("/readyz", ct);
        using var third = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        AssertSingleNotReadyWarning(harness, MailerReadinessReasons.WorkerNotRunning);
    }

    [Fact]
    public async Task Readyz_logs_again_when_primary_reason_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);
        using (var first = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        }

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(false);
        using (var second = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        }

        var warnings = harness.LogCapture.Snapshot()
            .Where(static entry =>
                entry.Level == LogLevel.Warning &&
                entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(MailerReadinessReasons.WorkerNotRunning, warnings[0].State["Reason"]);
        Assert.Equal(MailerReadinessReasons.SweepNotRunning, warnings[1].State["Reason"]);
        AssertPrimaryReason(harness, MailerReadinessReasons.SweepNotRunning);
    }

    [Fact]
    public async Task Readyz_when_worker_disabled_skips_worker_sweep_and_heartbeat_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(
            ct,
            workerEnabled: false);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(false);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertReadyBody(body, ready: true);
        AssertReadyMetrics(harness);
        Assert.DoesNotContain(
            harness.LogCapture.Snapshot(),
            static entry => entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readyz_records_schema_not_ready_on_partial_migrations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateWithPartialMigrationsAsync(
            throughMigrationFileName: "002_worker_heartbeats.sql",
            cancellationToken: ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertReadyBody(body, ready: false);
        AssertPrimaryReason(harness, MailerReadinessReasons.SchemaNotReady);
    }

    [Fact]
    public async Task EvaluateCore_cancelled_probe_does_not_overwrite_readiness_observation()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        evaluator.Observe(MailerReadinessResult.Ready());
        Assert.True(metrics.CaptureSnapshot().Ready);
        logCapture.Clear();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new OperationCanceledException(cts.Token),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: cts.Token);

        Assert.False(cancelled.IsReady);
        var snapshot = metrics.CaptureSnapshot();
        Assert.True(snapshot.ReadinessObserved);
        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.ReadinessFailureReason);
        Assert.DoesNotContain(
            logCapture.Snapshot(),
            static entry => entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateCore_maps_database_and_unexpected_exceptions()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var databaseResult = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new SqliteException("db busy", 5),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(databaseResult.IsReady);
        Assert.Equal(MailerReadinessReasons.DatabaseError, databaseResult.FailureReason);
        AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        evaluator.ClearForTests();
        logCapture.Clear();

        var unexpectedResult = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new InvalidOperationException("boom with secret=s3cret"),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(unexpectedResult.IsReady);
        Assert.Equal(MailerReadinessReasons.UnexpectedError, unexpectedResult.FailureReason);
        AssertPrimaryReason(metrics, MailerReadinessReasons.UnexpectedError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("boom", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("db busy", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyException_maps_sqlite_and_unexpected()
    {
        Assert.Equal(
            MailerReadinessReasons.DatabaseError,
            MailerReadinessEvaluator.ClassifyException(new SqliteException("db busy", 5)));
        Assert.Equal(
            MailerReadinessReasons.DatabaseError,
            MailerReadinessEvaluator.ClassifyException(
                new InvalidOperationException("wrap", new SqliteException("locked", 6))));
        Assert.Equal(
            MailerReadinessReasons.UnexpectedError,
            MailerReadinessEvaluator.ClassifyException(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Observe_records_database_and_unexpected_errors_without_exception_message()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        evaluator.Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.DatabaseError));
        AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        evaluator.Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.UnexpectedError));
        AssertPrimaryReason(metrics, MailerReadinessReasons.UnexpectedError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("boom", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("db busy", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("password", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerHeartbeatFreshness_distinguishes_missing_and_stale()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var maxStaleness = TimeSpan.FromSeconds(300);

        Assert.Equal(
            MailerReadinessReasons.HeartbeatMissing,
            WorkerHeartbeatFreshness.GetFailureReason([], maxStaleness, now));
        Assert.Equal(
            MailerReadinessReasons.HeartbeatMissing,
            WorkerHeartbeatFreshness.GetFailureReason(
                [new WorkerHeartbeat("worker", now)],
                maxStaleness,
                now));
        Assert.Equal(
            MailerReadinessReasons.HeartbeatStale,
            WorkerHeartbeatFreshness.GetFailureReason(
                [
                    new WorkerHeartbeat("worker", now.AddMinutes(-10)),
                    new WorkerHeartbeat("sweep", now),
                ],
                maxStaleness,
                now));
        Assert.Null(
            WorkerHeartbeatFreshness.GetFailureReason(
                [
                    new WorkerHeartbeat("worker", now.AddSeconds(-30)),
                    new WorkerHeartbeat("sweep", now.AddSeconds(-45)),
                ],
                maxStaleness,
                now));
    }

    private static void AssertReadyBody(string body, bool ready)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal(ready, document.RootElement.GetProperty("ready").GetBoolean());
    }

    private static void AssertPrimaryReason(ReadyzObservabilityHarness harness, string reason) =>
        AssertPrimaryReason(harness.RuntimeMetrics, reason);

    private static void AssertPrimaryReason(MailerRuntimeMetrics metrics, string reason)
    {
        var snapshot = metrics.CaptureSnapshot();
        Assert.False(snapshot.Ready);
        Assert.Equal(reason, snapshot.ReadinessFailureReason);

        var text = PrometheusMetricsFormatter.Format(EmptyStats(), snapshot);
        Assert.Contains("mail_ready 0", text, StringComparison.Ordinal);
        Assert.Contains($"mail_readiness_failure{{reason=\"{reason}\"}} 1", text, StringComparison.Ordinal);
        foreach (var other in MailerReadinessReasons.All.Where(value => value != reason))
        {
            Assert.Contains($"mail_readiness_failure{{reason=\"{other}\"}} 0", text, StringComparison.Ordinal);
        }
    }

    private static void AssertReadyMetrics(ReadyzObservabilityHarness harness)
    {
        var snapshot = harness.RuntimeMetrics.CaptureSnapshot();
        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.ReadinessFailureReason);

        var text = PrometheusMetricsFormatter.Format(EmptyStats(), snapshot);
        Assert.Contains("mail_ready 1", text, StringComparison.Ordinal);
        foreach (var reason in MailerReadinessReasons.All)
        {
            Assert.Contains($"mail_readiness_failure{{reason=\"{reason}\"}} 0", text, StringComparison.Ordinal);
        }
    }

    private static void AssertSingleNotReadyWarning(ReadyzObservabilityHarness harness, string reason)
    {
        var warnings = harness.LogCapture.Snapshot()
            .Where(static entry =>
                entry.Level == LogLevel.Warning &&
                entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal))
            .ToList();
        Assert.Single(warnings);
        Assert.Equal(reason, warnings[0].State["Reason"]);
    }

    private static MailerDbStatsResult EmptyStats() =>
        new(
            AsOfUtc: DateTimeOffset.UtcNow,
            QueuedCount: 0,
            ProcessingCount: 0,
            DeliveredCount: 0,
            FailedCount: 0,
            DeadLetteredCount: 0,
            ReadyBacklogCount: 0,
            OldestQueuedAgeSeconds: 0,
            QueuedStaleCount: 0,
            StaleProcessingCount: 0,
            ExpiredProcessingCount: 0,
            RecentFailedCount: 0,
            RecentDeadLetteredCount: 0,
            WorkerHeartbeatAgeSeconds: -1,
            SweepHeartbeatAgeSeconds: -1);

    private sealed class ReadyzObservabilityHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private ReadyzObservabilityHarness(
            string root,
            WebApplicationFactory<global::Program> factory,
            HttpClient client,
            MailRequestRepository repository,
            WorkerServiceStatus serviceStatus,
            MailerRuntimeMetrics runtimeMetrics,
            CapturingLoggerProvider logCapture)
        {
            _root = root;
            _factory = factory;
            Client = client;
            Repository = repository;
            ServiceStatus = serviceStatus;
            RuntimeMetrics = runtimeMetrics;
            LogCapture = logCapture;
        }

        public HttpClient Client { get; }

        public MailRequestRepository Repository { get; }

        public WorkerServiceStatus ServiceStatus { get; }

        public MailerRuntimeMetrics RuntimeMetrics { get; }

        public CapturingLoggerProvider LogCapture { get; }

        public static Task<ReadyzObservabilityHarness> CreateAsync(
            CancellationToken cancellationToken,
            bool workerEnabled = true) =>
            CreateCoreAsync(
                cancellationToken,
                workerEnabled,
                migrateFully: true,
                throughMigrationFileName: null);

        public static Task<ReadyzObservabilityHarness> CreateWithPartialMigrationsAsync(
            string throughMigrationFileName,
            CancellationToken cancellationToken) =>
            CreateCoreAsync(
                cancellationToken,
                workerEnabled: false,
                migrateFully: false,
                throughMigrationFileName: throughMigrationFileName);

        private static async Task<ReadyzObservabilityHarness> CreateCoreAsync(
            CancellationToken cancellationToken,
            bool workerEnabled,
            bool migrateFully,
            string? throughMigrationFileName)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-readyz-observability",
                Guid.NewGuid().ToString("N"));
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
            if (migrateFully)
            {
                await new SqlMigrationRunner(factoryForMigrate).ApplyPendingAsync(cancellationToken);
            }
            else
            {
                var migrationDirectory = Path.Combine(root, "migrations");
                ApplyMigrationsThrough(migrationDirectory, throughMigrationFileName!);
                await new SqlMigrationRunner(factoryForMigrate, migrationDirectory)
                    .ApplyPendingAsync(cancellationToken);
            }

            var logCapture = new CapturingLoggerProvider();
            var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(logCapture);
                });
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["Mailer:Worker:Enabled"] = workerEnabled ? "true" : "false",
                        ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                });
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            return new ReadyzObservabilityHarness(
                root,
                factory,
                client,
                factory.Services.GetRequiredService<MailRequestRepository>(),
                factory.Services.GetRequiredService<WorkerServiceStatus>(),
                factory.Services.GetRequiredService<MailerRuntimeMetrics>(),
                logCapture);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
        }

        private static void ApplyMigrationsThrough(string migrationDirectory, string throughMigrationFileName)
        {
            Directory.CreateDirectory(migrationDirectory);
            var source = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
            if (!Directory.Exists(source))
            {
                source = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "Amane.Mailer",
                    "Data",
                    "Migrations"));
            }

            var keep = true;
            foreach (var file in Directory.GetFiles(source, "*.sql")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(file);
                if (!keep)
                    continue;

                File.Copy(file, Path.Combine(migrationDirectory, fileName), overwrite: true);
                if (string.Equals(fileName, throughMigrationFileName, StringComparison.Ordinal))
                    keep = false;
            }
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
