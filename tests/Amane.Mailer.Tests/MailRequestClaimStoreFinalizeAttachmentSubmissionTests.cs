using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// ADR 0022 D-08 terminal finalize: the request terminal state, the submission evidence
/// terminal state, and the mail_attempts history row must commit as one atomic unit. Independent
/// review (post-merge review of #533/PR #537) found that the submission evidence update ran
/// unconditionally even when the request update failed, and had no lock-token fencing of its
/// own -- letting a stale claim (whose request-row lease was reclaimed by another worker) still
/// overwrite submission evidence out from under the new owner.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestClaimStoreFinalizeAttachmentSubmissionTests(MailerAdminDbOpsFixture fixture)
    : IClassFixture<MailerAdminDbOpsFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Same_tokens_updates_request_submission_and_attempt_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        var requestId = await SeedRequestAsync(MailRequestState.Processing, lockToken, now, ct);
        await SeedSubmissionAsync(requestId, AttachmentSubmissionState.Started, lockToken, now, ct);

        var claimStore = fixture.Factory.Services.GetRequiredService<MailRequestClaimStore>();
        var finalized = await claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            submissionLockToken: lockToken,
            now,
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: AttachmentSubmissionState.Succeeded,
            providerMessageId: "provider-message-1",
            requestTerminalState: MailRequestState.Delivered,
            lastErrorMessage: null,
            attempt: CreateAttempt(requestId, lockToken, now),
            ct);

        Assert.True(finalized);
        Assert.Equal(MailRequestState.Delivered, await ReadRequestStatusAsync(requestId, ct));

        var evidence = await FindEvidenceAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Succeeded, evidence!.SubmissionState);

        Assert.Equal(1, await CountAttemptsAsync(requestId, ct));
    }

    [Fact]
    public async Task Request_lock_token_mismatch_touches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var currentLockToken = Guid.NewGuid();
        var staleLockToken = Guid.NewGuid();
        var requestId = await SeedRequestAsync(MailRequestState.Processing, currentLockToken, now, ct);
        await SeedSubmissionAsync(requestId, AttachmentSubmissionState.Started, staleLockToken, now, ct);

        var claimStore = fixture.Factory.Services.GetRequiredService<MailRequestClaimStore>();
        var finalized = await claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: staleLockToken,
            submissionLockToken: staleLockToken,
            now,
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: AttachmentSubmissionState.Succeeded,
            providerMessageId: "provider-message-2",
            requestTerminalState: MailRequestState.Delivered,
            lastErrorMessage: null,
            attempt: CreateAttempt(requestId, staleLockToken, now),
            ct);

        Assert.False(finalized);
        Assert.Equal(MailRequestState.Processing, await ReadRequestStatusAsync(requestId, ct));

        var evidence = await FindEvidenceAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Started, evidence!.SubmissionState);

        Assert.Equal(0, await CountAttemptsAsync(requestId, ct));
    }

    [Fact]
    public async Task Submission_expected_state_mismatch_rolls_back_the_whole_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        var requestId = await SeedRequestAsync(MailRequestState.Processing, lockToken, now, ct);
        // Evidence is already Succeeded, but the caller (incorrectly) still believes it's Started.
        await SeedSubmissionAsync(requestId, AttachmentSubmissionState.Succeeded, lockToken, now, ct);

        var claimStore = fixture.Factory.Services.GetRequiredService<MailRequestClaimStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            submissionLockToken: lockToken,
            now,
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: AttachmentSubmissionState.Succeeded,
            providerMessageId: "provider-message-3",
            requestTerminalState: MailRequestState.Delivered,
            lastErrorMessage: null,
            attempt: CreateAttempt(requestId, lockToken, now),
            ct));

        // The request update (step 1) succeeded before the submission mismatch was discovered
        // (step 3) -- the whole transaction, including that request update, must have rolled
        // back. No partial state.
        Assert.Equal(MailRequestState.Processing, await ReadRequestStatusAsync(requestId, ct));

        var evidence = await FindEvidenceAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Succeeded, evidence!.SubmissionState);

        Assert.Equal(0, await CountAttemptsAsync(requestId, ct));
    }

    [Theory]
    [InlineData(AttachmentSubmissionState.Succeeded, MailRequestState.Delivered)]
    [InlineData(AttachmentSubmissionState.DefinitiveFailed, MailRequestState.Failed)]
    [InlineData(AttachmentSubmissionState.Unknown, MailRequestState.DeliveryUnknown)]
    public async Task Recovery_reaffirms_already_terminal_evidence_into_the_matching_request_state(
        AttachmentSubmissionState evidenceState, MailRequestState expectedRequestState)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        // The request row is stuck non-terminal (e.g. a prior finalize attempt updated evidence
        // but lost the request-row race under the old code) while evidence already terminalized.
        var requestId = await SeedRequestAsync(MailRequestState.Processing, lockToken, now, ct);
        await SeedSubmissionAsync(requestId, evidenceState, lockToken, now, ct);

        var claimStore = fixture.Factory.Services.GetRequiredService<MailRequestClaimStore>();
        var finalized = await claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: lockToken,
            submissionLockToken: lockToken,
            now,
            expectedSubmissionState: evidenceState,
            targetSubmissionState: evidenceState,
            providerMessageId: null,
            requestTerminalState: expectedRequestState,
            lastErrorMessage: null,
            attempt: CreateAttempt(requestId, lockToken, now),
            ct);

        Assert.True(finalized);
        Assert.Equal(expectedRequestState, await ReadRequestStatusAsync(requestId, ct));

        var evidence = await FindEvidenceAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(evidenceState, evidence!.SubmissionState);

        Assert.Equal(1, await CountAttemptsAsync(requestId, ct));
    }

    [Fact]
    public async Task Stale_worker_finalize_after_reclaim_never_overwrites_the_new_owners_convergence()
    {
        // Replays the exact race from the post-merge review: Worker A creates the Started
        // marker and calls the provider; its lease expires before it hears back; Worker B
        // reclaims the row and converges DeliveryUnknown from the Started-only evidence; Worker
        // A then belatedly tries to finalize as Succeeded under its now-superseded lock token.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var workerATokenAtStartedInsert = Guid.NewGuid();
        var workerBToken = Guid.NewGuid();

        var requestId = await SeedRequestAsync(MailRequestState.Processing, workerATokenAtStartedInsert, now, ct);
        await SeedSubmissionAsync(
            requestId, AttachmentSubmissionState.Started, workerATokenAtStartedInsert, now, ct);

        var claimStore = fixture.Factory.Services.GetRequiredService<MailRequestClaimStore>();

        // Worker B reclaims: mail_requests.lock_token moves to Worker B's token (as
        // TryClaimOneAsync would do). The submission row's own lock_token is untouched.
        await UpdateRequestLockTokenAsync(requestId, workerBToken, now, ct);

        // Worker B converges the Started-only evidence to DeliveryUnknown, using its own
        // current claim token for the request fence and the evidence's own (Worker A's)
        // token/state for the submission fence -- exactly what ConvergeFromAttachmentEvidenceAsync does.
        var convergedByB = await claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: workerBToken,
            submissionLockToken: workerATokenAtStartedInsert,
            now,
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: AttachmentSubmissionState.Unknown,
            providerMessageId: null,
            requestTerminalState: MailRequestState.DeliveryUnknown,
            lastErrorMessage: "Provider acceptance could not be confirmed during recovery.",
            attempt: CreateAttempt(requestId, workerBToken, now),
            ct);

        Assert.True(convergedByB);
        Assert.Equal(MailRequestState.DeliveryUnknown, await ReadRequestStatusAsync(requestId, ct));
        var afterB = await FindEvidenceAsync(requestId, ct);
        Assert.Equal(AttachmentSubmissionState.Unknown, afterB!.SubmissionState);

        // Worker A finally hears back "Succeeded" from the provider and tries to finalize under
        // its own (now-superseded) claim token -- it must be a complete no-op.
        var staleFinalizeByA = await claimStore.FinalizeAttachmentSubmissionAsync(
            requestId,
            requestLockToken: workerATokenAtStartedInsert,
            submissionLockToken: workerATokenAtStartedInsert,
            now.AddSeconds(5),
            expectedSubmissionState: AttachmentSubmissionState.Started,
            targetSubmissionState: AttachmentSubmissionState.Succeeded,
            providerMessageId: "late-provider-message",
            requestTerminalState: MailRequestState.Delivered,
            lastErrorMessage: null,
            attempt: CreateAttempt(requestId, workerATokenAtStartedInsert, now.AddSeconds(5)),
            ct);

        Assert.False(staleFinalizeByA);

        // No cross-inconsistency: request stays DeliveryUnknown, evidence stays Unknown, only
        // Worker B's single attempt row exists.
        Assert.Equal(MailRequestState.DeliveryUnknown, await ReadRequestStatusAsync(requestId, ct));
        var finalEvidence = await FindEvidenceAsync(requestId, ct);
        Assert.Equal(AttachmentSubmissionState.Unknown, finalEvidence!.SubmissionState);
        Assert.Equal(1, await CountAttemptsAsync(requestId, ct));
    }

    private static MailAttemptInsert CreateAttempt(Guid requestId, Guid lockToken, DateTimeOffset now) =>
        new()
        {
            RequestId = requestId,
            AttemptNumber = 1,
            Provider = "mailpit",
            Status = MailRequestState.Delivered,
            ProviderMessageId = null,
            ErrorCode = null,
            ErrorMessage = null,
            Retryable = false,
            LockToken = lockToken,
            StartedAt = now,
            CompletedAt = now,
        };

    private async Task<Guid> SeedRequestAsync(
        MailRequestState status,
        Guid lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, attachment_count,
                lock_token, lock_expires_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'finalize-atomic-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 1, 3, 1,
                @LockToken, @LockExpiresAt,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(now.AddMinutes(5)));
        command.Parameters.AddWithValue("@Now", nowStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return requestId;
    }

    private async Task SeedSubmissionAsync(
        Guid requestId,
        AttachmentSubmissionState state,
        Guid lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_attachment_submissions (
                request_id, submission_state, provider, submission_started_at,
                lock_token, provider_message_id, completed_at, created_at, updated_at)
            VALUES (
                @RequestId, @SubmissionState, 'mailpit', @Now,
                @LockToken, NULL, NULL, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@SubmissionState", (int)state);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@Now", nowStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateRequestLockTokenAsync(
        Guid requestId,
        Guid newLockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET lock_token = @LockToken, lock_expires_at = @LockExpiresAt
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@LockToken", newLockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(now.AddMinutes(5)));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MailRequestState> ReadRequestStatusAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM mail_requests WHERE id = @Id;";
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        var result = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return (MailRequestState)result;
    }

    private async Task<int> CountAttemptsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_attempts WHERE request_id = @Id;";
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        var result = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return (int)result;
    }

    private async Task<AttachmentSubmissionRow?> FindEvidenceAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        return await repository.FindAttachmentSubmissionAsync(requestId, cancellationToken);
    }
}
