using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// ADR 0022 D-09 durable maintenance lease: backup preflight (active attachment row check) and
/// the attachment acceptance gate share one SQLite-backed lease.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailerAttachmentBackupLeaseTests(MailerAdminDbOpsFixture dbOpsFixture)
    : IClassFixture<MailerAdminDbOpsFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await dbOpsFixture.ResetAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(dbOpsFixture.BackupDirectory);
        foreach (var file in Directory.EnumerateFiles(dbOpsFixture.BackupDirectory, "mailer-*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(file);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RunBackupAsync_fails_when_a_non_terminal_attachment_request_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAttachmentRequestAsync(MailRequestState.Processing, ct);

        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        var result = await service.RunBackupAsync(ct);

        Assert.Equal(AdminDbOpsStatus.Failed, result.Status);
        Assert.Equal("ActiveAttachmentRequests", result.ErrorDetail);

        // The lease must not be left held after the aborted backup.
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        Assert.False(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            DateTimeOffset.UtcNow,
            ct));
    }

    [Fact]
    public async Task RunBackupAsync_succeeds_and_releases_the_lease_when_no_attachment_requests_are_active()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAttachmentRequestAsync(MailRequestState.Delivered, ct);

        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        var result = await service.RunBackupAsync(ct);

        Assert.Equal(AdminDbOpsStatus.Succeeded, result.Status);
        Assert.NotNull(result.BackupFileName);

        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        Assert.False(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            DateTimeOffset.UtcNow.AddMinutes(1),
            ct));
    }

    [Fact]
    public async Task Lease_store_reacquires_after_release_and_blocks_while_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        var now = DateTimeOffset.UtcNow;
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var acquiredA = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now, ct);
        Assert.True(acquiredA.Acquired);

        // A different owner cannot acquire while the first lease is still valid.
        Assert.False((await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerB, TimeSpan.FromMinutes(10), now.AddSeconds(1), ct)).Acquired);

        await leaseStore.ReleaseAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, acquiredA.FencingToken, now.AddSeconds(2), ct);
        Assert.False(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, now.AddSeconds(3), ct));

        // A fresh owner can now acquire.
        var acquiredB = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerB, TimeSpan.FromMinutes(10), now.AddSeconds(4), ct);
        Assert.True(acquiredB.Acquired);

        // A stale (never-released) lease is reclaimable once its expiry has passed.
        Assert.False((await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now.AddSeconds(5), ct)).Acquired);
        Assert.True((await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now.AddMinutes(11), ct)).Acquired);
    }

    [Fact]
    public async Task RenewAsync_succeeds_within_validity_and_fails_after_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid();

        var acquired = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, owner, TimeSpan.FromSeconds(30), now, ct);
        Assert.True(acquired.Acquired);

        // Renewing while still within the lease's validity extends it.
        Assert.True(await leaseStore.RenewAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            owner,
            acquired.FencingToken,
            TimeSpan.FromMinutes(10),
            now.AddSeconds(10),
            ct));
        Assert.True(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, now.AddMinutes(5), ct));

        // Once the lease has actually lapsed, the same owner/fencing token can never revive it
        // via RenewAsync -- only a fresh TryAcquireAsync (which bumps the fencing token) can.
        var lapsedAt = now.AddSeconds(10).AddMinutes(11);
        Assert.False(await leaseStore.RenewAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            owner,
            acquired.FencingToken,
            TimeSpan.FromMinutes(10),
            lapsedAt,
            ct));
        Assert.False(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, lapsedAt, ct));
    }

    [Fact]
    public async Task RenewAsync_fails_when_the_fencing_token_does_not_match()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid();

        var acquired = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, owner, TimeSpan.FromMinutes(10), now, ct);
        Assert.True(acquired.Acquired);

        // Same owner token, but a fencing token that does not match the row -- renewal must
        // fail even though the lease is otherwise still valid.
        Assert.False(await leaseStore.RenewAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            owner,
            acquired.FencingToken + 1,
            TimeSpan.FromMinutes(10),
            now.AddSeconds(1),
            ct));
    }

    [Fact]
    public async Task Reclaim_by_a_new_owner_after_expiry_blocks_the_old_owners_renew_and_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        var now = DateTimeOffset.UtcNow;
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var acquiredA = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromSeconds(5), now, ct);
        Assert.True(acquiredA.Acquired);

        var afterExpiry = now.AddSeconds(10);
        var acquiredB = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerB, TimeSpan.FromMinutes(10), afterExpiry, ct);
        Assert.True(acquiredB.Acquired);
        Assert.NotEqual(acquiredA.FencingToken, acquiredB.FencingToken);

        // Owner A's renew, using its own (now-stale) fencing token, must fail.
        Assert.False(await leaseStore.RenewAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            ownerA,
            acquiredA.FencingToken,
            TimeSpan.FromMinutes(10),
            afterExpiry.AddSeconds(1),
            ct));

        // Owner A's release must not affect owner B's lease.
        await leaseStore.ReleaseAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            ownerA,
            acquiredA.FencingToken,
            afterExpiry.AddSeconds(2),
            ct);
        Assert.True(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, afterExpiry.AddSeconds(3), ct));
    }

    [Fact]
    public async Task RunBackupAsync_never_publishes_when_the_lease_is_reclaimed_just_before_publish()
    {
        // The narrow-window equivalent of a heartbeat renewal that hasn't fired yet: something
        // else takes the lease (e.g. a stale owner's clock skew, or the lease genuinely
        // expiring) between the snapshot finishing and the atomic publish. The DB-side
        // ownership/fencing/expiry re-check right before publish must catch this even when the
        // heartbeat itself hasn't observed it yet.
        var ct = TestContext.Current.CancellationToken;
        await SeedAttachmentRequestAsync(MailRequestState.Delivered, ct);

        // Establish a known-good prior backup so we can prove it is never overwritten.
        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        var firstBackup = await service.RunBackupAsync(ct);
        Assert.Equal(AdminDbOpsStatus.Succeeded, firstBackup.Status);
        var backupPath = Path.Combine(dbOpsFixture.BackupDirectory, firstBackup.BackupFileName!);
        var originalBytes = await File.ReadAllBytesAsync(backupPath, ct);

        var connectionFactory = dbOpsFixture.Factory.Services.GetRequiredService<SqliteConnectionFactory>();
        connectionFactory.BeforeAtomicReplaceForTests = async _ =>
        {
            // Simulate the lease being reclaimed by a different owner in the narrow window
            // between the snapshot completing and the atomic publish -- bump owner_token and
            // fencing_token directly, without touching the in-flight backup's own row otherwise.
            await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mailer_maintenance_leases
                SET owner_token = @NewOwner, fencing_token = fencing_token + 1
                WHERE lease_name = @LeaseName;
                """;
            command.Parameters.AddWithValue("@NewOwner", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@LeaseName", MailerMaintenanceLeaseStore.BackupLeaseName);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        };

        try
        {
            var result = await service.RunBackupAsync(ct);
            Assert.Equal(AdminDbOpsStatus.Failed, result.Status);
            Assert.Equal("BackupMaintenanceLeaseLost", result.ErrorDetail);
        }
        finally
        {
            connectionFactory.BeforeAtomicReplaceForTests = null;
        }

        var afterBytes = await File.ReadAllBytesAsync(backupPath, ct);
        Assert.Equal(originalBytes, afterBytes);
    }

    [Fact]
    public async Task Attachment_acceptance_returns_503_while_backup_lease_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseStore = dbOpsFixture.Factory.Services.GetRequiredService<MailerMaintenanceLeaseStore>();
        var acquired = await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName,
            Guid.NewGuid(),
            TimeSpan.FromMinutes(5),
            DateTimeOffset.UtcNow,
            ct);
        Assert.True(acquired.Acquired);

        using var client = dbOpsFixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var request = MailRequestTestData.CreateRequest(
            attachments: [MailRequestTestData.CreateTextAttachment()]);

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("ATTACHMENT_STORAGE_UNAVAILABLE", body, StringComparison.Ordinal);

        // No orphaned row: the request was never committed.
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_requests WHERE mail_request_id = @MailRequestId;";
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        Assert.Equal(0, count);
    }

    private async Task SeedAttachmentRequestAsync(MailRequestState status, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, attachment_count,
                accepted_at, created_at, updated_at, completed_at)
            VALUES (
                @Id, @TenantId, 'backup-lease-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 1, 3, 1,
                @Now, @Now, @Now, @CompletedAt);
            """;
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        var isTerminal = status is MailRequestState.Delivered
            or MailRequestState.Failed
            or MailRequestState.DeadLettered
            or MailRequestState.Cancelled
            or MailRequestState.DeliveryUnknown;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('d', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@CompletedAt", isTerminal ? now : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
