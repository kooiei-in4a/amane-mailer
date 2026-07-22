using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression coverage for ADR 0015 D-04: manual cancel and worker claim both target
/// stale Processing rows and must serialize so exactly one update wins (#243).
/// </summary>
public sealed class MailRequestCancelClaimRaceTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Manual_cancel_before_claim_on_stale_processing_cancels_and_blocks_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await RaceTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var auditRepository = new AdminAuditRepository(db.Factory);
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var staleLockToken = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var claimLockToken = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var internalId = await SeedStaleProcessingAsync(db.ConnectionString, now, staleLockToken, attemptCount: 1, ct);

        var cancel = await repository.TryManualCancelAsync(
            internalId,
            allowedTenantIds: null,
            now,
            auditRepository,
            CreateCancelAuditTemplate(internalId, now),
            ct);
        var claimed = await repository.TryClaimOneAsync(now, LeaseDuration, claimLockToken, ct);

        Assert.Equal(ManualMailRequestMutationStatus.Succeeded, cancel.Status);
        Assert.Null(claimed);

        var state = await ReadStateAsync(db.ConnectionString, internalId, ct);
        Assert.Equal(MailRequestState.Cancelled, state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.LockToken);
        Assert.Null(state.LockExpiresAt);
        Assert.Equal(MailRequestRepository.OperatorCancelledLastErrorMessage, state.LastErrorMessage);
    }

    [Fact]
    public async Task Claim_before_manual_cancel_on_stale_processing_claims_and_rejects_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await RaceTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var auditRepository = new AdminAuditRepository(db.Factory);
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var staleLockToken = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var claimLockToken = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var internalId = await SeedStaleProcessingAsync(db.ConnectionString, now, staleLockToken, attemptCount: 1, ct);

        var claimed = await repository.TryClaimOneAsync(now, LeaseDuration, claimLockToken, ct);
        var cancel = await repository.TryManualCancelAsync(
            internalId,
            allowedTenantIds: null,
            now,
            auditRepository,
            CreateCancelAuditTemplate(internalId, now),
            ct);

        Assert.NotNull(claimed);
        Assert.Equal(internalId, claimed!.Id);
        Assert.Equal(claimLockToken, claimed.LockToken);
        Assert.Equal(2, claimed.AttemptCount);
        Assert.Equal(ManualMailRequestMutationStatus.LockHeld, cancel.Status);

        var state = await ReadStateAsync(db.ConnectionString, internalId, ct);
        Assert.Equal(MailRequestState.Processing, state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal(claimLockToken, state.LockToken);
        Assert.Equal(now.Add(LeaseDuration), state.LockExpiresAt);
    }

    [Fact]
    public async Task Concurrent_manual_cancel_and_claim_on_stale_processing_have_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await RaceTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);
        var auditRepository = new AdminAuditRepository(db.Factory);
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var staleLockToken = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var claimLockToken = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var internalId = await SeedStaleProcessingAsync(db.ConnectionString, now, staleLockToken, attemptCount: 1, ct);

        using var ready = new Barrier(2);
        var cancelTask = Task.Run(async () =>
        {
            ready.SignalAndWait(ct);
            return await repository.TryManualCancelAsync(
                internalId,
                allowedTenantIds: null,
                now,
                auditRepository,
                CreateCancelAuditTemplate(internalId, now),
                ct);
        }, ct);
        var claimTask = Task.Run(async () =>
        {
            ready.SignalAndWait(ct);
            return await repository.TryClaimOneAsync(now, LeaseDuration, claimLockToken, ct);
        }, ct);

        await Task.WhenAll(cancelTask, claimTask);
        var cancel = await cancelTask;
        var claimed = await claimTask;

        var cancelWon = cancel.Status == ManualMailRequestMutationStatus.Succeeded;
        var claimWon = claimed is not null;
        Assert.NotEqual(cancelWon, claimWon);

        var state = await ReadStateAsync(db.ConnectionString, internalId, ct);
        if (cancelWon)
        {
            Assert.Equal(ManualMailRequestMutationStatus.Succeeded, cancel.Status);
            Assert.Null(claimed);
            Assert.Equal(MailRequestState.Cancelled, state.Status);
            Assert.Equal(1, state.AttemptCount);
            Assert.Null(state.LockToken);
            Assert.Null(state.LockExpiresAt);
            Assert.Equal(MailRequestRepository.OperatorCancelledLastErrorMessage, state.LastErrorMessage);
            return;
        }

        Assert.Equal(ManualMailRequestMutationStatus.LockHeld, cancel.Status);
        Assert.NotNull(claimed);
        Assert.Equal(internalId, claimed!.Id);
        Assert.Equal(claimLockToken, claimed.LockToken);
        Assert.Equal(MailRequestState.Processing, state.Status);
        Assert.Equal(2, state.AttemptCount);
        Assert.Equal(claimLockToken, state.LockToken);
        Assert.Equal(now.Add(LeaseDuration), state.LockExpiresAt);
    }

    private static AdminAuditEvent CreateCancelAuditTemplate(Guid internalId, DateTimeOffset now) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.ManualCancelRequested,
            Actor = "race-test-admin",
            OccurredAt = now,
            TargetType = AdminAuditLog.TargetTypes.MailRequest,
            TargetId = internalId.ToString("D"),
            Result = AdminAuditLog.Results.Success,
        };

    private static async Task<Guid> SeedStaleProcessingAsync(
        string connectionString,
        DateTimeOffset now,
        Guid staleLockToken,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var internalId = Guid.CreateVersion7(now);
        var acceptedAt = now.AddMinutes(-10);
        var lockExpiredAt = now.AddMinutes(-1);

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
                @Id, @TenantId, 'cancel-claim-race-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'race-test@example.com',
                @Status, @AttemptCount, 3, @LockToken, @LockExpiresAt,
                @AcceptedAt, @AcceptedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", Guid.Parse("00000000-0000-0000-0000-000000000101").ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.CreateVersion7(now).ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@LockToken", staleLockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiredAt));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(acceptedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(lockExpiredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return internalId;
    }

    private static async Task<(
        MailRequestState Status,
        int AttemptCount,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt,
        string? LastErrorMessage)> ReadStateAsync(
        string connectionString,
        Guid internalId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, attempt_count, lock_token, lock_expires_at, last_error_message
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
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private sealed class RaceTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private RaceTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<RaceTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cancel-claim-race", Guid.NewGuid().ToString("N"));
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
            return new RaceTestDatabase(root, factory, connectionString);
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
