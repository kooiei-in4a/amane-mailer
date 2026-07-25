using System.Diagnostics;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Webhooks.Models;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Coverage for #389: WebhookDeliveryWorker isolates per-event failures so the
/// BackgroundService continues, with bounded backoff and PII-free observability.
/// </summary>
public sealed class WebhookDeliveryWorkerIsolationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 26, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConfiguredTenantId = Guid.Parse("00000000-0000-0000-0000-000000000389");
    private const string WebhookSecret = "test-webhook-secret-389";
    private const string WebhookUrl = "https://93.184.216.34/internal/mailer/webhooks";
    private const string PayloadCanary = "PAYLOAD-CANARY-389-do-not-log";
    private const string RecipientCanary = "pii-canary-389@example.com";

    [Fact]
    public async Task Malformed_json_event_does_not_block_later_valid_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);

        var malformedId = await harness.SeedPendingAsync(
            "{ not-json " + PayloadCanary,
            createdAt: FixedNow.AddSeconds(-2),
            ct);
        var validId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow)),
            createdAt: FixedNow.AddSeconds(-1),
            ct);

        await harness.RunWorkerUntilAsync(
            () => harness.WebhookHandler.AttemptCount >= 1,
            TimeSpan.FromSeconds(10),
            ct);

        Assert.Equal(DeliveryEventState.DeadLettered, await harness.ReadStatusAsync(malformedId, ct));
        Assert.Equal(DeliveryEventState.Delivered, await harness.ReadStatusAsync(validId, ct));
        Assert.Equal(1, harness.WebhookHandler.AttemptCount);
        Assert.Equal(
            WebhookDeliveryWorker.PayloadInvalidErrorCode,
            await harness.ReadLastErrorCodeAsync(malformedId, ct));
        Assert.DoesNotContain(PayloadCanary, harness.LogCapture.JoinedOutput(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claim_throw_once_then_worker_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var flaky = new FlakyWorkStore(harness.Repository) { ClaimFailuresRemaining = 1 };
        harness.Worker.WorkStoreOverride = flaky;

        var eventId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId),
            createdAt: FixedNow.AddSeconds(-1),
            ct);

        await harness.RunWorkerUntilAsync(
            () => harness.WebhookHandler.AttemptCount >= 1,
            TimeSpan.FromSeconds(10),
            ct);

        Assert.Equal(DeliveryEventState.Delivered, await harness.ReadStatusAsync(eventId, ct));
        Assert.Equal(0, flaky.ClaimFailuresRemaining);
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            entry =>
                entry.Level == LogLevel.Error &&
                entry.State.GetValueOrDefault("Stage") == WebhookDeliveryWorker.FailureStageClaim);
    }

    [Fact]
    public async Task Finalize_throw_once_then_worker_continues_with_later_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var flaky = new FlakyWorkStore(harness.Repository) { FinalizeFailuresRemaining = 1 };
        harness.Worker.WorkStoreOverride = flaky;

        var firstId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow)),
            createdAt: FixedNow.AddSeconds(-2),
            ct);
        var secondId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow.AddMilliseconds(1))),
            createdAt: FixedNow.AddSeconds(-1),
            ct);

        await harness.RunWorkerUntilAsync(
            () => harness.WebhookHandler.AttemptCount >= 2,
            TimeSpan.FromSeconds(15),
            ct);

        Assert.Equal(DeliveryEventState.Delivered, await harness.ReadStatusAsync(secondId, ct));
        // First event was claimed and HTTP-delivered, but finalize threw once; lease remains Delivering.
        Assert.Equal(DeliveryEventState.Delivering, await harness.ReadStatusAsync(firstId, ct));
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            entry =>
                entry.Level == LogLevel.Error &&
                entry.State.GetValueOrDefault("Stage") == WebhookDeliveryWorker.FailureStageFinalize);
    }

    [Fact]
    public async Task Tenant_resolve_unclassified_exception_does_not_block_later_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var lookup = new FlakyTenantLookup(harness.TenantRegistry) { FailuresRemaining = 1 };
        harness.Worker.TenantConfigLookupOverride = lookup;

        var firstId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow)),
            createdAt: FixedNow.AddSeconds(-2),
            ct);
        var secondId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow.AddMilliseconds(1))),
            createdAt: FixedNow.AddSeconds(-1),
            ct);

        await harness.RunWorkerUntilAsync(
            () => harness.WebhookHandler.AttemptCount >= 1,
            TimeSpan.FromSeconds(15),
            ct);

        Assert.Equal(DeliveryEventState.Delivering, await harness.ReadStatusAsync(firstId, ct));
        Assert.Equal(DeliveryEventState.Delivered, await harness.ReadStatusAsync(secondId, ct));
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            entry =>
                entry.Level == LogLevel.Error &&
                entry.State.GetValueOrDefault("Stage") == WebhookDeliveryWorker.FailureStageResolveConfig);
    }

    [Fact]
    public async Task Host_with_malformed_event_does_not_fault_background_service()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-webhook-isolation-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, BuildTenantConfigJson(), ct);
        await ApplyMigrationsAsync(connectionString, ct);

        var webhookHandler = new RecordingWebhookHandler();
        var logCapture = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["Mailer:Worker:Enabled"] = "True",
                        ["Mailer:Worker:SendTimeoutSeconds"] = "2",
                        ["Mailer:Worker:LeaseDurationSeconds"] = "30",
                        ["Mailer:Sweep:IntervalSeconds"] = "30",
                        ["Mailer:Webhook:MaxAttempts"] = "3",
                        ["Mailer:Webhook:InitialDelaySeconds"] = "1",
                        ["Mailer:Webhook:MaxDelaySeconds"] = "2",
                        ["Mailer:Webhook:DeliveryTimeoutSeconds"] = "2",
                        ["Mailer:Webhook:LeaseDurationSeconds"] = "20",
                        ["MAIL_SERVICE_TOKEN"] = "local-mail-service-token",
                        ["TEST_WEBHOOK_SECRET"] = WebhookSecret,
                    }));
                builder.ConfigureLogging(logging => logging.AddProvider(logCapture));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(webhookHandler));
                    // Keep webhook worker; drop mail worker noise for this isolation check.
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<WebhookDeliveryWorker>();
                });
            });

        try
        {
            _ = factory.CreateClient();
            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

            var validEventId = Guid.CreateVersion7(FixedNow);
            await SeedPendingSqlAsync(
                connectionString,
                "{ not-json " + PayloadCanary,
                createdAt: FixedNow.AddSeconds(-2),
                ct);
            await SeedPendingSqlAsync(
                connectionString,
                BuildValidPayloadJson(ConfiguredTenantId, validEventId),
                createdAt: FixedNow.AddSeconds(-1),
                ct,
                eventId: validEventId);

            factory.Services.GetRequiredService<IWebhookDeliveryQueue>().TrySignalWorkAvailable();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (webhookHandler.AttemptCount < 1 && DateTime.UtcNow < deadline)
            {
                Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
                await Task.Delay(50, ct);
            }

            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.True(webhookHandler.AttemptCount >= 1);
            Assert.DoesNotContain(PayloadCanary, logCapture.JoinedOutput(), StringComparison.Ordinal);
        }
        finally
        {
            await factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Stopping_token_cancels_without_error_log()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await harness.Worker.StartAsync(cts.Token);
        await Task.Delay(50, ct);
        await cts.CancelAsync();
        await harness.Worker.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(
            harness.LogCapture.Snapshot(),
            entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Failure_backoff_is_interrupted_by_stopping_token()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        harness.Worker.WorkStoreOverride = new FlakyWorkStore(harness.Repository)
        {
            ClaimFailuresRemaining = int.MaxValue,
        };
        _ = await harness.SeedPendingAsync(BuildValidPayloadJson(ConfiguredTenantId), FixedNow, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = Stopwatch.StartNew();
        await harness.Worker.StartAsync(cts.Token);
        harness.Queue.TrySignalWorkAvailable();
        await Task.Delay(100, ct);
        await cts.CancelAsync();
        await harness.Worker.StopAsync(CancellationToken.None);
        started.Stop();

        Assert.True(
            started.Elapsed < WebhookDeliveryWorker.IsolatedFailureBackoff + TimeSpan.FromSeconds(2),
            $"Expected shutdown to interrupt backoff promptly, elapsed={started.Elapsed}");
        Assert.DoesNotContain(
            harness.LogCapture.Snapshot(),
            entry =>
                entry.Level >= LogLevel.Error &&
                entry.FormattedMessage.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Idle_worker_does_not_busy_spin_claim_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var counting = new CountingNullWorkStore();
        harness.Worker.WorkStoreOverride = counting;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await harness.Worker.StartAsync(cts.Token);
        harness.Queue.TrySignalWorkAvailable();
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        await cts.CancelAsync();
        await harness.Worker.StopAsync(CancellationToken.None);

        // Pre-fix (#402): ~11.8M claim attempts in 500ms with an unread channel item.
        // With drain + InitialDelaySeconds wake (1s in this harness), expect a single poll.
        Assert.True(
            counting.ClaimAttempts is >= 1 and <= 5,
            $"claim attempts in 500ms with empty queue = {counting.ClaimAttempts}");
    }

    [Fact]
    public async Task Persistent_claim_failure_does_not_tight_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var flaky = new FlakyWorkStore(harness.Repository) { ClaimFailuresRemaining = int.MaxValue };
        harness.Worker.WorkStoreOverride = flaky;
        _ = await harness.SeedPendingAsync(BuildValidPayloadJson(ConfiguredTenantId), FixedNow, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await harness.Worker.StartAsync(cts.Token);
        harness.Queue.TrySignalWorkAvailable();
        await Task.Delay(WebhookDeliveryWorker.IsolatedFailureBackoff * 2 + TimeSpan.FromMilliseconds(200), ct);
        await cts.CancelAsync();
        await harness.Worker.StopAsync(CancellationToken.None);

        var claimErrors = harness.LogCapture.Snapshot()
            .Count(entry =>
                entry.Level == LogLevel.Error &&
                entry.State.GetValueOrDefault("Stage") == WebhookDeliveryWorker.FailureStageClaim);
        // Backoff + return-to-wait (#402) caps claim-error logs; without either this window
        // would produce dozens of attempts.
        Assert.InRange(claimErrors, 1, 4);
    }

    [Fact]
    public async Task Invalid_json_converges_to_payload_invalid_terminal_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            "{ not-json " + PayloadCanary,
            lockExpiresAt: FixedNow.AddMinutes(5),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(DeliveryEventState.DeadLettered, await harness.ReadStatusAsync(row.Id, ct));
        Assert.Equal(
            WebhookDeliveryWorker.PayloadInvalidErrorCode,
            await harness.ReadLastErrorCodeAsync(row.Id, ct));
        Assert.Equal(0, harness.WebhookHandler.AttemptCount);
        Assert.DoesNotContain(PayloadCanary, harness.LogCapture.JoinedOutput(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deserialize_null_payload_converges_to_terminal_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            "null",
            lockExpiresAt: FixedNow.AddMinutes(5),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(DeliveryEventState.DeadLettered, await harness.ReadStatusAsync(row.Id, ct));
        Assert.Equal(
            WebhookDeliveryWorker.PayloadInvalidErrorCode,
            await harness.ReadLastErrorCodeAsync(row.Id, ct));
    }

    [Fact]
    public async Task Terminal_finalize_failure_converges_via_388_reaper()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var flaky = new FlakyWorkStore(harness.Repository) { FinalizeFailuresRemaining = int.MaxValue };
        harness.Worker.WorkStoreOverride = flaky;

        var row = await harness.SeedDeliveringAsync(
            "{ not-json " + PayloadCanary,
            lockExpiresAt: FixedNow.AddMinutes(5),
            ct,
            attemptCount: 3,
            maxAttempts: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Worker.DeliverClaimedEventAsync(row, ct));
        Assert.Equal(DeliveryEventState.Delivering, await harness.ReadStatusAsync(row.Id, ct));

        await harness.ExpireLockAsync(row.Id, FixedNow.AddMinutes(-1), ct);

        var deadLettered = await harness.Repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);
        Assert.Equal(row.Id, Assert.Single(deadLettered).Id);
        Assert.Equal(
            DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode,
            deadLettered[0].ErrorCode);
        Assert.Equal(DeliveryEventState.DeadLettered, await harness.ReadStatusAsync(row.Id, ct));
    }

    [Fact]
    public async Task Isolated_failure_logs_exclude_payload_url_secret_signature_recipient_canaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var lookup = new FlakyTenantLookup(harness.TenantRegistry)
        {
            FailuresRemaining = 1,
            // The injected exception carries every forbidden value in its own message, so the
            // assertions below fail if the exception object ever reaches the logger.
            ThrowMessage = $"{PayloadCanary} {WebhookUrl} {WebhookSecret} sha256=deadbeef {RecipientCanary}",
        };
        harness.Worker.TenantConfigLookupOverride = lookup;

        // First event carries payload/URL/secret canaries in durable state; resolve_config throws
        // before delivery so those values must never appear in isolated-failure logs.
        _ = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow)),
            FixedNow.AddSeconds(-1),
            ct);
        var validId = await harness.SeedPendingAsync(
            BuildValidPayloadJson(ConfiguredTenantId, validId: Guid.CreateVersion7(FixedNow.AddMilliseconds(2))),
            FixedNow,
            ct);

        await harness.RunWorkerUntilAsync(
            () => harness.WebhookHandler.AttemptCount >= 1,
            TimeSpan.FromSeconds(15),
            ct);

        // Includes exception text: providers render exceptions via ToString(), so a canary check
        // that only looked at the message template would not cover what reaches a log sink.
        var joined = harness.LogCapture.JoinedOutputWithExceptions();
        Assert.DoesNotContain(PayloadCanary, joined, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookUrl, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WebhookSecret, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256=", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RecipientCanary, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient", joined, StringComparison.OrdinalIgnoreCase);

        var error = Assert.Single(
            harness.LogCapture.Snapshot(),
            entry =>
                entry.Level == LogLevel.Error &&
                entry.State.GetValueOrDefault("Stage") == WebhookDeliveryWorker.FailureStageResolveConfig);
        Assert.Equal(WebhookDeliveryWorker.FailureStageResolveConfig, error.State["Stage"]);
        Assert.True(error.State.ContainsKey("EventId"));
        Assert.True(error.State.ContainsKey("TenantId"));
        Assert.True(error.State.ContainsKey("MailRequestId"));
        Assert.True(error.State.ContainsKey("AttemptNumber"));
        // Unclassified (non-SQLite) failures record the type name and must not hand the exception
        // object to the logger, whose text may embed the webhook URL or payload fragments.
        Assert.Equal(typeof(InvalidOperationException).FullName, error.State["ExceptionType"]);
        Assert.Null(error.Exception);
        Assert.Equal(DeliveryEventState.Delivered, await harness.ReadStatusAsync(validId, ct));
    }

    [Fact]
    public async Task Finalize_skip_metric_regression_still_increments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            "null",
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(1, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
    }

    private static string BuildValidPayloadJson(Guid tenantId, Guid? validId = null)
    {
        var payload = new MailDeliveryEventPayload
        {
            EventId = validId ?? Guid.CreateVersion7(FixedNow),
            EventType = MailDeliveryEventType.Delivered,
            OccurredAt = FixedNow,
            TenantId = tenantId,
            SourceService = "example-service",
            MailRequestId = Guid.CreateVersion7(FixedNow),
            Status = MailDeliveryEventType.Delivered,
            AttemptCount = 1,
        };

        return JsonSerializer.Serialize(payload, MailerContractsJsonContext.Default.MailDeliveryEventPayload);
    }

    private static string BuildTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{ConfiguredTenantId}}",
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
              },
              "webhook": {
                "url": "{{WebhookUrl}}",
                "secret_env": "TEST_WEBHOOK_SECRET"
              }
            }
          ]
        }
        """;

    private static async Task ApplyMigrationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build());
        await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
    }

    private static async Task SeedPendingSqlAsync(
        string connectionString,
        string payloadJson,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken,
        Guid? eventId = null)
    {
        eventId ??= Guid.CreateVersion7(createdAt);
        var mailRequestId = Guid.CreateVersion7(createdAt);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_events (
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, next_attempt_at,
                created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'example-service', @MailRequestId, 'delivered',
                @PayloadJson, @Status, 0, 3, NULL,
                @CreatedAt, @CreatedAt);
            """;
        command.Parameters.AddWithValue("@Id", eventId.Value.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", ConfiguredTenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
        command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.Pending);
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ILoggerFactory _loggerFactory;
        private CancellationTokenSource? _runCts;

        private Harness(
            string root,
            ILoggerFactory loggerFactory,
            DeliveryEventRepository repository,
            WebhookDeliveryWorker worker,
            WebhookDeliveryQueue queue,
            MailerTenantRegistry tenantRegistry,
            MailerRuntimeMetrics runtimeMetrics,
            CapturingLoggerProvider logCapture,
            RecordingWebhookHandler webhookHandler,
            string connectionString)
        {
            _root = root;
            _loggerFactory = loggerFactory;
            Repository = repository;
            Worker = worker;
            Queue = queue;
            TenantRegistry = tenantRegistry;
            RuntimeMetrics = runtimeMetrics;
            LogCapture = logCapture;
            WebhookHandler = webhookHandler;
            ConnectionString = connectionString;
        }

        public DeliveryEventRepository Repository { get; }

        public WebhookDeliveryWorker Worker { get; }

        public WebhookDeliveryQueue Queue { get; }

        public MailerTenantRegistry TenantRegistry { get; }

        public MailerRuntimeMetrics RuntimeMetrics { get; }

        public CapturingLoggerProvider LogCapture { get; }

        public RecordingWebhookHandler WebhookHandler { get; }

        public string ConnectionString { get; }

        public static async Task<Harness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-isolation",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, BuildTenantConfigJson(), cancellationToken);

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                    ["MAIL_SERVICE_TOKEN"] = "local-mail-service-token",
                    ["TEST_WEBHOOK_SECRET"] = WebhookSecret,
                })
                .Build();
            var tenantRegistry = MailerTenantRegistry.Load(configuration);
            var runtimeMetrics = new MailerRuntimeMetrics();
            var logCapture = new CapturingLoggerProvider();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
            var webhookHandler = new RecordingWebhookHandler();
            var webhookOptions = new MailerWebhookOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
                DeliveryTimeoutSeconds = 2,
                LeaseDurationSeconds = 20,
            };
            var deliveryClient = new WebhookDeliveryClient(
                new StubHttpClientFactory(webhookHandler),
                new WebhookSignatureService(),
                new WebhookUrlValidator(),
                new FixedUtcTimeProvider(FixedNow),
                NullLogger<WebhookDeliveryClient>.Instance);
            var repository = new DeliveryEventRepository(factory);
            var queue = new WebhookDeliveryQueue();
            var worker = new WebhookDeliveryWorker(
                repository,
                deliveryClient,
                tenantRegistry,
                webhookOptions,
                queue,
                runtimeMetrics,
                new FixedUtcTimeProvider(FixedNow),
                loggerFactory.CreateLogger<WebhookDeliveryWorker>());

            return new Harness(
                root,
                loggerFactory,
                repository,
                worker,
                queue,
                tenantRegistry,
                runtimeMetrics,
                logCapture,
                webhookHandler,
                connectionString);
        }

        public async Task RunWorkerUntilAsync(
            Func<bool> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Worker.StartAsync(_runCts.Token);
            Queue.TrySignalWorkAvailable();

            var deadline = DateTime.UtcNow + timeout;
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }

            Assert.True(predicate(), "Worker did not reach the expected condition before timeout.");

            await _runCts.CancelAsync();
            await Worker.StopAsync(CancellationToken.None);
            _runCts.Dispose();
            _runCts = null;
        }

        public async Task<Guid> SeedPendingAsync(
            string payloadJson,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken,
            Guid? eventId = null)
        {
            eventId ??= Guid.CreateVersion7(createdAt);
            var mailRequestId = Guid.CreateVersion7(createdAt);

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO delivery_events (
                    id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                    status, attempt_count, max_attempts, next_attempt_at,
                    created_at, updated_at)
                VALUES (
                    @Id, @TenantId, 'example-service', @MailRequestId, 'delivered',
                    @PayloadJson, @Status, 0, 3, NULL,
                    @CreatedAt, @CreatedAt);
                """;
            command.Parameters.AddWithValue("@Id", eventId.Value.ToString("D"));
            command.Parameters.AddWithValue("@TenantId", ConfiguredTenantId.ToString("D"));
            command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
            command.Parameters.AddWithValue("@PayloadJson", payloadJson);
            command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.Pending);
            command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return eventId.Value;
        }

        public async Task<DeliveryEventRow> SeedDeliveringAsync(
            string payloadJson,
            DateTimeOffset lockExpiresAt,
            CancellationToken cancellationToken,
            int attemptCount = 1,
            int maxAttempts = 3)
        {
            var eventId = Guid.CreateVersion7(FixedNow);
            var mailRequestId = Guid.CreateVersion7(FixedNow);
            var lockToken = Guid.CreateVersion7(FixedNow);
            var claimedAt = FixedNow.AddMinutes(-2);

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO delivery_events (
                    id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                    status, attempt_count, max_attempts, next_attempt_at,
                    lock_token, lock_expires_at, created_at, updated_at)
                VALUES (
                    @Id, @TenantId, 'example-service', @MailRequestId, 'delivered',
                    @PayloadJson, @Status, @AttemptCount, @MaxAttempts, NULL,
                    @LockToken, @LockExpiresAt, @CreatedAt, @UpdatedAt);
                """;
            command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
            command.Parameters.AddWithValue("@TenantId", ConfiguredTenantId.ToString("D"));
            command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
            command.Parameters.AddWithValue("@PayloadJson", payloadJson);
            command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.Delivering);
            command.Parameters.AddWithValue("@AttemptCount", attemptCount);
            command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
            command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
            command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(claimedAt));
            command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(claimedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);

            return new DeliveryEventRow
            {
                Id = eventId,
                TenantId = ConfiguredTenantId,
                SourceService = "example-service",
                MailRequestId = mailRequestId,
                EventType = "delivered",
                PayloadJson = payloadJson,
                Status = DeliveryEventState.Delivering,
                AttemptCount = attemptCount,
                MaxAttempts = maxAttempts,
                LockToken = lockToken,
                LockExpiresAt = lockExpiresAt,
            };
        }

        public async Task ExpireLockAsync(Guid eventId, DateTimeOffset lockExpiresAt, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE delivery_events
                SET lock_expires_at = @LockExpiresAt
                WHERE id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
            command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<DeliveryEventState> ReadStatusAsync(Guid eventId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM delivery_events WHERE id = @Id;";
            command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return (DeliveryEventState)Convert.ToInt32(result);
        }

        public async Task<string?> ReadLastErrorCodeAsync(Guid eventId, CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_error_code FROM delivery_events WHERE id = @Id;";
            command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is DBNull or null ? null : Convert.ToString(result);
        }

        public async ValueTask DisposeAsync()
        {
            if (_runCts is not null)
            {
                await _runCts.CancelAsync();
                try
                {
                    await Worker.StopAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // Best-effort shutdown during dispose.
                }

                _runCts.Dispose();
            }

            _loggerFactory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }


    private sealed class CountingNullWorkStore : IWebhookDeliveryWorkStore
    {
        private int _claimAttempts;

        public int ClaimAttempts => Volatile.Read(ref _claimAttempts);

        public Task<DeliveryEventRow?> TryClaimOneAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claimAttempts);
            return Task.FromResult<DeliveryEventRow?>(null);
        }

        public Task<bool> FinalizeAsync(
            Guid id,
            Guid lockToken,
            DateTimeOffset now,
            DeliveryEventFinalizeOutcome outcome,
            DateTimeOffset? nextAttemptAt,
            string? lastErrorCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FlakyWorkStore(IWebhookDeliveryWorkStore inner) : IWebhookDeliveryWorkStore
    {
        public int ClaimFailuresRemaining { get; set; }

        public int FinalizeFailuresRemaining { get; set; }

        public Task<DeliveryEventRow?> TryClaimOneAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (ClaimFailuresRemaining > 0)
            {
                ClaimFailuresRemaining--;
                throw new InvalidOperationException("injected claim failure");
            }

            return inner.TryClaimOneAsync(now, leaseDuration, cancellationToken);
        }

        public Task<bool> FinalizeAsync(
            Guid id,
            Guid lockToken,
            DateTimeOffset now,
            DeliveryEventFinalizeOutcome outcome,
            DateTimeOffset? nextAttemptAt,
            string? lastErrorCode,
            CancellationToken cancellationToken = default)
        {
            if (FinalizeFailuresRemaining > 0)
            {
                FinalizeFailuresRemaining--;
                throw new InvalidOperationException("injected finalize failure");
            }

            return inner.FinalizeAsync(
                id,
                lockToken,
                now,
                outcome,
                nextAttemptAt,
                lastErrorCode,
                cancellationToken);
        }
    }

    private sealed class FlakyTenantLookup(MailerTenantRegistry registry) : IWebhookTenantConfigLookup
    {
        public int FailuresRemaining { get; set; }

        public string ThrowMessage { get; set; } = "injected resolve_config failure";

        public MailerTenant? Find(Guid tenantId)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException(ThrowMessage);
            }

            return registry.Find(tenantId);
        }

        public string? GetWebhookSecret(Guid tenantId) => registry.GetWebhookSecret(tenantId);
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
