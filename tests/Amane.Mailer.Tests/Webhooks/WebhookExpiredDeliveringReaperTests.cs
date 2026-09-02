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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Coverage for #388: expired Delivering webhook events at max_attempts converge to
/// DeadLettered without re-delivery, with fencing and batch drain.
/// </summary>
public sealed class WebhookExpiredDeliveringReaperTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000388");

    [Fact]
    public async Task Expired_at_max_attempts_is_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);

        var result = Assert.Single(deadLettered);
        Assert.Equal(row.Id, result.Id);
        Assert.Equal(DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode, result.ErrorCode);

        var state = await ReadFullStateAsync(db.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.DeadLettered, state.Status);
        Assert.Null(state.NextAttemptAt);
        Assert.Null(state.LockToken);
        Assert.Null(state.LockExpiresAt);
        Assert.Equal(FixedNow, state.CompletedAt);
        Assert.Equal(FixedNow, state.UpdatedAt);
        Assert.Equal(DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode, state.LastErrorCode);
        Assert.Equal(3, state.AttemptCount);
    }

    [Fact]
    public async Task Expired_above_max_attempts_is_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 5,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);

        Assert.Equal(row.Id, Assert.Single(deadLettered).Id);
        Assert.Equal(
            DeliveryEventState.DeadLettered,
            (await ReadFullStateAsync(db.ConnectionString, row.Id, ct)).Status);
    }

    [Fact]
    public async Task Expired_with_attempts_remaining_is_not_reaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 2,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);

        Assert.Empty(deadLettered);
        var state = await ReadFullStateAsync(db.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, state.Status);
        Assert.Equal(row.LockToken, state.LockToken);
    }

    [Fact]
    public async Task Unexpired_at_max_attempts_is_not_reaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(5),
            ct);

        var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);

        Assert.Empty(deadLettered);
        Assert.Equal(
            DeliveryEventState.Delivering,
            (await ReadFullStateAsync(db.ConnectionString, row.Id, ct)).Status);
    }

    [Fact]
    public async Task Non_delivering_status_is_not_reaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var pendingId = await SeedEventAsync(
            db.ConnectionString,
            status: DeliveryEventState.Pending,
            attemptCount: 3,
            maxAttempts: 3,
            lockToken: Guid.CreateVersion7(FixedNow),
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        var deadLettered = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(
            FixedNow,
            batchSize: 10,
            ct);

        Assert.Empty(deadLettered);
        Assert.Equal(
            DeliveryEventState.Pending,
            (await ReadFullStateAsync(db.ConnectionString, pendingId, ct)).Status);
    }

    [Fact]
    public async Task Stale_lock_token_fencing_refuses_overwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var staleToken = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var freshToken = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct,
            lockToken: staleToken);

        await using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE delivery_events
                SET lock_token = @LockToken, lock_expires_at = @LockExpiresAt, updated_at = @Now
                WHERE id = @Id;
                """;
            command.Parameters.AddWithValue("@LockToken", freshToken.ToString("D"));
            command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(5)));
            command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
            command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        Assert.Empty(await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, 10, ct));

        await using (var connection = await db.Factory.OpenConnectionAsync(ct))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE delivery_events
                SET
                    status = @DeadLetteredStatus,
                    next_attempt_at = NULL,
                    lock_token = NULL,
                    lock_expires_at = NULL,
                    last_error_code = @LastErrorCode,
                    updated_at = @Now,
                    completed_at = @Now
                WHERE id = @Id
                  AND status = @DeliveringStatus
                  AND lock_token = @LockToken
                  AND lock_expires_at IS NOT NULL
                  AND lock_expires_at <= @Now
                  AND attempt_count = @AttemptCount
                  AND attempt_count >= max_attempts;
                """;
            update.Parameters.AddWithValue("@DeadLetteredStatus", (int)DeliveryEventState.DeadLettered);
            update.Parameters.AddWithValue("@LastErrorCode", DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode);
            update.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
            update.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
            update.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
            update.Parameters.AddWithValue("@LockToken", staleToken.ToString("D"));
            update.Parameters.AddWithValue("@AttemptCount", 3);

            Assert.Equal(0, await update.ExecuteNonQueryAsync(ct));
        }

        var state = await ReadFullStateAsync(db.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, state.Status);
        Assert.Equal(freshToken, state.LockToken);
        Assert.Null(state.LastErrorCode);
    }

    [Fact]
    public async Task Batch_limit_is_respected_and_second_call_drains_remainder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        for (var i = 0; i < 5; i++)
        {
            await SeedDeliveringAsync(
                db.ConnectionString,
                attemptCount: 3,
                maxAttempts: 3,
                lockExpiresAt: FixedNow.AddMinutes(-1).AddSeconds(-i),
                ct);
        }

        var first = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, batchSize: 2, ct);
        Assert.Equal(2, first.Count);

        var second = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, batchSize: 2, ct);
        Assert.Equal(2, second.Count);

        var third = await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, batchSize: 2, ct);
        Assert.Single(third);

        var counts = await repository.CountOperationalAsync(ct);
        Assert.Equal(5, counts.DeadLetteredCount);
        Assert.Equal(0, counts.PendingCount);
    }

    [Fact]
    public async Task Second_run_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        Assert.Single(await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, 10, ct));
        var afterFirst = await ReadFullStateAsync(db.ConnectionString, row.Id, ct);

        Assert.Empty(await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, 10, ct));
        Assert.Equal(afterFirst, await ReadFullStateAsync(db.ConnectionString, row.Id, ct));
    }

    [Fact]
    public async Task Sweep_converges_expired_max_attempts_without_http_redelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SweepHarness.CreateAsync(ct);
        var row = await SeedDeliveringAsync(
            harness.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Sweep.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, ct);

        Assert.Equal(0, harness.WebhookHandler.AttemptCount);
        var state = await ReadFullStateAsync(harness.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.DeadLettered, state.Status);
        Assert.Equal(DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode, state.LastErrorCode);
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            entry => entry.FormattedMessage.Contains(row.Id.ToString("D"), StringComparison.Ordinal)
                && entry.FormattedMessage.Contains(
                    DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode,
                    StringComparison.Ordinal)
                && !entry.FormattedMessage.Contains("https://", StringComparison.Ordinal)
                && !entry.FormattedMessage.Contains("secret", StringComparison.OrdinalIgnoreCase)
                && !entry.FormattedMessage.Contains("recipient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sweep_leaves_expired_with_attempts_remaining_for_normal_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SweepHarness.CreateAsync(ct);
        var row = await SeedDeliveringAsync(
            harness.ConnectionString,
            attemptCount: 1,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await harness.Sweep.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, ct);

        var afterReaper = await ReadFullStateAsync(harness.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.Delivering, afterReaper.Status);
        Assert.Equal(row.LockToken, afterReaper.LockToken);

        var claimed = await harness.Repository.TryClaimOneAsync(FixedNow, TimeSpan.FromSeconds(20), ct);
        Assert.NotNull(claimed);
        Assert.Equal(row.Id, claimed!.Id);
        Assert.Equal(2, claimed.AttemptCount);
        Assert.NotEqual(row.LockToken, claimed.LockToken);
    }

    [Fact]
    public async Task Sweep_drain_loop_converges_beyond_single_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SweepHarness.CreateAsync(reconcileBatchSize: 2, ct);
        for (var i = 0; i < 5; i++)
        {
            await SeedDeliveringAsync(
                harness.ConnectionString,
                attemptCount: 3,
                maxAttempts: 3,
                lockExpiresAt: FixedNow.AddMinutes(-1).AddSeconds(-i),
                ct);
        }

        await harness.Sweep.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, ct);

        var counts = await harness.Repository.CountOperationalAsync(ct);
        Assert.Equal(5, counts.DeadLetteredCount);
        Assert.Equal(0, harness.WebhookHandler.AttemptCount);
    }

    [Fact]
    public async Task Concurrent_fresh_claim_then_expire_is_reaped_with_fresh_fencing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SweepHarness.CreateAsync(ct);
        var row = await SeedDeliveringAsync(
            harness.ConnectionString,
            attemptCount: 2,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        var claimed = await harness.Repository.TryClaimOneAsync(FixedNow, TimeSpan.FromSeconds(20), ct);
        Assert.NotNull(claimed);
        Assert.Equal(3, claimed!.AttemptCount);
        Assert.NotEqual(row.LockToken, claimed.LockToken);

        await using (var connection = new SqliteConnection(harness.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE delivery_events
                SET lock_expires_at = @LockExpiresAt
                WHERE id = @Id;
                """;
            command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(-1)));
            command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var connection = await harness.Factory.OpenConnectionAsync(ct))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE delivery_events
                SET status = @DeadLetteredStatus
                WHERE id = @Id
                  AND status = @DeliveringStatus
                  AND lock_token = @LockToken
                  AND lock_expires_at IS NOT NULL
                  AND lock_expires_at <= @Now
                  AND attempt_count = @AttemptCount
                  AND attempt_count >= max_attempts;
                """;
            update.Parameters.AddWithValue("@DeadLetteredStatus", (int)DeliveryEventState.DeadLettered);
            update.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
            update.Parameters.AddWithValue("@DeliveringStatus", (int)DeliveryEventState.Delivering);
            update.Parameters.AddWithValue("@LockToken", row.LockToken.ToString("D"));
            update.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
            update.Parameters.AddWithValue("@AttemptCount", 2);
            Assert.Equal(0, await update.ExecuteNonQueryAsync(ct));
        }

        await harness.Sweep.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, ct);

        var state = await ReadFullStateAsync(harness.ConnectionString, row.Id, ct);
        Assert.Equal(DeliveryEventState.DeadLettered, state.Status);
        Assert.Equal(3, state.AttemptCount);
        Assert.Null(state.LockToken);
    }

    [Fact]
    public async Task Dead_lettered_row_appears_in_admin_list_and_operational_gauge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ReaperDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var row = await SeedDeliveringAsync(
            db.ConnectionString,
            attemptCount: 3,
            maxAttempts: 3,
            lockExpiresAt: FixedNow.AddMinutes(-1),
            ct);

        await repository.DeadLetterExpiredDeliveringAtMaxAttemptsAsync(FixedNow, 10, ct);

        var page = await repository.ListDeadLettersForAdminAsync(
            new AdminWebhookDeadLetterListQuery { PageSize = 25 },
            ct);
        var listed = Assert.Single(page.Rows);
        Assert.Equal(row.Id, listed.EventId);
        Assert.Equal(DeliveryEventRepository.LeaseExpiredMaxAttemptsErrorCode, listed.LastErrorCode);

        Assert.Equal(1, await repository.CountDeadLettersForAdminAsync(cancellationToken: ct));
        var operational = await repository.CountOperationalAsync(ct);
        Assert.Equal(1, operational.DeadLetteredCount);

        var metricsBody = PrometheusMetricsFormatter.Format(
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
            new MailerRuntimeMetrics().CaptureSnapshot(),
            webhookEventsPending: operational.PendingCount,
            webhookEventsDeadLettered: operational.DeadLetteredCount);
        Assert.Contains("mail_webhook_events_dead_lettered 1", metricsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("WEBHOOK_LEASE_EXPIRED_MAX_ATTEMPTS", metricsBody, StringComparison.Ordinal);
        Assert.DoesNotContain(row.Id.ToString("D"), metricsBody, StringComparison.Ordinal);
    }

    private static async Task<SeededEvent> SeedDeliveringAsync(
        string connectionString,
        int attemptCount,
        int maxAttempts,
        DateTimeOffset lockExpiresAt,
        CancellationToken cancellationToken,
        Guid? lockToken = null)
    {
        var token = lockToken ?? Guid.CreateVersion7(FixedNow);
        var id = await SeedEventAsync(
            connectionString,
            DeliveryEventState.Delivering,
            attemptCount,
            maxAttempts,
            token,
            lockExpiresAt,
            cancellationToken);
        return new SeededEvent(id, token);
    }

    private static async Task<Guid> SeedEventAsync(
        string connectionString,
        DeliveryEventState status,
        int attemptCount,
        int maxAttempts,
        Guid lockToken,
        DateTimeOffset lockExpiresAt,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.CreateVersion7(FixedNow);
        var mailRequestId = Guid.CreateVersion7(FixedNow);
        var claimedAt = FixedNow.AddMinutes(-2);
        var payload = new MailDeliveryEventPayload
        {
            EventId = eventId,
            EventType = MailDeliveryEventType.Delivered,
            OccurredAt = FixedNow,
            TenantId = TenantId,
            SourceService = "example-service",
            MailRequestId = mailRequestId,
            Status = MailDeliveryEventType.Delivered,
            AttemptCount = 1,
        };
        var payloadJson = JsonSerializer.Serialize(
            payload,
            MailerContractsJsonContext.Default.MailDeliveryEventPayload);

        await using var connection = new SqliteConnection(connectionString);
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
        command.Parameters.AddWithValue("@TenantId", TenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(claimedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(claimedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return eventId;
    }

    private static async Task<EventState> ReadFullStateAsync(
        string connectionString,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, next_attempt_at, lock_token, lock_expires_at,
                   last_error_code, completed_at, updated_at
            FROM delivery_events
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new EventState(
            (DeliveryEventState)reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : SqliteTime.FromStorage(reader.GetString(2)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6)),
            SqliteTime.FromStorage(reader.GetString(7)));
    }

    private sealed record SeededEvent(Guid Id, Guid LockToken);

    private sealed record EventState(
        DeliveryEventState Status,
        int AttemptCount,
        DateTimeOffset? NextAttemptAt,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt,
        string? LastErrorCode,
        DateTimeOffset? CompletedAt,
        DateTimeOffset UpdatedAt);

    private sealed class ReaperDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private ReaperDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<ReaperDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-lease-reaper",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connectionString = $"Data Source={Path.Combine(root, "mailer.db")}";
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new ReaperDatabase(root, factory, connectionString);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SweepHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ILoggerFactory _loggerFactory;

        private SweepHarness(
            string root,
            ILoggerFactory loggerFactory,
            SqliteConnectionFactory factory,
            DeliveryEventRepository repository,
            WebhookDeliverySweepService sweep,
            CapturingLoggerProvider logCapture,
            RecordingWebhookHandler webhookHandler,
            string connectionString)
        {
            _root = root;
            _loggerFactory = loggerFactory;
            Factory = factory;
            Repository = repository;
            Sweep = sweep;
            LogCapture = logCapture;
            WebhookHandler = webhookHandler;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public DeliveryEventRepository Repository { get; }

        public WebhookDeliverySweepService Sweep { get; }

        public CapturingLoggerProvider LogCapture { get; }

        public RecordingWebhookHandler WebhookHandler { get; }

        public string ConnectionString { get; }

        public static Task<SweepHarness> CreateAsync(CancellationToken cancellationToken) =>
            CreateAsync(reconcileBatchSize: 8, cancellationToken);

        public static async Task<SweepHarness> CreateAsync(
            int reconcileBatchSize,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-lease-sweep",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connectionString = $"Data Source={Path.Combine(root, "mailer.db")}";
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);

            var repository = new DeliveryEventRepository(factory);
            var webhookHandler = new RecordingWebhookHandler();
            var logCapture = new CapturingLoggerProvider();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
            var webhookOptions = new MailerWebhookOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
                DeliveryTimeoutSeconds = 2,
                LeaseDurationSeconds = 20,
                ReconcileBatchSize = reconcileBatchSize,
            };

            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(
                tenantConfigPath,
                $$"""
                {
                  "version": 1,
                  "environment": "develop",
                  "tenants": [
                    {
                      "tenant_id": "{{TenantId}}",
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
                """,
                cancellationToken);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                    ["MAIL_SERVICE_TOKEN"] = "local-mail-service-token",
                })
                .Build();
            var tenantRegistry = MailerTenantRegistry.Load(configuration, "Testing");
            var queue = new WebhookDeliveryQueue();
            var enqueuer = new DeliveryEventEnqueuer(
                tenantRegistry,
                repository,
                webhookOptions,
                queue,
                new FixedUtcTimeProvider(FixedNow),
                NullLogger<DeliveryEventEnqueuer>.Instance);
            var sweep = new WebhookDeliverySweepService(
                repository,
                enqueuer,
                webhookOptions,
                queue,
                new FixedUtcTimeProvider(FixedNow),
                loggerFactory.CreateLogger<WebhookDeliverySweepService>());

            return new SweepHarness(
                root,
                loggerFactory,
                factory,
                repository,
                sweep,
                logCapture,
                webhookHandler,
                connectionString);
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
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
