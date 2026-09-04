using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Identity;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Worker_delivers_queued_request_with_provider_stub()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 1, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
        Assert.Equal("recipient@example.com", sent.To);
        Assert.Equal("mailpit", sent.Provider);
        Assert.Equal(1, await CountAttemptsAsync(stored.Id, ct));
    }

    [Fact]
    public async Task Worker_does_not_claim_future_scheduled_request_until_due()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddHours(3));

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        SignalWorker();
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);

        var beforeDue = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(beforeDue);
        Assert.Equal(MailRequestState.Queued, beforeDue.Status);
        Assert.Equal(0, beforeDue.AttemptCount);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        await SetScheduledAtAsync(request.MailRequestId, DateTimeOffset.UtcNow.AddSeconds(-1), ct);
        SignalWorker();

        var delivered = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 1, ct);
        Assert.Equal(1, delivered.AttemptCount);
        Assert.Single(fixture.DeliveryProvider.Sent);
    }

    [Fact]
    public async Task Worker_recovers_stale_processing_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        await SeedExpiredProcessingRequestAsync(request, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 2, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
        Assert.Equal(2, stored.AttemptCount);
    }

    // ADR 0023 D-04/D-07 (Issue #546): plain requests were unified onto the same at-most-once
    // provider invocation model attachment requests already use. A retryable-classified provider
    // exception (SMTP_TEMPORARY is not one of the fixed codes AttachmentProviderResultClassifier
    // recognizes as definitive) can no longer disprove or prove provider acceptance, so it
    // converges straight to Unknown/DeliveryUnknown with retryable=false -- never back to Queued
    // with a backoff delay. These two tests replace the old
    // Worker_caps_retry_backoff_delay/Worker_dead_letters_after_max_retry_attempts, which
    // exercised the automatic retry-with-backoff loop this issue removed for plain requests.
    [Fact]
    public async Task Worker_converges_retryable_provider_failure_to_delivery_unknown_without_scheduling_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_TEMPORARY",
            "temporary failure",
            retryable: true));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 1, ct);

        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.NextAttemptAt);
        Assert.Equal(1, await CountAttemptsAsync(stored.Id, ct));

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, attempt.Status);
        Assert.Equal(MailDeliveryErrorCodes.DeliveryUnknown, attempt.ErrorCode);
        Assert.False(attempt.Retryable);
    }

    [Fact]
    public async Task Worker_does_not_automatically_reinvoke_provider_after_delivery_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_TEMPORARY",
            "temporary failure",
            retryable: true));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 1, ct);
        Assert.Equal(1, stored.AttemptCount);

        // A Failed/Queued/DeadLettered request would normally be re-signalable; DeliveryUnknown
        // must not be, since it is a terminal state the ADR 0023 D-07 automatic-retry ban covers.
        SignalWorker();
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        var afterSignal = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(afterSignal);
        Assert.Equal(MailRequestState.DeliveryUnknown, afterSignal.Status);
        Assert.Equal(1, afterSignal.AttemptCount);
        Assert.Single(fixture.DeliveryProvider.Sent);
    }

    [Fact]
    public async Task Worker_dead_letters_expired_processing_request_at_max_attempts_without_resending()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        await SeedExpiredProcessingRequestAsync(request, ct, attemptCount: 3);

        var deadLettered = await WaitUntilStatusAsync(
            request.MailRequestId,
            MailRequestState.DeadLettered,
            minAttemptCount: 3,
            ct);

        Assert.Equal(3, deadLettered.AttemptCount);
        Assert.NotNull(deadLettered.CompletedAt);
        Assert.Null(deadLettered.LockToken);
        Assert.Contains("max_attempts", deadLettered.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        var terminalColumns = await ReadTerminalColumnsAsync(deadLettered.Id, ct);
        Assert.NotNull(terminalColumns.CompletedAt);
        Assert.NotNull(terminalColumns.FailedAt);
        Assert.Null(terminalColumns.LockToken);
        Assert.Null(terminalColumns.LockExpiresAt);

        var attempt = await ReadSingleAttemptAsync(deadLettered.Id, ct);
        Assert.Equal(3, attempt.AttemptNumber);
        Assert.Equal("lease-reaper", attempt.Provider);
        Assert.Equal(MailRequestState.DeadLettered, attempt.Status);
        Assert.Equal("PROCESSING_LEASE_EXPIRED_MAX_ATTEMPTS", attempt.ErrorCode);
        Assert.True(attempt.Retryable);
    }

    // ADR 0023 D-04 / Issue #546 review finding F3 replaced the old #238 "ignore lease expiry
    // under the same lock token" finalize fallback with strict fencing (current claim token AND
    // unexpired lease) for plain requests. Combined with finding F1 (a plain request with durable
    // evidence is always reclaimable to converge from that evidence, even at max_attempts, since
    // the reclaim never re-invokes the provider), a send that completes after its lease has
    // expired can no longer finalize directly to Delivered: it loses the fencing race, and the
    // request instead recovers via reclaim + existing-evidence convergence to DeliveryUnknown.
    // This replaces the old Worker_marks_delivered_when_send_succeeds_after_lease_expiry_at_max_attempts.
    [Fact]
    public async Task Worker_converges_to_delivery_unknown_when_lease_expires_at_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.HoldNextSendIgnoringCancellation();
        var request = await SeedQueuedRequestAsync(attemptCount: 2, ct);

        var processing = await WaitUntilStatusAsync(
            request.MailRequestId,
            MailRequestState.Processing,
            minAttemptCount: 3,
            ct);

        await fixture.DeliveryProvider.WaitUntilHoldConsumedAsync(ct);
        await ExpireProcessingLeaseAsync(processing.Id, ct);

        // The row is at max_attempts, but its Started evidence makes it a recovery claim rather
        // than a generic max-attempt dead-letter candidate. Exercise both sides of F1 directly:
        // the generic reaper must leave it Processing, and the normal claim path must still be
        // able to reclaim it for no-provider-call convergence.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var reaper = scope.ServiceProvider.GetRequiredService<Amane.Mailer.Worker.ExpiredProcessingReaper>();
            await reaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(DateTimeOffset.UtcNow, ct);
        }

        var stillRecoverable = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(stillRecoverable);
        Assert.Equal(MailRequestState.Processing, stillRecoverable!.Status);
        Assert.Equal(0, await CountAttemptsAsync(processing.Id, ct));

        fixture.DeliveryProvider.ReleaseHeldSend();

        // The sweep service only polls every 30s (MailerWorkerFixture does not override
        // Mailer:Sweep:IntervalSeconds); signal the worker directly so the reclaim happens
        // within this test's wait window instead of depending on sweep timing.
        SignalWorker();

        var converged = await WaitUntilStatusAsync(
            request.MailRequestId,
            MailRequestState.DeliveryUnknown,
            minAttemptCount: 4,
            ct);
        Assert.Single(fixture.DeliveryProvider.Sent);

        var attempts = await ListAttemptsAsync(converged.Id, ct);
        Assert.Contains(
            attempts,
            attempt => attempt.Status == MailRequestState.DeliveryUnknown
                && attempt.ErrorCode == MailDeliveryErrorCodes.DeliveryUnknown);
        Assert.Single(attempts);

        SignalWorker();
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        var afterTerminalSignal = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(afterTerminalSignal);
        Assert.Equal(MailRequestState.DeliveryUnknown, afterTerminalSignal!.Status);
        Assert.Equal(1, await CountAttemptsAsync(converged.Id, ct));
    }

    [Fact]
    public async Task Worker_sanitizes_raw_provider_error_before_persisting_to_db()
    {
        // CapturingMailDeliveryProvider.QueueResult bypasses the real provider catch
        // blocks, so raw secrets in the queued MailDeliveryResult reach the worker
        // without provider-layer sanitization. This test verifies the worker's own
        // defense-in-depth layer strips secrets before writing to the DB.
        var ct = TestContext.Current.CancellationToken;
        const string rawError = "SMTP connect failed: password=hunter2 sender=admin@acme.example.com";
        // AcsRequestFailed classifies as DefinitiveFailed/Failed (AttachmentProviderResultClassifier,
        // shared with plain requests since Issue #546) so this exercises sanitization on a genuine
        // terminal Failed outcome rather than the Unknown/DeliveryUnknown bucket every other
        // non-fixed error code now falls into (ADR 0023 D-04).
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            MailDeliveryErrorCodes.AcsRequestFailed,
            rawError,
            retryable: false));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Failed, minAttemptCount: 1, ct);

        Assert.DoesNotContain("hunter2", stored.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("admin@acme.example.com", stored.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.DoesNotContain("hunter2", attempt.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("admin@acme.example.com", attempt.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_marks_request_failed_when_tenant_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = await SeedQueuedRequestAsync(
            attemptCount: 0,
            ct,
            tenantId: Guid.Parse("00000000-0000-0000-0000-00000000ffff"));

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Failed, minAttemptCount: 1, ct);

        Assert.Equal("Sender is not configured.", stored.LastErrorMessage);
    }

    [Fact]
    public async Task Worker_fails_suppressed_recipient_without_provider_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSuppressionAsync("recipient@example.com", ct);
        var metrics = fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>();
        metrics.ClearForTests();

        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Failed, minAttemptCount: 1, ct);
        Assert.Equal("Recipient is on the suppression list.", stored.LastErrorMessage);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.Equal(MailDeliveryErrorCodes.RecipientSuppressed, attempt.ErrorCode);
        Assert.Equal("none", attempt.Provider);
        Assert.False(attempt.Retryable);
        Assert.Equal(1, metrics.CaptureSnapshot().SuppressedSendsTotal);
    }

    [Fact]
    public async Task Worker_delivers_when_recipient_is_not_suppressed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSuppressionAsync("other@example.com", ct);
        var metrics = fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>();
        metrics.ClearForTests();

        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 1, ct);
        Assert.Equal(MailRequestState.Delivered, stored.Status);
        Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(0, metrics.CaptureSnapshot().SuppressedSendsTotal);
    }

    [Fact]
    public async Task Worker_suppresses_recipient_case_insensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSuppressionAsync("Recipient@Example.COM", ct);

        var request = await SeedQueuedRequestAsync(
            attemptCount: 0,
            ct,
            recipientEmail: "recipient@example.com");

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Failed, minAttemptCount: 1, ct);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.Equal(MailDeliveryErrorCodes.RecipientSuppressed, attempt.ErrorCode);
        Assert.DoesNotContain("recipient@", stored.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", stored.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ADR 0023 D-04 / Issue #546 review finding F3 replaced the old #238 "ignore lease expiry
    // under the same lock token" finalize fallback with strict fencing (current claim token AND
    // unexpired lease) for plain requests: a send that completes after its lease has expired can
    // no longer finalize directly to Delivered, even under the same, still-current lock token.
    // This replaces the old Worker_skips_finalize_when_lock_token_is_stale, which asserted the
    // pre-ADR-0023 best-effort-Delivered behavior.
    [Fact]
    public async Task Worker_converges_to_delivery_unknown_when_finalize_loses_the_expired_lease_race()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.HoldNextSend();
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var processing = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Processing, minAttemptCount: 1, ct);
        Assert.NotNull(processing.LockToken);

        // Expire the lease but keep the same lock_token, then release the held send: the
        // original worker iteration finishes provider delivery, but strict lease fencing
        // (F3) rejects its finalize. The request is left in Processing with its Started
        // evidence intact until a reclaim converges it.
        await ExpireProcessingLeaseAsync(processing.Id, ct);
        fixture.DeliveryProvider.ReleaseHeldSend();

        // The sweep service only polls every 30s (MailerWorkerFixture does not override
        // Mailer:Sweep:IntervalSeconds); signal the worker directly so the reclaim happens
        // within this test's wait window instead of depending on sweep timing.
        SignalWorker();

        var converged = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 2, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, converged.Status);
        Assert.Single(fixture.DeliveryProvider.Sent);

        var attempts = await ListAttemptsAsync(converged.Id, ct);
        Assert.Contains(
            attempts,
            attempt => attempt.Status == MailRequestState.DeliveryUnknown
                && attempt.ErrorCode == MailDeliveryErrorCodes.DeliveryUnknown);
    }

    // ADR 0023 D-04 (Issue #546) replaced the old #238 mechanism (scan mail_attempts for a
    // Delivered row not superseded by manual retry) with durable plain submission evidence:
    // recovery now converges from the mail_plain_submissions row itself, never from attempt
    // history alone. These two tests replace Worker_skips_resend_when_prior_delivery_attempt_exists
    // and Worker_converges_prior_success_before_suppression_check, which simulated a "prior
    // success" using only a raw mail_attempts row with no evidence -- a state the new Worker can
    // no longer produce (mail_attempts.status=Delivered is now always written atomically with
    // evidence_state=Accepted in the same FinalizePlainSubmissionAsync transaction).
    [Fact]
    public async Task Worker_recovers_started_only_plain_evidence_to_delivery_unknown_without_calling_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var request = MailRequestTestData.CreateRequest();
        var internalId = Guid.CreateVersion7(now);
        var expiredLockToken = Guid.CreateVersion7(now);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            await repository.InsertAcceptedAsync(
                new AcceptedMailRequestInsert
                {
                    Id = internalId,
                    TenantId = request.TenantId,
                    SourceService = request.SourceService,
                    MailRequestId = request.MailRequestId,
                    Purpose = request.Purpose,
                    PayloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    PayloadHash = request.PayloadHash,
                    Subject = request.Subject,
                    HtmlBody = request.HtmlBody,
                    TextBody = request.TextBody,
                    ReplyTo = request.ReplyTo,
                    RecipientEmail = request.To[0].Email,
                    RecipientDisplayName = request.To[0].DisplayName,
                    MaxAttempts = 3,
                    AcceptedAt = now,
                },
                ct);
        }

        // Simulate a crash after Started durably committed but before the provider was ever
        // called or finalized: request stuck in Processing with an expired lease, a Started
        // plain evidence row, and no mail_attempts row yet.
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE mail_requests
                    SET
                        status = @ProcessingStatus,
                        attempt_count = 1,
                        lock_token = @LockToken,
                        lock_expires_at = @LockExpiresAt,
                        updated_at = @UpdatedAt
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@LockToken", expiredLockToken.ToString("D"));
                update.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                update.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                update.Parameters.AddWithValue("@Id", internalId.ToString("D"));
                await update.ExecuteNonQueryAsync(ct);
            }

            await InsertStartedPlainEvidenceAsync(connection, internalId, expiredLockToken, now, ct);
        }

        SignalWorker();

        var converged = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 2, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, converged.Status);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        var attempts = await ListAttemptsAsync(converged.Id, ct);
        Assert.Contains(
            attempts,
            attempt => attempt.Status == MailRequestState.DeliveryUnknown
                && attempt.ErrorCode == MailDeliveryErrorCodes.DeliveryUnknown);
    }

    [Fact]
    public async Task Worker_does_not_recheck_suppression_when_recovering_from_existing_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var request = MailRequestTestData.CreateRequest();
        var internalId = Guid.CreateVersion7(now);
        var expiredLockToken = Guid.CreateVersion7(now);

        // Suppression added *after* the durable Started marker committed: recovery must converge
        // from the existing evidence (DeliveryUnknown, since it is Started-only) and must never
        // re-run the suppression precheck for a request that already has evidence.
        await SeedSuppressionAsync(request.To[0].Email, ct);
        var metrics = fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>();
        metrics.ClearForTests();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            await repository.InsertAcceptedAsync(
                new AcceptedMailRequestInsert
                {
                    Id = internalId,
                    TenantId = request.TenantId,
                    SourceService = request.SourceService,
                    MailRequestId = request.MailRequestId,
                    Purpose = request.Purpose,
                    PayloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    PayloadHash = request.PayloadHash,
                    Subject = request.Subject,
                    HtmlBody = request.HtmlBody,
                    TextBody = request.TextBody,
                    ReplyTo = request.ReplyTo,
                    RecipientEmail = request.To[0].Email,
                    RecipientDisplayName = request.To[0].DisplayName,
                    MaxAttempts = 3,
                    AcceptedAt = now,
                },
                ct);
        }

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE mail_requests
                    SET
                        status = @ProcessingStatus,
                        attempt_count = 1,
                        lock_token = @LockToken,
                        lock_expires_at = @LockExpiresAt,
                        updated_at = @UpdatedAt
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@LockToken", expiredLockToken.ToString("D"));
                update.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                update.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                update.Parameters.AddWithValue("@Id", internalId.ToString("D"));
                await update.ExecuteNonQueryAsync(ct);
            }

            await InsertStartedPlainEvidenceAsync(connection, internalId, expiredLockToken, now, ct);
        }

        SignalWorker();

        var converged = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 2, ct);
        Assert.Equal(MailRequestState.DeliveryUnknown, converged.Status);
        Assert.Empty(fixture.DeliveryProvider.Sent);
        Assert.Equal(0, metrics.CaptureSnapshot().SuppressedSendsTotal);

        var attempts = await ListAttemptsAsync(converged.Id, ct);
        Assert.DoesNotContain(
            attempts,
            attempt => attempt.ErrorCode == MailDeliveryErrorCodes.RecipientSuppressed);
        Assert.Contains(
            attempts,
            attempt => attempt.Status == MailRequestState.DeliveryUnknown
                && attempt.ErrorCode == MailDeliveryErrorCodes.DeliveryUnknown);
    }

    private static async Task InsertStartedPlainEvidenceAsync(
        SqliteConnection connection,
        Guid requestId,
        Guid claimToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insertEvidence = connection.CreateCommand();
        insertEvidence.CommandText = """
            INSERT INTO mail_plain_submissions (
                request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                provider_message_id, resolved_at, created_at, updated_at)
            VALUES (
                @RequestId, @Started, @Runtime, 'mailpit', @ClaimToken, @StartedAt,
                NULL, NULL, @Now, @Now);
            """;
        insertEvidence.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        insertEvidence.Parameters.AddWithValue("@Started", (int)MailPlainSubmissionEvidenceState.Started);
        insertEvidence.Parameters.AddWithValue("@Runtime", (int)MailPlainSubmissionEvidenceOrigin.Runtime);
        insertEvidence.Parameters.AddWithValue("@ClaimToken", claimToken.ToString("D"));
        insertEvidence.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-2)));
        insertEvidence.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now.AddMinutes(-2)));
        await insertEvidence.ExecuteNonQueryAsync(cancellationToken);
    }

    [Fact]
    public async Task Worker_does_not_skip_resend_after_manual_retry_when_prior_cycle_delivered_evidence_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var request = MailRequestTestData.CreateRequest();
        var internalId = Guid.CreateVersion7(now);
        var expiredLockToken = Guid.CreateVersion7(now);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            await repository.InsertAcceptedAsync(
                new AcceptedMailRequestInsert
                {
                    Id = internalId,
                    TenantId = request.TenantId,
                    SourceService = request.SourceService,
                    MailRequestId = request.MailRequestId,
                    Purpose = request.Purpose,
                    PayloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    PayloadHash = request.PayloadHash,
                    Subject = request.Subject,
                    HtmlBody = request.HtmlBody,
                    TextBody = request.TextBody,
                    ReplyTo = request.ReplyTo,
                    RecipientEmail = request.To[0].Email,
                    RecipientDisplayName = request.To[0].DisplayName,
                    MaxAttempts = 3,
                    AcceptedAt = now,
                },
                ct);
        }

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE mail_requests
                    SET
                        status = @DeadLetteredStatus,
                        attempt_count = 3,
                        lock_token = NULL,
                        lock_expires_at = NULL,
                        completed_at = @CompletedAt,
                        failed_at = @CompletedAt,
                        updated_at = @CompletedAt
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                update.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                update.Parameters.AddWithValue("@Id", internalId.ToString("D"));
                await update.ExecuteNonQueryAsync(ct);
            }

            await using (var insertAttempt = connection.CreateCommand())
            {
                insertAttempt.CommandText = """
                    INSERT INTO mail_attempts (
                        request_id, attempt_number, provider, status,
                        provider_message_id, error_code, error_message, retryable,
                        lock_token, started_at, completed_at)
                    VALUES (
                        @RequestId, 3, 'mailpit', @DeliveredStatus,
                        @ProviderMessageId, NULL, NULL, 0,
                        @LockToken, @StartedAt, @CompletedAt);
                    """;
                insertAttempt.Parameters.AddWithValue("@RequestId", internalId.ToString("D"));
                insertAttempt.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
                insertAttempt.Parameters.AddWithValue("@ProviderMessageId", "old-cycle-prior-success");
                insertAttempt.Parameters.AddWithValue("@LockToken", Guid.CreateVersion7(now).ToString("D"));
                insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-2)));
                insertAttempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                await insertAttempt.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var auditRepository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();
            var retry = await repository.TryManualRetryAsync(
                internalId,
                allowedTenantIds: null,
                now,
                auditRepository,
                new AdminAuditEvent
                {
                    EventType = AdminAuditLog.EventTypes.ManualRetryRequested,
                    Actor = "worker-test-admin",
                    OccurredAt = now,
                    TargetType = AdminAuditLog.TargetTypes.MailRequest,
                    TargetId = internalId.ToString("D"),
                    Result = AdminAuditLog.Results.Success,
                },
                ct);
            Assert.Equal(ManualMailRequestMutationStatus.Succeeded, retry.Status);

            var priorSuccess = await repository.FindSuccessfulDeliveryAttemptAsync(internalId, ct);
            Assert.Null(priorSuccess);
        }

        var superseded = await ListAttemptsAsync(internalId, ct);
        Assert.Contains(
            superseded,
            attempt => attempt.Status == MailRequestState.Delivered
                && attempt.ProviderMessageId == "old-cycle-prior-success"
                && attempt.ErrorCode == MailRequestRepository.SupersededByManualRetryErrorCode);

        // Simulate new-cycle claim #1 lease expiry so reclaim reaches AttemptCount > 1
        // with old-cycle Delivered evidence still present (#268).
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE mail_requests
                SET
                    status = @ProcessingStatus,
                    attempt_count = 1,
                    lock_token = @LockToken,
                    lock_expires_at = @LockExpiresAt,
                    updated_at = @UpdatedAt
                WHERE id = @Id;
                """;
            update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
            update.Parameters.AddWithValue("@LockToken", expiredLockToken.ToString("D"));
            update.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
            update.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
            update.Parameters.AddWithValue("@Id", internalId.ToString("D"));
            await update.ExecuteNonQueryAsync(ct);
        }

        SignalWorker();

        var delivered = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 2, ct);
        Assert.Equal(MailRequestState.Delivered, delivered.Status);
        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
    }

    // ADR 0023 D-04 (Issue #546): a send timeout can neither prove nor disprove provider
    // acceptance, so it converges straight to Unknown/DeliveryUnknown -- never back to Queued
    // with a scheduled retry, replacing this test's old Worker_schedules_retry_when_send_times_out
    // expectation.
    [Fact]
    public async Task Worker_converges_send_timeout_to_delivery_unknown_without_scheduling_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.SetSendDelay(TimeSpan.FromSeconds(3));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, minAttemptCount: 1, ct);

        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.NextAttemptAt);

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.Equal(MailDeliveryErrorCodes.DeliveryUnknown, attempt.ErrorCode);
        Assert.False(attempt.Retryable);
    }

    private async Task<MailRequestDispatchState> WaitUntilStatusAsync(
        Guid mailRequestId,
        MailRequestState status,
        int minAttemptCount,
        CancellationToken cancellationToken)
    {
        // Prefer provider activity pulses (send start/complete) over fixed 100ms spin.
        // Paths that never call the provider (lease reaper, missing tenant) still use a short fallback.
        MailRequestDispatchState? lastStored = null;
        try
        {
            return await ConditionWait.UntilAsync<MailRequestDispatchState>(
                async ct =>
                {
                    lastStored = await FindDispatchStateAsync(mailRequestId, ct);
                    return lastStored;
                },
                stored => stored.Status == status && stored.AttemptCount >= minAttemptCount,
                ConditionWait.DefaultTimeout,
                cancellationToken,
                wake: fixture.DeliveryProvider.Activity);
        }
        catch (TimeoutException)
        {
            var lastState = lastStored is null
                ? "not found"
                : $"{lastStored.Status} attempt_count={lastStored.AttemptCount} lock_token={(lastStored.LockToken is null ? "null" : "present")}";
            throw new TimeoutException(
                $"Mail request did not reach status '{status}' with attempt_count >= {minAttemptCount}. Last state: {lastState}.");
        }
    }

    private async Task<MailRequestDispatchState?> FindDispatchStateAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);
    }

    private async Task<int> CountAttemptsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.CountAttemptsAsync(requestId, cancellationToken);
    }

    private async Task<MailRequestTerminalColumns> ReadTerminalColumnsAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT completed_at, failed_at, lock_token, lock_expires_at
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));

        return new MailRequestTerminalColumns(
            reader.IsDBNull(0) ? null : SqliteTime.FromStorage(reader.GetString(0)),
            reader.IsDBNull(1) ? null : SqliteTime.FromStorage(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)));
    }

    private async Task<MailAttemptRecord> ReadSingleAttemptAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var attempts = await ListAttemptsAsync(requestId, cancellationToken);
        Assert.Single(attempts);
        return attempts[0];
    }

    private async Task<IReadOnlyList<MailAttemptRecord>> ListAttemptsAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_number, provider, status, error_code, error_message, retryable, provider_message_id
            FROM mail_attempts
            WHERE request_id = @RequestId
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var attempts = new List<MailAttemptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new MailAttemptRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                (MailRequestState)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return attempts;
    }

    private async Task SeedSuppressionAsync(string recipientEmail, CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var suppressions = scope.ServiceProvider.GetRequiredService<MailSuppressionRepository>();
        var now = DateTimeOffset.UtcNow;
        Assert.True(await suppressions.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = V2PersistenceCompatibility.SuppressionScopeId,
                RecipientEmail = recipientEmail,
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = now,
            },
            cancellationToken));
    }

    private async Task<MailRequestCreateRequest> SeedQueuedRequestAsync(
        int attemptCount,
        CancellationToken cancellationToken,
        Guid? tenantId = null,
        string? recipientEmail = null)
    {
        var request = MailRequestTestData.CreateRequest();
        if (tenantId is not null)
        {
            request = request with { TenantId = tenantId.Value };
        }

        if (recipientEmail is not null)
        {
            request = request with
            {
                To =
                [
                    new MailRecipientDto
                    {
                        Email = recipientEmail,
                        DisplayName = request.To[0].DisplayName,
                    },
                ],
            };
            request = request with
            {
                PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
            };
        }

        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = request.TenantId,
                SourceService = request.SourceService,
                MailRequestId = request.MailRequestId,
                Purpose = request.Purpose,
                PayloadJson = body,
                PayloadHash = request.PayloadHash,
                Subject = request.Subject,
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody,
                ReplyTo = request.ReplyTo,
                RecipientEmail = request.To[0].Email,
                RecipientDisplayName = request.To[0].DisplayName,
                MaxAttempts = 3,
                AcceptedAt = now,
            },
            cancellationToken);

        if (attemptCount > 0)
        {
            await using var connection = new SqliteConnection(fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mail_requests
                SET attempt_count = @AttemptCount
                WHERE mail_request_id = @MailRequestId;
                """;
            command.Parameters.AddWithValue("@AttemptCount", attemptCount);
            command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        SignalWorker();
        return request;
    }

    private async Task SeedExpiredProcessingRequestAsync(
        MailRequestCreateRequest request,
        CancellationToken cancellationToken,
        int attemptCount = 1)
    {
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = request.TenantId,
                SourceService = request.SourceService,
                MailRequestId = request.MailRequestId,
                Purpose = request.Purpose,
                PayloadJson = body,
                PayloadHash = request.PayloadHash,
                Subject = request.Subject,
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody,
                ReplyTo = request.ReplyTo,
                RecipientEmail = request.To[0].Email,
                RecipientDisplayName = request.To[0].DisplayName,
                MaxAttempts = 3,
                AcceptedAt = now,
            },
            cancellationToken);

        var lockToken = Guid.CreateVersion7(now);
        var expiredAt = now.AddMinutes(-1);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = 1,
                attempt_count = @AttemptCount,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @UpdatedAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        SignalWorker();
    }

    private async Task ExpireProcessingLeaseAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET lock_expires_at = @LockExpiresAt
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetScheduledAtAsync(
        Guid mailRequestId,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET scheduled_at = @ScheduledAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@ScheduledAt", SqliteTime.ToStorageUtc(scheduledAt));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void SignalWorker()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<Amane.Mailer.Queue.IMailRequestQueue>();
        queue.TrySignalWorkAvailable();
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", MailerWebApplicationFixtureBase.Token);
        return client;
    }

    private sealed record MailRequestTerminalColumns(
        DateTimeOffset? CompletedAt,
        DateTimeOffset? FailedAt,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt);

    private sealed record MailAttemptRecord(
        int AttemptNumber,
        string Provider,
        MailRequestState Status,
        string? ErrorCode,
        string? ErrorMessage,
        bool Retryable,
        string? ProviderMessageId = null);
}
