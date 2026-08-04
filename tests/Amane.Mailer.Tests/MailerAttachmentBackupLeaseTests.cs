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

        Assert.True(await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now, ct));

        // A different owner cannot acquire while the first lease is still valid.
        Assert.False(await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerB, TimeSpan.FromMinutes(10), now.AddSeconds(1), ct));

        await leaseStore.ReleaseAsync(MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, now.AddSeconds(2), ct);
        Assert.False(await leaseStore.IsHeldAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, now.AddSeconds(3), ct));

        // A fresh owner can now acquire.
        Assert.True(await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerB, TimeSpan.FromMinutes(10), now.AddSeconds(4), ct));

        // A stale (never-released) lease is reclaimable once its expiry has passed.
        Assert.False(await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now.AddSeconds(5), ct));
        Assert.True(await leaseStore.TryAcquireAsync(
            MailerMaintenanceLeaseStore.BackupLeaseName, ownerA, TimeSpan.FromMinutes(10), now.AddMinutes(11), ct));
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
        Assert.True(acquired);

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
