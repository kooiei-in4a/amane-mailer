using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Store-level coverage for #276: mail leases compare absolute wall-clock
/// <c>lock_expires_at</c> to the caller's <c>now</c> (from <see cref="TimeProvider"/>).
/// Passing a jumped <c>now</c> reproduces forward/backward host clock corrections.
/// </summary>
public sealed class MailRequestLeaseClockJumpTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset ClaimNow = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Forward_clock_jump_past_lock_expires_allows_early_reclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var heldToken = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var reclaimToken = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var internalId = await SeedProcessingAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        // Wall clock jumps forward past the absolute lease deadline.
        var jumpedNow = lockExpiresAt.AddSeconds(1);
        var claimed = await repository.TryClaimOneAsync(jumpedNow, LeaseDuration, reclaimToken, ct);

        Assert.NotNull(claimed);
        Assert.Equal(internalId, claimed!.Id);
        Assert.Equal(reclaimToken, claimed.LockToken);
        Assert.Equal(2, claimed.AttemptCount);
        Assert.Equal(jumpedNow.Add(LeaseDuration), claimed.LockExpiresAt);
    }

    [Fact]
    public async Task Backward_clock_jump_keeps_active_lease_from_reclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var heldToken = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var reclaimToken = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var internalId = await SeedProcessingAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        // Wall clock jumps backward; absolute lock_expires_at is still in the future.
        var jumpedNow = ClaimNow.AddHours(-1);
        var claimed = await repository.TryClaimOneAsync(jumpedNow, LeaseDuration, reclaimToken, ct);

        Assert.Null(claimed);

        var state = await ReadStateAsync(db.ConnectionString, internalId, ct);
        Assert.Equal(MailRequestState.Processing, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(heldToken, state.LockToken);
        Assert.Equal(lockExpiresAt, state.LockExpiresAt);
    }

    [Fact]
    public async Task Forward_clock_jump_fails_strict_finalize_but_delivered_converges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ClockJumpDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var heldToken = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var lockExpiresAt = ClaimNow.Add(LeaseDuration);
        var internalId = await SeedProcessingAsync(
            db.ConnectionString,
            ClaimNow,
            heldToken,
            lockExpiresAt,
            attemptCount: 1,
            ct);

        var jumpedNow = lockExpiresAt.AddSeconds(30);
        var converged = await repository.FinalizeAsync(
            internalId,
            heldToken,
            jumpedNow,
            MailRequestFinalizeOutcome.Delivered,
            nextAttemptAt: null,
            lastErrorMessage: null,
            new MailAttemptInsert
            {
                RequestId = internalId,
                AttemptNumber = 1,
                Provider = "mailpit",
                Status = MailRequestState.Delivered,
                ProviderMessageId = "clock-jump-provider-message",
                LockToken = heldToken,
                Retryable = false,
                StartedAt = ClaimNow,
                CompletedAt = jumpedNow,
            },
            ct);

        // Strict fencing fails (lock_expires_at > @Now is false), but #238 still
        // persists Delivered evidence and best-effort completes under the same lock.
        Assert.True(converged);

        var state = await ReadStateAsync(db.ConnectionString, internalId, ct);
        Assert.Equal(MailRequestState.Delivered, state.Status);
        Assert.Null(state.LockToken);
        Assert.Null(state.LockExpiresAt);

        var evidence = await repository.FindSuccessfulDeliveryAttemptAsync(internalId, ct);
        Assert.NotNull(evidence);
        Assert.Equal("clock-jump-provider-message", evidence!.ProviderMessageId);
    }

    private static async Task<Guid> SeedProcessingAsync(
        string connectionString,
        DateTimeOffset claimedAt,
        Guid lockToken,
        DateTimeOffset lockExpiresAt,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var internalId = Guid.CreateVersion7(claimedAt);
        var acceptedAt = claimedAt.AddMinutes(-1);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, lock_token, lock_expires_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'lease-clock-jump-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'lease-clock-jump@example.com',
                @Status, @AttemptCount, 3, @LockToken, @LockExpiresAt,
                @AcceptedAt, @AcceptedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", Guid.Parse("00000000-0000-0000-0000-000000000276").ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.CreateVersion7(claimedAt).ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(acceptedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(claimedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return internalId;
    }

    private static async Task<(
        MailRequestState Status,
        int AttemptCount,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt)> ReadStateAsync(
        string connectionString,
        Guid internalId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, lock_token, lock_expires_at
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (
            (MailRequestState)reader.GetInt32(0),
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
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-lease-clock-jump", Guid.NewGuid().ToString("N"));
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
