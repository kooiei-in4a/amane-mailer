using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression coverage for the Issue #546 maintainer decision: manual cancel is an
/// administrative terminal transition for Failed plain requests and must not rewrite
/// submission evidence, recipient disposition, or attempt history.
/// </summary>
public sealed class MailRequestManualCancelWithPlainEvidenceTests
{
    [Fact]
    public async Task Failed_plain_request_with_evidence_can_be_cancelled_without_mutating_delivery_history()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var auditRepository = new AdminAuditRepository(database.Factory);
        var now = new DateTimeOffset(2026, 8, 6, 3, 30, 0, TimeSpan.Zero);
        var requestId = Guid.CreateVersion7(now.AddMinutes(-5));

        await SeedFailedRequestWithEvidenceAsync(database.ConnectionString, requestId, now, ct);
        var evidenceBefore = await ReadEvidenceAsync(database.ConnectionString, requestId, ct);
        var recipientBefore = await ReadRecipientAsync(database.ConnectionString, requestId, ct);
        var attemptBefore = await ReadAttemptAsync(database.ConnectionString, requestId, ct);

        var result = await repository.TryManualCancelAsync(
            requestId,
            allowedTenantIds: null,
            now,
            auditRepository,
            CreateCancelAuditTemplate(requestId, now),
            ct);

        Assert.Equal(ManualMailRequestMutationStatus.Succeeded, result.Status);

        var request = await ReadRequestAsync(database.ConnectionString, requestId, ct);
        Assert.Equal(MailRequestState.Cancelled, request.Status);
        Assert.Equal(MailRequestRepository.OperatorCancelledLastErrorMessage, request.LastErrorMessage);
        Assert.Null(request.LockToken);
        Assert.Null(request.LockExpiresAt);

        Assert.Equal(evidenceBefore, await ReadEvidenceAsync(database.ConnectionString, requestId, ct));
        Assert.Equal(recipientBefore, await ReadRecipientAsync(database.ConnectionString, requestId, ct));
        Assert.Equal(attemptBefore, await ReadAttemptAsync(database.ConnectionString, requestId, ct));
        Assert.Equal(1L, await CountAttemptsAsync(database.ConnectionString, requestId, ct));

        var audit = await ReadLatestCancelAuditAsync(database.ConnectionString, requestId, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
        Assert.Null(audit.Value.ErrorCode);
    }

    [Fact]
    public async Task Expired_processing_plain_request_with_started_evidence_remains_not_cancellable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var auditRepository = new AdminAuditRepository(database.Factory);
        var now = new DateTimeOffset(2026, 8, 6, 3, 30, 0, TimeSpan.Zero);
        var requestId = Guid.CreateVersion7(now.AddMinutes(-5));

        await SeedExpiredProcessingRequestWithStartedEvidenceAsync(
            database.ConnectionString,
            requestId,
            now,
            ct);
        var evidenceBefore = await ReadEvidenceAsync(database.ConnectionString, requestId, ct);
        var recipientBefore = await ReadRecipientAsync(database.ConnectionString, requestId, ct);

        var result = await repository.TryManualCancelAsync(
            requestId,
            allowedTenantIds: null,
            now,
            auditRepository,
            CreateCancelAuditTemplate(requestId, now),
            ct);

        Assert.Equal(ManualMailRequestMutationStatus.InvalidState, result.Status);

        var request = await ReadRequestAsync(database.ConnectionString, requestId, ct);
        Assert.Equal(MailRequestState.Processing, request.Status);
        Assert.NotNull(request.LockToken);
        Assert.NotNull(request.LockExpiresAt);
        Assert.Equal(evidenceBefore, await ReadEvidenceAsync(database.ConnectionString, requestId, ct));
        Assert.Equal(recipientBefore, await ReadRecipientAsync(database.ConnectionString, requestId, ct));
        Assert.Equal(0L, await CountAttemptsAsync(database.ConnectionString, requestId, ct));

