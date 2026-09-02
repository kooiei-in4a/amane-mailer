using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Webhooks.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Coverage for #328: webhook finalize fencing failures are observable via
/// <c>mail_webhook_finalize_skipped_total</c> and PII-free structured Warning logs
/// on every finalize path (delivery result + terminal failure).
/// </summary>
public sealed class WebhookFinalizeSkipObservabilityTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConfiguredTenantId = Guid.Parse("00000000-0000-0000-0000-000000000328");
    private static readonly Guid UnknownTenantId = Guid.Parse("00000000-0000-0000-0000-000000000999");
    private const string WebhookSecret = "test-webhook-secret-328";
    private const string WebhookUrl = "https://93.184.216.34/internal/mailer/webhooks";

    [Fact]
    public async Task Successful_finalize_does_not_increment_webhook_finalize_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            BuildValidPayloadJson(ConfiguredTenantId),
            lockExpiresAt: FixedNow.AddMinutes(5),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(0, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
        Assert.DoesNotContain(
            harness.LogCapture.Snapshot(),
            static entry => entry.FormattedMessage.Contains("Skipped webhook finalize", StringComparison.Ordinal));
        Assert.Equal(1, harness.WebhookHandler.AttemptCount);

        var state = await harness.ReadStatusAsync(row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivered, state);
    }

    [Fact]
    public async Task Delivery_result_finalize_skip_increments_counter_and_logs_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            BuildValidPayloadJson(ConfiguredTenantId),
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(1, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
        Assert.Equal(1, harness.WebhookHandler.AttemptCount);
        AssertSkipWarning(
            harness,
            row,
            DeliveryEventFinalizeOutcome.Delivered,
            WebhookDeliveryWorker.FinalizeSkipReasonDeliveryResult);

        var state = await harness.ReadStatusAsync(row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, state);
        AssertMetricsTextContains(harness, "mail_webhook_finalize_skipped_total 1");
    }

    [Fact]
    public async Task Webhook_not_configured_finalize_skip_increments_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            UnknownTenantId,
            BuildValidPayloadJson(UnknownTenantId),
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(1, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
        Assert.Equal(0, harness.WebhookHandler.AttemptCount);
        AssertSkipWarning(
            harness,
            row,
            DeliveryEventFinalizeOutcome.DeadLettered,
            WebhookDeliveryWorker.FinalizeSkipReasonWebhookNotConfigured);

        var state = await harness.ReadStatusAsync(row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, state);
    }

    [Fact]
    public async Task Payload_invalid_finalize_skip_increments_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var row = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            "null",
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        Assert.Equal(1, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
        Assert.Equal(0, harness.WebhookHandler.AttemptCount);
        AssertSkipWarning(
            harness,
            row,
            DeliveryEventFinalizeOutcome.DeadLettered,
            WebhookDeliveryWorker.FinalizeSkipReasonPayloadInvalid);

        var state = await harness.ReadStatusAsync(row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, state);
    }

    [Fact]
    public async Task Multiple_independent_skips_accumulate_one_count_each()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);

        var first = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            BuildValidPayloadJson(ConfiguredTenantId),
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);
        await harness.Worker.DeliverClaimedEventAsync(first, ct);
        Assert.Equal(1, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);

        // A second observation of the same expired row is a separate skip event.
        await harness.Worker.DeliverClaimedEventAsync(first, ct);
        Assert.Equal(2, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);

        var second = await harness.SeedDeliveringAsync(
            UnknownTenantId,
            BuildValidPayloadJson(UnknownTenantId),
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);
        await harness.Worker.DeliverClaimedEventAsync(second, ct);
        Assert.Equal(3, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);

        AssertMetricsTextContains(harness, "mail_webhook_finalize_skipped_total 3");
    }

    [Fact]
    public async Task Successful_retry_and_dead_letter_finalize_do_not_increment_skip_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);

        harness.WebhookHandler.FailuresBeforeSuccess = 1;
        var retryRow = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            BuildValidPayloadJson(ConfiguredTenantId),
            lockExpiresAt: FixedNow.AddMinutes(5),
            cancellationToken: ct,
            attemptCount: 1,
            maxAttempts: 3);
        await harness.Worker.DeliverClaimedEventAsync(retryRow, ct);
        Assert.Equal(DeliveryEventState.Pending, await harness.ReadStatusAsync(retryRow.Id, ct));

        harness.WebhookHandler.FailuresBeforeSuccess = int.MaxValue;
        var deadLetterRow = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            BuildValidPayloadJson(ConfiguredTenantId),
            lockExpiresAt: FixedNow.AddMinutes(5),
            cancellationToken: ct,
            attemptCount: 3,
            maxAttempts: 3);
        await harness.Worker.DeliverClaimedEventAsync(deadLetterRow, ct);
        Assert.Equal(DeliveryEventState.DeadLettered, await harness.ReadStatusAsync(deadLetterRow.Id, ct));

        Assert.Equal(0, harness.RuntimeMetrics.CaptureSnapshot().WebhookFinalizeSkippedTotal);
    }

    [Fact]
    public async Task Skip_warning_excludes_lock_token_url_secret_and_payload_pii_canaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(ct);
        var payloadJson = BuildValidPayloadJson(ConfiguredTenantId);
        var row = await harness.SeedDeliveringAsync(
            ConfiguredTenantId,
            payloadJson,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Worker.DeliverClaimedEventAsync(row, ct);

        var joined = harness.LogCapture.JoinedOutput();
        Assert.DoesNotContain(row.LockToken.ToString("D"), joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WebhookUrl, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WebhookSecret, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(payloadJson, joined, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.com", joined, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSkipWarning(
        Harness harness,
        DeliveryEventRow row,
        DeliveryEventFinalizeOutcome outcome,
        string skipReason)
    {
        var warnings = harness.LogCapture.Snapshot()
            .Where(static entry =>
                entry.Level == LogLevel.Warning &&
                entry.FormattedMessage.Contains("Skipped webhook finalize", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(warnings);
        var warning = warnings[^1];
        Assert.Equal(row.Id.ToString("D"), warning.State["EventId"]);
        Assert.Equal(row.TenantId.ToString("D"), warning.State["TenantId"]);
        Assert.Equal(row.MailRequestId.ToString("D"), warning.State["MailRequestId"]);
        Assert.Equal(row.AttemptCount.ToString(), warning.State["AttemptNumber"]);
        Assert.Equal(outcome.ToString(), warning.State["FinalizeOutcome"]);
        Assert.Equal(skipReason, warning.State["FinalizeSkipReason"]);
    }

    private static void AssertMetricsTextContains(Harness harness, string expectedLine)
    {
        var body = PrometheusMetricsFormatter.Format(
            new MailerDbStatsResult(
                AsOfUtc: FixedNow,
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
                SweepHeartbeatAgeSeconds: -1),
            harness.RuntimeMetrics.CaptureSnapshot());
        Assert.Contains("# HELP mail_webhook_finalize_skipped_total", body, StringComparison.Ordinal);
        Assert.Contains("# TYPE mail_webhook_finalize_skipped_total counter", body, StringComparison.Ordinal);
        Assert.Contains(expectedLine, body, StringComparison.Ordinal);
    }

    private static string BuildValidPayloadJson(Guid tenantId)
    {
        var payload = new MailDeliveryEventPayload
        {
            EventId = Guid.CreateVersion7(FixedNow),
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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ILoggerFactory _loggerFactory;

        private Harness(
            string root,
            ILoggerFactory loggerFactory,
            DeliveryEventRepository repository,
            WebhookDeliveryWorker worker,
            MailerRuntimeMetrics runtimeMetrics,
            CapturingLoggerProvider logCapture,
            RecordingWebhookHandler webhookHandler,
            string connectionString)
        {
            _root = root;
            _loggerFactory = loggerFactory;
            Repository = repository;
            Worker = worker;
            RuntimeMetrics = runtimeMetrics;
            LogCapture = logCapture;
            WebhookHandler = webhookHandler;
            ConnectionString = connectionString;
        }

        public DeliveryEventRepository Repository { get; }

        public WebhookDeliveryWorker Worker { get; }

        public MailerRuntimeMetrics RuntimeMetrics { get; }

        public CapturingLoggerProvider LogCapture { get; }

        public RecordingWebhookHandler WebhookHandler { get; }

        public string ConnectionString { get; }

        public static async Task<Harness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-finalize-skip",
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
            var tenantRegistry = MailerTenantRegistry.Load(configuration, "Testing");
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
            var worker = new WebhookDeliveryWorker(
                repository,
                deliveryClient,
                tenantRegistry,
                webhookOptions,
                new WebhookDeliveryQueue(),
                runtimeMetrics,
                new FixedUtcTimeProvider(FixedNow),
                loggerFactory.CreateLogger<WebhookDeliveryWorker>());

            return new Harness(
                root,
                loggerFactory,
                repository,
                worker,
                runtimeMetrics,
                logCapture,
                webhookHandler,
                connectionString);
        }

        public async Task<DeliveryEventRow> SeedDeliveringAsync(
            Guid tenantId,
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
            command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
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
                TenantId = tenantId,
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

        public ValueTask DisposeAsync()
        {
            _loggerFactory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
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
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
