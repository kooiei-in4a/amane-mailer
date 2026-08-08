using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Direct coverage for <see cref="AttachmentSpoolReconciliationService"/>'s reconciliation passes
/// (ADR 0022 D-08 spool lifecycle). The real periodic loop only fires on a 5-minute wall-clock
/// <see cref="PeriodicTimer"/>, so these tests construct a standalone instance from the same
/// DI-registered dependencies as the hosted singleton and invoke its internal passes directly,
/// with a fixed <see cref="TimeProvider"/> to control grace-period staleness deterministically.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class AttachmentSpoolReconciliationServiceTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void CleanupAllStaging_removes_every_staging_directory_regardless_of_age()
    {
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var requestId = Guid.NewGuid();
        spool.EnsureStagingDirectory(requestId);
        var stagingDirectory = spool.GetStagingDirectory(requestId);
        Assert.True(Directory.Exists(stagingDirectory));

        var service = CreateService(DateTimeOffset.UtcNow);
        service.CleanupAllStaging();

        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public void CleanupStaleStaging_removes_only_directories_past_the_grace_period()
    {
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        spool.EnsureStagingDirectory(staleId);
        spool.EnsureStagingDirectory(freshId);

        var now = DateTimeOffset.UtcNow;
        Directory.SetLastWriteTimeUtc(spool.GetStagingDirectory(staleId), now.AddMinutes(-20).UtcDateTime);
        Directory.SetLastWriteTimeUtc(spool.GetStagingDirectory(freshId), now.UtcDateTime);

        var service = CreateService(now);
        service.CleanupStaleStaging();

        Assert.False(Directory.Exists(spool.GetStagingDirectory(staleId)));
        Assert.True(Directory.Exists(spool.GetStagingDirectory(freshId)));
    }

    [Fact]
    public async Task ReconcileCommittedSpoolAsync_removes_committed_spool_for_a_terminal_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var requestId = await SeedMailRequestAsync(MailRequestState.Delivered, ct);
        spool.EnsureStagingDirectory(requestId);
        spool.CommitStagingToCommitted(requestId);
        Assert.True(spool.CommittedDirectoryExists(requestId));

        var service = CreateService(DateTimeOffset.UtcNow);
        await service.ReconcileCommittedSpoolAsync(ct);

        Assert.False(spool.CommittedDirectoryExists(requestId));
    }

    [Fact]
    public async Task ReconcileCommittedSpoolAsync_retains_committed_spool_for_a_non_terminal_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var requestId = await SeedMailRequestAsync(MailRequestState.Processing, ct);
        spool.EnsureStagingDirectory(requestId);
        spool.CommitStagingToCommitted(requestId);

        var service = CreateService(DateTimeOffset.UtcNow);
        await service.ReconcileCommittedSpoolAsync(ct);

        Assert.True(spool.CommittedDirectoryExists(requestId));
    }

    [Fact]
    public async Task ReconcileCommittedSpoolAsync_retains_a_recent_committed_directory_with_no_db_row()
    {
        // Simulates the crash window between the spool rename and the SQLite commit (ADR 0022
        // D-08): the DB row does not exist yet, so it must never be touched before the grace
        // period elapses, or an in-flight accept's spool would be deleted out from under it.
        var ct = TestContext.Current.CancellationToken;
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var requestId = Guid.NewGuid();
        spool.EnsureStagingDirectory(requestId);
        spool.CommitStagingToCommitted(requestId);

        var service = CreateService(DateTimeOffset.UtcNow);
        await service.ReconcileCommittedSpoolAsync(ct);

        Assert.True(spool.CommittedDirectoryExists(requestId));
    }

    [Fact]
    public async Task ReconcileCommittedSpoolAsync_removes_a_stale_orphaned_committed_directory_with_no_db_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var spool = fixture.Factory.Services.GetRequiredService<AttachmentSpool>();
        var requestId = Guid.NewGuid();
        spool.EnsureStagingDirectory(requestId);
        spool.CommitStagingToCommitted(requestId);

        var now = DateTimeOffset.UtcNow;
        Directory.SetLastWriteTimeUtc(spool.GetCommittedDirectory(requestId), now.AddMinutes(-45).UtcDateTime);

        var service = CreateService(now);
        await service.ReconcileCommittedSpoolAsync(ct);

        Assert.False(spool.CommittedDirectoryExists(requestId));
    }

    private AttachmentSpoolReconciliationService CreateService(DateTimeOffset now) =>
        new(
            fixture.Factory.Services.GetRequiredService<AttachmentSpool>(),
            fixture.Factory.Services.GetRequiredService<SqliteConnectionFactory>(),
            new FixedUtcTimeProvider(now),
            fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>(),
            NullLogger<AttachmentSpoolReconciliationService>.Instance);

    private async Task<Guid> SeedMailRequestAsync(MailRequestState status, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var now = SqliteTime.UtcNow;

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, attachment_count,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'spool-reconciliation-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 1, 3, 1,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return requestId;
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
