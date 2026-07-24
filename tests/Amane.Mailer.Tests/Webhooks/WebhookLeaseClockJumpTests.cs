using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Webhooks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Store-level coverage for #276: webhook leases compare absolute wall-clock
/// <c>lock_expires_at</c> to the caller's <c>now</c>. Unlike mail (#238), webhook
/// finalize has no prior-success converge path when fencing fails.
/// </summary>
public sealed class WebhookLeaseClockJumpTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset ClaimNow = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Forward_clock_jump_past_lock_expires_allows_early_reclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var heldToken = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var eventId = await SeedDeliveringAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        var jumpedNow = lockExpiresAt.AddSeconds(1);
        var claimed = await repository.TryClaimOneAsync(jumpedNow, LeaseDuration, ct);

        Assert.NotNull(claimed);
        Assert.Equal(eventId, claimed!.Id);
        Assert.Equal(2, claimed.AttemptCount);
        Assert.NotEqual(heldToken, claimed.LockToken);
        Assert.Equal(jumpedNow.Add(LeaseDuration), claimed.LockExpiresAt);
    }

    [Fact]
    public async Task Backward_clock_jump_keeps_active_lease_from_reclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var heldToken = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var eventId = await SeedDeliveringAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        var jumpedNow = ClaimNow.AddHours(-1);
        var claimed = await repository.TryClaimOneAsync(jumpedNow, LeaseDuration, ct);

        Assert.Null(claimed);

        var state = await ReadStateAsync(db.ConnectionString, eventId, ct);
        Assert.Equal(DeliveryEventState.Delivering, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(heldToken, state.LockToken);
        Assert.Equal(lockExpiresAt, state.LockExpiresAt);
    }

    [Fact]
    public async Task Forward_clock_jump_fails_strict_finalize_and_leaves_delivering()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var heldToken = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var eventId = await SeedDeliveringAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        var jumpedNow = lockExpiresAt.AddSeconds(30);
        var finalized = await repository.FinalizeAsync(
            eventId,
            heldToken,
            jumpedNow,
            DeliveryEventFinalizeOutcome.Delivered,
            nextAttemptAt: null,
            lastErrorCode: null,
            ct);

        Assert.False(finalized);

        var state = await ReadStateAsync(db.ConnectionString, eventId, ct);
        Assert.Equal(DeliveryEventState.Delivering, state.Status);
        Assert.Equal(heldToken, state.LockToken);
        Assert.Equal(lockExpiresAt, state.LockExpiresAt);
    }

    private static async Task<Guid> SeedDeliveringAsync(
        string connectionString,
        DateTimeOffset claimedAt,
        Guid lockToken,
        DateTimeOffset lockExpiresAt,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.CreateVersion7(claimedAt);
        var mailRequestId = Guid.CreateVersion7(claimedAt);
        var createdAt = claimedAt.AddMinutes(-1);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_events (
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, next_attempt_at,
                lock_token, lock_expires_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'lease-clock-jump-webhook', @MailRequestId, 'delivered',
                @PayloadJson, @Status, @AttemptCount, 3, NULL,
                @LockToken, @LockExpiresAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", Guid.Parse("00000000-0000-0000-0000-000000000276").ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue(
            "@PayloadJson",
            """{"event_id":"00000000-0000-0000-0000-000000000276"}""");
        command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.Delivering);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(claimedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return eventId;
    }

    private static async Task<(
        DeliveryEventState Status,
        int AttemptCount,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt)> ReadStateAsync(
        string connectionString,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, lock_token, lock_expires_at
            FROM delivery_events
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (
            (DeliveryEventState)reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)));
    }

    private sealed class ClockJumpDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private ClockJumpDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<ClockJumpDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-lease-clock-jump",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new ClockJumpDatabase(root, factory, connectionString);
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
}
