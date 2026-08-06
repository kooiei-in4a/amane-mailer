using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
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
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var claimToken = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId), ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, claimToken, startedAt.AddMinutes(5), ct);
        var prepared = await repository.TryPreparePlainProviderInvocationAsync(
            requestId,
            tenantId,
            "mailpit",
            claimToken,
            attemptNumber: 1,
            ct);
        Assert.Equal(PlainProviderInvocationOutcome.Started, prepared.Outcome);

        var evidenceClaimToken = Assert.IsType<Guid>(
            (await repository.FindPlainSubmissionAsync(requestId, ct))?.ClaimToken);
        var finalizedAt = DateTimeOffset.UtcNow;
        Assert.True(await repository.FinalizePlainSubmissionAsync(
            requestId,
            claimToken,
            evidenceClaimToken,
            MailPlainSubmissionEvidenceState.Started,
            finalizedAt,
            MailPlainSubmissionEvidenceState.DefinitelyRejected,
            providerMessageId: "provider-message-123",
            requestTerminalState: MailRequestState.Failed,
            recipientTargetState: MailRecipientDeliveryState.Failed,
            lastErrorMessage: "provider rejected",
            FailedAttempt(requestId, claimToken, startedAt, finalizedAt),
            ct));

        var evidenceBefore = await repository.FindPlainSubmissionAsync(requestId, ct);
        var recipientsBefore = await repository.ListRecipientsAsync(requestId, ct);
        var attemptBefore = await ReadAttemptAsync(database.Factory, requestId, ct);

        var cancelAt = DateTimeOffset.UtcNow;
        var result = await repository.TryManualCancelAsync(
            requestId,
            allowedTenantIds: null,
            cancelAt,
            auditRepository,
            CreateCancelAuditTemplate(requestId, cancelAt),
            ct);

        Assert.Equal(ManualMailRequestMutationStatus.Succeeded, result.Status);
        var request = await ReadRequestAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Cancelled, request.Status);
        Assert.Equal(MailRequestRepository.OperatorCancelledLastErrorMessage, request.LastErrorMessage);
        Assert.Null(request.LockToken);
        Assert.Null(request.LockExpiresAt);

        Assert.Equal(evidenceBefore, await repository.FindPlainSubmissionAsync(requestId, ct));
        Assert.Equal(recipientsBefore, await repository.ListRecipientsAsync(requestId, ct));
        Assert.Equal(attemptBefore, await ReadAttemptAsync(database.Factory, requestId, ct));
        Assert.Equal(1L, await CountAttemptsAsync(database.Factory, requestId, ct));

        var audit = await ReadLatestCancelAuditAsync(database.Factory, requestId, ct);
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
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var claimToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId), ct);
        await ClaimAsProcessingAsync(
            database.Factory,
            requestId,
            claimToken,
            DateTimeOffset.UtcNow.AddMinutes(5),
            ct);
        var prepared = await repository.TryPreparePlainProviderInvocationAsync(
            requestId,
            tenantId,
            "mailpit",
            claimToken,
            attemptNumber: 1,
            ct);
        Assert.Equal(PlainProviderInvocationOutcome.Started, prepared.Outcome);
        await ExpireLeaseAsync(database.Factory, requestId, ct);

        var evidenceBefore = await repository.FindPlainSubmissionAsync(requestId, ct);
        var recipientsBefore = await repository.ListRecipientsAsync(requestId, ct);
        var cancelAt = DateTimeOffset.UtcNow;
        var result = await repository.TryManualCancelAsync(
            requestId,
            allowedTenantIds: null,
            cancelAt,
            auditRepository,
            CreateCancelAuditTemplate(requestId, cancelAt),
            ct);

        Assert.Equal(ManualMailRequestMutationStatus.InvalidState, result.Status);
        var request = await ReadRequestAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Processing, request.Status);
        Assert.Equal(claimToken, request.LockToken);
        Assert.NotNull(request.LockExpiresAt);
        Assert.Equal(evidenceBefore, await repository.FindPlainSubmissionAsync(requestId, ct));
        Assert.Equal(recipientsBefore, await repository.ListRecipientsAsync(requestId, ct));
        Assert.Equal(0L, await CountAttemptsAsync(database.Factory, requestId, ct));

        var audit = await ReadLatestCancelAuditAsync(database.Factory, requestId, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Failure, audit.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.InvalidState, audit.Value.ErrorCode);
    }

    private static AcceptedMailRequestInsert CreateInsert(Guid requestId, Guid tenantId) =>
        new()
        {
            Id = requestId,
            TenantId = tenantId,
            SourceService = "plain-evidence-cancel-test",
            MailRequestId = Guid.NewGuid(),
            Purpose = "test",
            PayloadJson = "{}",
            PayloadHash = new string('a', 64),
            Subject = "subject",
            RecipientEmail = "user@example.com",
            RecipientDisplayName = "User",
            MaxAttempts = 3,
            AcceptedAt = DateTimeOffset.UtcNow,
            Recipients =
            [
                new CanonicalMailRecipient
                {
                    Role = MailRecipientRole.To,
                    Ordinal = 0,
                    Address = "user@example.com",
                    AddressKey = RecipientEmailNormalizer.Normalize("user@example.com"),
                    DisplayName = "User",
                },
            ],
        };

    private static MailAttemptInsert FailedAttempt(
        Guid requestId,
        Guid claimToken,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new()
        {
            RequestId = requestId,
            AttemptNumber = 1,
            Provider = "mailpit",
            Status = MailRequestState.Failed,
            ProviderMessageId = "provider-message-123",
            ErrorCode = "PROVIDER_REJECTED",
            ErrorMessage = "provider rejected",
            Retryable = false,
            LockToken = claimToken,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

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

    private static async Task ClaimAsProcessingAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        Guid lockToken,
        DateTimeOffset lockExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET status = @ProcessingStatus,
                attempt_count = 1,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @Now
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task ExpireLeaseAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_requests SET lock_expires_at = @ExpiredAt WHERE id = @Id;";
        command.Parameters.AddWithValue("@ExpiredAt", SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow.AddMinutes(-1)));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<RequestSnapshot> ReadRequestAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, last_error_message, lock_token, lock_expires_at
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new(
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)));
    }

    private static async Task<AttemptSnapshot> ReadAttemptAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_number, provider, status, provider_message_id, error_code,
                   error_message, retryable, lock_token, started_at, completed_at
            FROM mail_attempts
            WHERE request_id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
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
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_attempts WHERE request_id = @Id;";
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(string Result, string? ErrorCode)?> ReadLatestCancelAuditAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
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
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private sealed record RequestSnapshot(
        MailRequestState Status,
        string? LastErrorMessage,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt);

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

        private TestDatabase(string root, SqliteConnectionFactory factory)
        {
            _root = root;
            Factory = factory;
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-plain-evidence-cancel", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={Path.Combine(root, "mailer.db")}",
                    })
                    .Build());
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new TestDatabase(root, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
