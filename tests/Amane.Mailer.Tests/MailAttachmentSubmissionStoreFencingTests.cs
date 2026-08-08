using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// ADR 0022 D-08 provider invocation boundary: the Started marker must only be creatable by the
/// claim that currently, genuinely owns the request row. Independent review (PR #537) found that
/// TryInsertStartedAsync only checked "no evidence exists yet" and never checked the request
/// row's own status/lock_token, so a stale claim (expired lease, or a row cancelled out from
/// under it before any evidence existed) could still create a Started marker and go on to call
/// the provider for a request it no longer owns. A later review of PR #538 required the fencing
/// timestamp itself to be read only after BEGIN IMMEDIATE write ownership, so a claim whose
/// lease expires while the insert waits on the SQLite write lock cannot slip through with a
/// stale pre-wait now.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailAttachmentSubmissionStoreFencingTests(MailerAdminDbOpsFixture fixture)
    : IClassFixture<MailerAdminDbOpsFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Succeeds_when_the_claim_still_genuinely_owns_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        var requestId = await SeedAttachmentRequestAsync(MailRequestState.Processing, lockToken, now, ct);

        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        var started = await repository.TryInsertAttachmentSubmissionStartedAsync(
            requestId, "mailpit", lockToken, ct);

        Assert.True(started);
        var evidence = await repository.FindAttachmentSubmissionAsync(requestId, ct);
        Assert.NotNull(evidence);
        Assert.Equal(AttachmentSubmissionState.Started, evidence!.SubmissionState);
    }

    [Fact]
    public async Task Fails_closed_when_the_lease_has_already_expired()
    {
        // The lease timer expired but no other worker has reclaimed the row yet -- the stale
        // claim must not be able to create the Started marker during that narrow window even
        // though its lock_token still matches (post-merge review of #533/PR #537).
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        var requestId = await SeedAttachmentRequestAsync(
            MailRequestState.Processing, lockToken, now, ct, leaseExpiresAt: now.AddSeconds(-1));

        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        var started = await repository.TryInsertAttachmentSubmissionStartedAsync(
            requestId, "mailpit", lockToken, ct);

        Assert.False(started);
        var evidence = await repository.FindAttachmentSubmissionAsync(requestId, ct);
        Assert.Null(evidence);
    }

    [Fact]
    public async Task Fails_closed_when_the_row_was_cancelled_before_any_evidence_existed()
    {
        // Mirrors what MailRequestConsumerMutations.TryManualCancelAsync does to a stale
        // Processing row with no submission evidence yet: status -> Cancelled, lock_token -> NULL.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var staleLockToken = Guid.NewGuid();
        var requestId = await SeedAttachmentRequestAsync(MailRequestState.Cancelled, null, now, ct);

        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();

        // The stale worker doesn't know it lost the row -- it still presents its own lock token.
        var started = await repository.TryInsertAttachmentSubmissionStartedAsync(
            requestId, "mailpit", staleLockToken, ct);

        Assert.False(started);
        var evidence = await repository.FindAttachmentSubmissionAsync(requestId, ct);
        Assert.Null(evidence);
    }

    [Fact]
    public async Task Fails_closed_when_a_different_claim_now_owns_the_row()
    {
        // A different worker (or a later sweep cycle of the same worker) reclaimed the row with
        // a new lock token before the original claim got around to inserting Started.
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var currentLockToken = Guid.NewGuid();
        var staleLockToken = Guid.NewGuid();
        var requestId = await SeedAttachmentRequestAsync(MailRequestState.Processing, currentLockToken, now, ct);

        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        var started = await repository.TryInsertAttachmentSubmissionStartedAsync(
            requestId, "mailpit", staleLockToken, ct);

        Assert.False(started);
        var evidence = await repository.FindAttachmentSubmissionAsync(requestId, ct);
        Assert.Null(evidence);
    }

    [Fact]
    public async Task Fails_closed_when_the_lease_expires_while_waiting_on_the_write_lock()
    {
        // Reproduces the stale-now gap: caller (or a pre-lock GetUtcNow) would still see a valid
        // lease, but by the time BEGIN IMMEDIATE succeeds the lease has expired. The fencing
        // timestamp must be read after write ownership so Started is refused and no evidence
        // row is created (PR #538 Major review).
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ControllableTimeProvider(start);
        var connections = fixture.Factory.Services.GetRequiredService<SqliteConnectionFactory>();
        var store = new MailAttachmentSubmissionStore(connections, time);

        var lockToken = Guid.NewGuid();
        var requestId = await SeedAttachmentRequestAsync(
            MailRequestState.Processing,
            lockToken,
            start,
            ct,
            leaseExpiresAt: start.AddSeconds(30));

        // Hold the write lock via the same factory path production uses (WAL + busy_timeout).
        await using var lockConnection = await connections.OpenConnectionAsync(ct);
        await using (var begin = lockConnection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(ct);
        }

        // BeginTransaction waits synchronously on the write lock; run off the test thread so we
        // can advance virtual time while it is blocked.
        var insertTask = Task.Run(
            () => store.TryInsertStartedAsync(requestId, "mailpit", lockToken, CancellationToken.None),
            ct);

        await Task.Delay(100, ct);
        Assert.False(insertTask.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(60));

        await using (var rollback = lockConnection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(ct);
        }

        Assert.False(await insertTask);
        Assert.Null(await store.FindAsync(requestId, ct));
    }

    private async Task<Guid> SeedAttachmentRequestAsync(
        MailRequestState status,
        Guid? lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        DateTimeOffset? leaseExpiresAt = null)
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
                @Id, @TenantId, 'fencing-test', @MailRequestId, 'test',
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
        command.Parameters.AddWithValue("@LockToken", lockToken is null ? DBNull.Value : lockToken.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "@LockExpiresAt",
            lockToken is null ? DBNull.Value : SqliteTime.ToStorageUtc(leaseExpiresAt ?? now.AddMinutes(5)));
        command.Parameters.AddWithValue("@Now", nowStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return requestId;
    }
}
