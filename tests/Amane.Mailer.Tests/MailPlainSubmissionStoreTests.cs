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
/// Repository-level coverage for the ADR 0023 D-04/D-05 provider invocation boundary and
/// suppression precheck (Issue #546): <see cref="MailPlainSubmissionStore"/> atomicity, fencing,
/// and recipient state transitions, independent of the full Worker/HTTP pipeline.
/// </summary>
public sealed class MailPlainSubmissionStoreTests
{
    [Fact]
    public async Task Suppressed_recipient_converges_all_or_nothing_with_atomic_recipient_states()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        var recipients = new[]
        {
            Recipient(MailRecipientRole.To, 0, "to@example.com", null),
            Recipient(MailRecipientRole.Cc, 0, "cc-suppressed@example.com", null),
            Recipient(MailRecipientRole.Bcc, 0, "bcc@example.com", null),
        };
        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId, recipients), ct);
        await SeedSuppressionAsync(database.Factory, tenantId, "cc-suppressed@example.com", ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", lockToken, attemptNumber: 1, ct);

        Assert.Equal(PlainProviderInvocationOutcome.Suppressed, result.Outcome);

        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.NotSent, recipientStates[(MailRecipientRole.To, 0)]);
        Assert.Equal(MailRecipientDeliveryState.Suppressed, recipientStates[(MailRecipientRole.Cc, 0)]);
        Assert.Equal(MailRecipientDeliveryState.NotSent, recipientStates[(MailRecipientRole.Bcc, 0)]);

        var evidence = await repository.FindPlainSubmissionAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(MailPlainSubmissionEvidenceState.DefinitelyNotSubmitted, evidence!.EvidenceState);
        Assert.Equal(MailPlainSubmissionEvidenceOrigin.Runtime, evidence.EvidenceOrigin);

        var (status, lastErrorMessage) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Failed, status);
        Assert.DoesNotContain("cc-suppressed@example.com", lastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var attempt = await ReadSingleAttemptAsync(database.Factory, requestId, ct);
        Assert.Equal(MailDeliveryErrorCodes.RecipientSuppressed, attempt.ErrorCode);
        Assert.False(attempt.Retryable);
        Assert.Equal(MailRequestState.Failed, attempt.Status);
        Assert.DoesNotContain("cc-suppressed@example.com", attempt.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suppression_precheck_is_scoped_per_tenant_and_does_not_block_a_different_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var suppressedTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        const string sharedAddress = "shared@example.com";

        await SeedSuppressionAsync(database.Factory, suppressedTenantId, sharedAddress, ct);

        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, otherTenantId, [Recipient(MailRecipientRole.To, 0, sharedAddress, null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, otherTenantId, "mailpit", lockToken, attemptNumber: 1, ct);

        Assert.Equal(PlainProviderInvocationOutcome.Started, result.Outcome);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.Pending, recipientStates[(MailRecipientRole.To, 0)]);
    }

    [Fact]
    public async Task Started_commits_before_provider_call_and_moves_all_recipients_to_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        var recipients = new[]
        {
            Recipient(MailRecipientRole.To, 0, "to-1@example.com", null),
            Recipient(MailRecipientRole.To, 1, "to-2@example.com", null),
            Recipient(MailRecipientRole.Cc, 0, "cc@example.com", null),
        };
        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId, recipients), ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", lockToken, attemptNumber: 1, ct);

        Assert.Equal(PlainProviderInvocationOutcome.Started, result.Outcome);
        Assert.NotNull(result.Recipients);
        Assert.Equal(3, result.Recipients!.Count);

        var evidence = await repository.FindPlainSubmissionAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(MailPlainSubmissionEvidenceState.Started, evidence!.EvidenceState);
        Assert.Equal(lockToken, evidence.ClaimToken);

        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.All(recipientStates.Values, state => Assert.Equal(MailRecipientDeliveryState.Pending, state));
    }

    [Fact]
    public async Task Existing_evidence_is_returned_without_creating_a_second_lifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var firstLockToken = Guid.NewGuid();
        var secondLockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, firstLockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        var first = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", firstLockToken, attemptNumber: 1, ct);
        Assert.Equal(PlainProviderInvocationOutcome.Started, first.Outcome);

        // Simulate a reclaim under a new lock token without the evidence ever resolving.
        await ClaimAsProcessingAsync(database.Factory, requestId, secondLockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        var second = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", secondLockToken, attemptNumber: 2, ct);

        Assert.Equal(PlainProviderInvocationOutcome.ExistingEvidence, second.Outcome);
        Assert.NotNull(second.ExistingEvidence);
        Assert.Equal(MailPlainSubmissionEvidenceState.Started, second.ExistingEvidence!.EvidenceState);
        Assert.Equal(firstLockToken, second.ExistingEvidence.ClaimToken);

        Assert.Equal(
            1L,
            await ReadScalarAsync(
                database.Factory,
                "SELECT COUNT(*) FROM mail_plain_submissions WHERE request_id = @Id;",
                ct,
                ("@Id", requestId.ToString("D"))));
    }

    [Fact]
    public async Task Fence_failed_when_lock_token_no_longer_matches_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var claimedToken = Guid.NewGuid();
        var staleToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, claimedToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", staleToken, attemptNumber: 1, ct);

        Assert.Equal(PlainProviderInvocationOutcome.FenceFailed, result.Outcome);
        Assert.Null(await repository.FindPlainSubmissionAsync(requestId, ct));
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.All(recipientStates.Values, state => Assert.Equal(MailRecipientDeliveryState.NotSent, state));
    }

    [Fact]
    public async Task Fence_failed_when_lease_has_already_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        var result = await repository.TryPreparePlainProviderInvocationAsync(
            requestId, tenantId, "mailpit", lockToken, attemptNumber: 1, ct);

        Assert.Equal(PlainProviderInvocationOutcome.FenceFailed, result.Outcome);
        Assert.Null(await repository.FindPlainSubmissionAsync(requestId, ct));
    }

    [Fact]
    public async Task Finalize_accepted_leaves_recipients_pending_and_moves_request_to_delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await repository.TryPreparePlainProviderInvocationAsync(requestId, tenantId, "mailpit", lockToken, 1, ct);

        var now = DateTimeOffset.UtcNow;
        var finalized = await repository.FinalizePlainSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            evidenceClaimToken: lockToken,
            expectedEvidenceState: MailPlainSubmissionEvidenceState.Started,
            now,
            MailPlainSubmissionEvidenceState.Accepted,
            providerMessageId: "provider-msg-1",
            requestTerminalState: MailRequestState.Delivered,
            recipientTargetState: null,
            lastErrorMessage: null,
            new MailAttemptInsert
            {
                RequestId = requestId,
                AttemptNumber = 1,
                Provider = "mailpit",
                Status = MailRequestState.Delivered,
                ProviderMessageId = "provider-msg-1",
                Retryable = false,
                LockToken = lockToken,
                StartedAt = now,
                CompletedAt = now,
            },
            ct);

        Assert.True(finalized);
        var evidence = await repository.FindPlainSubmissionAsync(requestId, ct);
        Assert.Equal(MailPlainSubmissionEvidenceState.Accepted, evidence!.EvidenceState);
        Assert.Equal("provider-msg-1", evidence.ProviderMessageId);

        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Delivered, status);

        // Provider acceptance alone must not mark the recipient Delivered (ADR 0023 D-05):
        // recipient-level feedback correlation is Issue D's scope.
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.Pending, recipientStates[(MailRecipientRole.To, 0)]);
    }

    [Fact]
    public async Task Finalize_definitely_rejected_moves_recipients_to_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await repository.TryPreparePlainProviderInvocationAsync(requestId, tenantId, "mailpit", lockToken, 1, ct);

        var now = DateTimeOffset.UtcNow;
        var finalized = await repository.FinalizePlainSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            evidenceClaimToken: lockToken,
            expectedEvidenceState: MailPlainSubmissionEvidenceState.Started,
            now,
            MailPlainSubmissionEvidenceState.DefinitelyRejected,
            providerMessageId: null,
            requestTerminalState: MailRequestState.Failed,
            recipientTargetState: MailRecipientDeliveryState.Failed,
            lastErrorMessage: "provider rejected the message",
            new MailAttemptInsert
            {
                RequestId = requestId,
                AttemptNumber = 1,
                Provider = "acs",
                Status = MailRequestState.Failed,
                Retryable = false,
                LockToken = lockToken,
                StartedAt = now,
                CompletedAt = now,
            },
            ct);

        Assert.True(finalized);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.Failed, recipientStates[(MailRecipientRole.To, 0)]);
    }

    [Fact]
    public async Task Finalize_unknown_moves_recipients_to_unknown_and_request_to_delivery_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await repository.TryPreparePlainProviderInvocationAsync(requestId, tenantId, "mailpit", lockToken, 1, ct);

        var now = DateTimeOffset.UtcNow;
        var finalized = await repository.FinalizePlainSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            evidenceClaimToken: lockToken,
            expectedEvidenceState: MailPlainSubmissionEvidenceState.Started,
            now,
            MailPlainSubmissionEvidenceState.Unknown,
            providerMessageId: null,
            requestTerminalState: MailRequestState.DeliveryUnknown,
            recipientTargetState: MailRecipientDeliveryState.Unknown,
            lastErrorMessage: "provider acceptance could not be confirmed",
            new MailAttemptInsert
            {
                RequestId = requestId,
                AttemptNumber = 1,
                Provider = "mailpit",
                Status = MailRequestState.DeliveryUnknown,
                ErrorCode = MailDeliveryErrorCodes.DeliveryUnknown,
                Retryable = false,
                LockToken = lockToken,
                StartedAt = now,
                CompletedAt = now,
            },
            ct);

        Assert.True(finalized);
        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, status);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.Unknown, recipientStates[(MailRecipientRole.To, 0)]);
    }

    [Fact]
    public async Task Manual_retry_is_rejected_once_plain_evidence_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var auditRepository = new AdminAuditRepository(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        await repository.TryPreparePlainProviderInvocationAsync(requestId, tenantId, "mailpit", lockToken, 1, ct);

        var now = DateTimeOffset.UtcNow;
        await repository.FinalizePlainSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            evidenceClaimToken: lockToken,
            expectedEvidenceState: MailPlainSubmissionEvidenceState.Started,
            now,
            MailPlainSubmissionEvidenceState.DefinitelyRejected,
            providerMessageId: null,
            requestTerminalState: MailRequestState.Failed,
            recipientTargetState: MailRecipientDeliveryState.Failed,
            lastErrorMessage: "provider rejected the message",
            new MailAttemptInsert
            {
                RequestId = requestId,
                AttemptNumber = 1,
                Provider = "acs",
                Status = MailRequestState.Failed,
                Retryable = false,
                LockToken = lockToken,
                StartedAt = now,
                CompletedAt = now,
            },
            ct);

        var retryResult = await repository.TryManualRetryAsync(
            requestId,
            allowedTenantIds: null,
            now,
            auditRepository,
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.ManualRetryRequested,
                Actor = "test-admin",
                OccurredAt = now,
                TargetType = AdminAuditLog.TargetTypes.MailRequest,
                TargetId = requestId.ToString("D"),
                Result = AdminAuditLog.Results.Success,
            },
            ct);

        Assert.Equal(ManualMailRequestMutationStatus.InvalidState, retryResult.Status);
        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Failed, status);
    }

    [Fact]
    public async Task Manual_cancel_is_rejected_once_plain_evidence_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var auditRepository = new AdminAuditRepository(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        // Claim with a valid lease so Started evidence actually commits, then let the lease
        // expire afterward -- simulating a crash mid-flight, which is exactly the scenario the
        // cancel guard must block (a stale Processing row with unresolved evidence).
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);
        var invocation = await repository.TryPreparePlainProviderInvocationAsync(requestId, tenantId, "mailpit", lockToken, 1, ct);
        Assert.Equal(PlainProviderInvocationOutcome.Started, invocation.Outcome);
        await ExpireLeaseAsync(database.Factory, requestId, ct);

        var now = DateTimeOffset.UtcNow;
        var cancelResult = await repository.TryManualCancelAsync(
            requestId,
            allowedTenantIds: null,
            now,
            auditRepository,
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.ManualCancelRequested,
                Actor = "test-admin",
                OccurredAt = now,
                TargetType = AdminAuditLog.TargetTypes.MailRequest,
                TargetId = requestId.ToString("D"),
                Result = AdminAuditLog.Results.Success,
            },
            ct);

        Assert.NotEqual(ManualMailRequestMutationStatus.Succeeded, cancelResult.Status);
    }

    private static CanonicalMailRecipient Recipient(
        MailRecipientRole role,
        int ordinal,
        string address,
        string? displayName) =>
        new()
        {
            Role = role,
            Ordinal = ordinal,
            Address = address,
            AddressKey = RecipientEmailNormalizer.Normalize(address),
            DisplayName = displayName,
        };

    private static AcceptedMailRequestInsert CreateInsert(
        Guid requestId,
        Guid tenantId,
        IReadOnlyList<CanonicalMailRecipient> recipients) =>
        new()
        {
            Id = requestId,
            TenantId = tenantId,
            SourceService = "plain-submission-test",
            MailRequestId = Guid.NewGuid(),
            Purpose = "test",
            PayloadJson = "{}",
            PayloadHash = new string('a', 64),
            Subject = "subject",
            RecipientEmail = recipients[0].Address,
            RecipientDisplayName = recipients[0].DisplayName,
            MaxAttempts = 3,
            AcceptedAt = DateTimeOffset.UtcNow,
            Recipients = recipients,
        };

    private static async Task SeedSuppressionAsync(
        SqliteConnectionFactory factory,
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        var suppressions = new MailSuppressionRepository(factory);
        Assert.True(await suppressions.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RecipientEmail = recipientEmail,
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken));
    }

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
            SET
                status = @ProcessingStatus,
                attempt_count = attempt_count + 1,
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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExpireLeaseAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET lock_expires_at = @LockExpiresAt
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow.AddMinutes(-1)));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<(MailRecipientRole Role, int Ordinal), MailRecipientDeliveryState>> ReadRecipientStatesAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<(MailRecipientRole, int), MailRecipientDeliveryState>();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_role, ordinal, delivery_state
            FROM mail_request_recipients
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states[((MailRecipientRole)reader.GetInt32(0), reader.GetInt32(1))] =
                (MailRecipientDeliveryState)reader.GetInt32(2);
        }

        return states;
    }

    private static async Task<(MailRequestState Status, string? LastErrorMessage)> ReadRequestStatusAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, last_error_message
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<PlainAttemptRow> ReadSingleAttemptAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, error_code, error_message, retryable
            FROM mail_attempts
            WHERE request_id = @RequestId
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var row = new PlainAttemptRow(
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3) == 1);
        Assert.False(await reader.ReadAsync(cancellationToken));
        return row;
    }

    private static async Task<long> ReadScalarAsync(
        SqliteConnectionFactory factory,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PlainAttemptRow(
        MailRequestState Status,
        string? ErrorCode,
        string? ErrorMessage,
        bool Retryable);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string root, SqliteConnectionFactory factory)
        {
            Root = root;
            Factory = factory;
        }

        public string Root { get; }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-plain-submission-store",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new TestDatabase(root, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
