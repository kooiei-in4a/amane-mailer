using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// End-to-end Worker coverage for ADR 0022 attachment requests: submission evidence,
/// at-most-once provider invocation, and DeliveryUnknown convergence.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerAttachmentTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Worker_delivers_attachment_request_and_records_succeeded_submission_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var request = MailRequestTestData.CreateRequest(
            attachments: [MailRequestTestData.CreateTextAttachment()]);

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);

        var evidence = await FindSubmissionEvidenceAsync(stored.Id, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Succeeded, evidence.SubmissionState);

        // Provider is invoked exactly once even though this went through the normal claim path.
        Assert.Single(fixture.DeliveryProvider.Sent);
    }

    [Fact]
    public async Task Worker_converges_to_delivery_unknown_on_ambiguous_provider_result_and_never_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.SetSendDelay(TimeSpan.FromSeconds(5));

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var request = MailRequestTestData.CreateRequest(
            attachments: [MailRequestTestData.CreateTextAttachment()]);

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, ct);
        Assert.Equal(1, stored.AttemptCount);

        var evidence = await FindSubmissionEvidenceAsync(stored.Id, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Unknown, evidence.SubmissionState);

        // Give any (incorrect) retry path a chance to fire, then assert it never did. The
        // timed-out send is never recorded in Sent (CapturingMailDeliveryProvider only enqueues
        // after its simulated delay completes, which the Worker's own send timeout cancels
        // first) -- attempt_count staying at 1 with no further status churn is the real proof
        // that no resend happened.
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        var recheck = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(recheck);
        Assert.Equal(MailRequestState.DeliveryUnknown, recheck.Status);
        Assert.Equal(1, recheck.AttemptCount);
    }

    [Fact]
    public async Task Worker_recovers_started_only_evidence_to_delivery_unknown_without_calling_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var request = MailRequestTestData.CreateRequest(
            attachments: [MailRequestTestData.CreateTextAttachment()]);

        using var response = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var accepted = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, ct);

        // The legitimate initial delivery above already recorded one Sent entry; clear it so the
        // assertion below proves the *recovery* path alone never calls the provider again.
        fixture.DeliveryProvider.Reset();

        // Simulate: a prior claim created the Started marker then crashed before ever calling
        // the provider. Roll the row back to an expired Processing lease with only Started
        // evidence, matching what a real crash-after-Started-commit would leave behind.
        await SeedStartedOnlyCrashStateAsync(accepted.Id, ct);

        SignalWorker();
        var recovered = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeliveryUnknown, ct);

        var evidence = await FindSubmissionEvidenceAsync(accepted.Id, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Unknown, evidence.SubmissionState);

        // The provider must never be invoked again once Started evidence exists (ADR 0022 D-08).
        Assert.Empty(fixture.DeliveryProvider.Sent);
        Assert.Equal(MailRequestState.DeliveryUnknown, recovered.Status);
    }

    private void SignalWorker()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Amane.Mailer.Queue.IMailRequestQueue>().TrySignalWorkAvailable();
    }

    private async Task SeedStartedOnlyCrashStateAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var lockToken = Guid.NewGuid();
        var pastExpiry = SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow.AddMinutes(-10));

        // All three statements must land atomically: a concurrent Worker cycle (heartbeat- or
        // signal-driven) could otherwise observe an intermediate state (e.g. Processing with
        // still-Succeeded evidence) between separate implicit-transaction statements.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var updateRequest = connection.CreateCommand())
        {
            updateRequest.Transaction = (SqliteTransaction)transaction;
            updateRequest.CommandText = """
                UPDATE mail_requests
                SET status = 1,
                    lock_token = @LockToken,
                    lock_expires_at = @LockExpiresAt,
                    completed_at = NULL,
                    delivered_at = NULL,
                    delivery_unknown_at = NULL,
                    failed_at = NULL
                WHERE id = @Id;
                """;
            updateRequest.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            updateRequest.Parameters.AddWithValue("@LockExpiresAt", pastExpiry);
            updateRequest.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            await updateRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteAttempts = connection.CreateCommand())
        {
            deleteAttempts.Transaction = (SqliteTransaction)transaction;
            deleteAttempts.CommandText = "DELETE FROM mail_attempts WHERE request_id = @Id;";
            deleteAttempts.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            await deleteAttempts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsertSubmission = connection.CreateCommand())
        {
            upsertSubmission.Transaction = (SqliteTransaction)transaction;
            upsertSubmission.CommandText = """
                UPDATE mail_attachment_submissions
                SET submission_state = 0,
                    lock_token = @LockToken,
                    provider_message_id = NULL,
                    completed_at = NULL
                WHERE request_id = @Id;
                """;
            upsertSubmission.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            upsertSubmission.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            var affected = await upsertSubmission.ExecuteNonQueryAsync(cancellationToken);
            Assert.Equal(1, affected);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<AttachmentSubmissionRow?> FindSubmissionEvidenceAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.FindAttachmentSubmissionAsync(requestId, cancellationToken);
    }

    private async Task<MailRequestDispatchState?> FindDispatchStateAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);
    }

    private async Task<MailRequestDispatchState> WaitUntilStatusAsync(
        Guid mailRequestId,
        MailRequestState status,
        CancellationToken cancellationToken)
    {
        MailRequestDispatchState? lastStored = null;
        try
        {
            return await ConditionWait.UntilAsync<MailRequestDispatchState>(
                async ct =>
                {
                    lastStored = await FindDispatchStateAsync(mailRequestId, ct);
                    return lastStored;
                },
                stored => stored.Status == status,
                ConditionWait.DefaultTimeout,
                cancellationToken,
                wake: fixture.DeliveryProvider.Activity);
        }
        catch (TimeoutException)
        {
            var lastState = lastStored is null
                ? "not found"
                : $"{lastStored.Status} attempt_count={lastStored.AttemptCount}";
            throw new TimeoutException(
                $"Mail request did not reach status '{status}'. Last state: {lastState}.");
        }
    }
}