        var audit = await ReadLatestCancelAuditAsync(database.ConnectionString, requestId, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Failure, audit.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.InvalidState, audit.Value.ErrorCode);
    }

    private static AdminAuditEvent CreateCancelAuditTemplate(Guid requestId, DateTimeOffset now) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.ManualCancelRequested,
            Actor = "plain-evidence-cancel-test-admin",
            OccurredAt = now,
            TargetType = AdminAuditLog.TargetTypes.MailRequest,
            TargetId = requestId.ToString("D"),
            Result = AdminAuditLog.Results.Success,
        };

    private static async Task SeedFailedRequestWithEvidenceAsync(
        string connectionString,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var claimToken = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var acceptedAt = now.AddMinutes(-10);
        var startedAt = now.AddMinutes(-8);
        var resolvedAt = now.AddMinutes(-7);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await InsertRequestAsync(
            connection,
            transaction,
            requestId,
            tenantId,
            MailRequestState.Failed,
            acceptedAt,
            lockToken: null,
            lockExpiresAt: null,
            completedAt: resolvedAt,
            lastErrorMessage: "provider rejected",
            cancellationToken);

        await InsertRecipientAsync(
            connection,
            transaction,
            requestId,
            MailRecipientDeliveryState.Failed,
            providerMessageId: "provider-message-123",
            providerStatusDetail: "definitive_rejection",
            acceptedAt,
            cancellationToken);

        await InsertEvidenceAsync(
            connection,
            transaction,
            requestId,
            MailPlainSubmissionEvidenceState.DefinitelyRejected,
            claimToken,
            startedAt,
            providerMessageId: "provider-message-123",
            resolvedAt,
            cancellationToken);

        await using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText = """
                INSERT INTO mail_attempts (
                    request_id, attempt_number, provider, status,
                    provider_message_id, error_code, error_message, retryable,
                    lock_token, started_at, completed_at)
                VALUES (
                    @RequestId, 1, 'mailpit', @Status,
                    'provider-message-123', 'PROVIDER_REJECTED', 'provider rejected', 0,
                    @LockToken, @StartedAt, @CompletedAt);
                """;
            attempt.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            attempt.Parameters.AddWithValue("@Status", (int)MailRequestState.Failed);
            attempt.Parameters.AddWithValue("@LockToken", claimToken.ToString("D"));
            attempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(startedAt));
            attempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(resolvedAt));
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SeedExpiredProcessingRequestWithStartedEvidenceAsync(
        string connectionString,
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var claimToken = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var acceptedAt = now.AddMinutes(-10);
        var startedAt = now.AddMinutes(-8);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await InsertRequestAsync(
            connection,
            transaction,
            requestId,
            tenantId,
            MailRequestState.Processing,
            acceptedAt,
            claimToken,
            now.AddMinutes(-1),
            completedAt: null,
            lastErrorMessage: null,
            cancellationToken);

        await InsertRecipientAsync(
            connection,
            transaction,
            requestId,
            MailRecipientDeliveryState.Pending,
            providerMessageId: null,
            providerStatusDetail: null,
            acceptedAt,
            cancellationToken);

        await InsertEvidenceAsync(
            connection,
            transaction,
            requestId,
            MailPlainSubmissionEvidenceState.Started,
            claimToken,
            startedAt,
            providerMessageId: null,
            resolvedAt: null,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid requestId,
        Guid tenantId,
        MailRequestState status,
        DateTimeOffset acceptedAt,
        Guid? lockToken,
        DateTimeOffset? lockExpiresAt,
        DateTimeOffset? completedAt,
        string? lastErrorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, attachment_count,
                lock_token, lock_expires_at,
                accepted_at, created_at, updated_at, completed_at, failed_at,
                last_error_message)
            VALUES (
                @Id, @TenantId, 'plain-evidence-cancel-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 1, 3, 0,
                @LockToken, @LockExpiresAt,
                @AcceptedAt, @AcceptedAt, @UpdatedAt, @CompletedAt, @FailedAt,
                @LastErrorMessage);
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('f', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@LockToken", lockToken is null ? DBNull.Value : lockToken.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "@LockExpiresAt",
            lockExpiresAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(lockExpiresAt.Value));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(acceptedAt));
        command.Parameters.AddWithValue(
            "@UpdatedAt",
            SqliteTime.ToStorageUtc(completedAt ?? lockExpiresAt ?? acceptedAt));
        command.Parameters.AddWithValue(
            "@CompletedAt",
            completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
        command.Parameters.AddWithValue(
            "@FailedAt",
            status == MailRequestState.Failed && completedAt is not null
                ? SqliteTime.ToStorageUtc(completedAt.Value)
                : DBNull.Value);
        command.Parameters.AddWithValue("@LastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRecipientAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid requestId,
        MailRecipientDeliveryState deliveryState,
        string? providerMessageId,
        string? providerStatusDetail,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key, display_name,
                delivery_state, provider_message_id, provider_status_detail,
                created_at, updated_at)
            VALUES (
                @RequestId, 0, 0, 'user@example.com', 'user@example.com', 'User',
                @DeliveryState, @ProviderMessageId, @ProviderStatusDetail,
                @Now, @Now);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@DeliveryState", (int)deliveryState);
        command.Parameters.AddWithValue("@ProviderMessageId", (object?)providerMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProviderStatusDetail", (object?)providerStatusDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid requestId,
        MailPlainSubmissionEvidenceState evidenceState,
        Guid claimToken,
        DateTimeOffset startedAt,
        string? providerMessageId,
        DateTimeOffset? resolvedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mail_plain_submissions (
                request_id, evidence_state, evidence_origin, provider, claim_token,
                started_at, provider_message_id, resolved_at, created_at, updated_at)
            VALUES (
                @RequestId, @EvidenceState, @EvidenceOrigin, 'mailpit', @ClaimToken,
                @StartedAt, @ProviderMessageId, @ResolvedAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@EvidenceState", (int)evidenceState);
        command.Parameters.AddWithValue("@EvidenceOrigin", (int)MailPlainSubmissionEvidenceOrigin.Runtime);
        command.Parameters.AddWithValue("@ClaimToken", claimToken.ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(startedAt));
        command.Parameters.AddWithValue("@ProviderMessageId", (object?)providerMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@ResolvedAt",
            resolvedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(resolvedAt.Value));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(startedAt));
        command.Parameters.AddWithValue(
            "@UpdatedAt",
            SqliteTime.ToStorageUtc(resolvedAt ?? startedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RequestSnapshot> ReadRequestAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, last_error_message, lock_token, lock_expires_at
            FROM mail_requests
            WHERE id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new(
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)));
    }

    private static async Task<EvidenceSnapshot> ReadEvidenceAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_state, evidence_origin, provider, claim_token, started_at,
                   provider_message_id, resolved_at, created_at, updated_at
            FROM mail_plain_submissions
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8));
    }

    private static async Task<RecipientSnapshot> ReadRecipientAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT delivery_state, provider_message_id, provider_status_detail, updated_at
            FROM mail_request_recipients
            WHERE request_id = @RequestId AND recipient_role = 0 AND ordinal = 0;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<AttemptSnapshot> ReadAttemptAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_number, provider, status, provider_message_id, error_code,
                   error_message, retryable, lock_token, started_at, completed_at
            FROM mail_attempts
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9));
    }

    private static async Task<long> CountAttemptsAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_attempts WHERE request_id = @RequestId;";
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(string Result, string? ErrorCode)?> ReadLatestCancelAuditAsync(
        string connectionString,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT result, error_code
            FROM admin_audit_events
            WHERE target_id = @TargetId AND event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@TargetId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@EventType", AdminAuditLog.EventTypes.ManualCancelRequested);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed record RequestSnapshot(
        MailRequestState Status,
        string? LastErrorMessage,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt);

    private sealed record EvidenceSnapshot(
        int EvidenceState,
        int EvidenceOrigin,
        string? Provider,
        string? ClaimToken,
        string? StartedAt,
        string? ProviderMessageId,
        string? ResolvedAt,
        string CreatedAt,
        string UpdatedAt);

    private sealed record RecipientSnapshot(
        int DeliveryState,
        string? ProviderMessageId,
        string? ProviderStatusDetail,
        string UpdatedAt);

    private sealed record AttemptSnapshot(
        int AttemptNumber,
        string Provider,
        int Status,
        string? ProviderMessageId,
        string? ErrorCode,
        string? ErrorMessage,
        int Retryable,
        string LockToken,
        string StartedAt,
        string CompletedAt);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private TestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-plain-evidence-cancel",
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
            return new TestDatabase(root, factory, connectionString);
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
