using System.Net;
using System.Reflection;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminDbOpsTests(MailerAdminDbOpsFixture dbOpsFixture, MailerAdminFixture adminFixture)
    : IClassFixture<MailerAdminDbOpsFixture>, IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");

    public async ValueTask InitializeAsync()
    {
        await dbOpsFixture.ResetAsync(TestContext.Current.CancellationToken);
        dbOpsFixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        dbOpsFixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        dbOpsFixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
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

        if (Directory.Exists(dbOpsFixture.BackupStatusDirectory))
            Directory.Delete(dbOpsFixture.BackupStatusDirectory, recursive: true);

        SqliteConnection.ClearAllPools();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Db_ops_disabled_hides_operations_ui_and_returns_not_found_for_post()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(adminFixture.Factory);

        await LoginAsync(client, ct);
        using var page = await client.GetAsync("/admin/ops", ct);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("Database operations", html, StringComparison.Ordinal);
        Assert.Contains("unavailable (DB ops disabled)", html, StringComparison.Ordinal);

        using var checkpoint = await client.PostAsync("/admin/ops/checkpoint", new StringContent(string.Empty), ct);
        using var backup = await client.PostAsync("/admin/ops/backup", new StringContent(string.Empty), ct);

        Assert.Equal(HttpStatusCode.NotFound, checkpoint.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, backup.StatusCode);
    }

    [Fact]
    public async Task Scoped_admin_without_all_tenants_is_forbidden_and_ops_ui_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-scoped-" + Guid.NewGuid().ToString("N");
        await SeedMailRequestForTenantAsync(OtherTenantId, ct);
        await SeedMailRequestForTenantAsync(MailerWebApplicationFixtureBase.TenantId, ct);
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Service-wide DB operations require break-glass access", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Backup and restore status", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/admin/ops/backup", html, StringComparison.Ordinal);

        var internalId = await SeedDeadLetterForTenantAsync(MailerWebApplicationFixtureBase.TenantId, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(client, $"/admin/mail-requests/{internalId:D}", ct);
        using var backup = await client.PostAsync("/admin/ops/backup", CreateCsrfContent(csrf), ct);
        Assert.Equal(HttpStatusCode.Forbidden, backup.StatusCode);
    }

    [Fact]
    public async Task Break_glass_admin_backup_writes_file_and_records_audit_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-break-glass-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/admin/ops/backup", html, StringComparison.Ordinal);

        var csrf = await ReadCsrfTokenFromHtmlAsync(html);
        using var response = await client.PostAsync("/admin/ops/backup", CreateCsrfContent(csrf), ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/admin/ops", response.Headers.Location?.OriginalString);

        var backupFiles = Directory.GetFiles(dbOpsFixture.BackupDirectory, "mailer-*.db");
        Assert.Single(backupFiles);

        var requested = await ReadLatestAuditAsync(AdminAuditLog.EventTypes.DbBackupRequested, ct);
        var completed = await ReadLatestAuditAsync(AdminAuditLog.EventTypes.DbBackupCompleted, ct);
        Assert.NotNull(requested);
        Assert.Equal(AdminAuditLog.Results.Success, requested.Value.Result);
        Assert.NotNull(completed);
        Assert.Equal(AdminAuditLog.Results.Success, completed.Value.Result);
        Assert.Equal(AdminAuditLog.TargetTypes.DbOps, completed.Value.TargetType);
        Assert.StartsWith("mailer-", completed.Value.TargetId, StringComparison.Ordinal);
        Assert.DoesNotContain(dbOpsFixture.BackupDirectory, completed.Value.TargetId, StringComparison.Ordinal);
        Assert.Equal(AdminAuditLog.FieldNames.ConfiguredBackupDirectory, completed.Value.FieldName);

        using var refreshedPage = await client.GetAsync("/admin/ops", ct);
        var refreshedHtml = await refreshedPage.Content.ReadAsStringAsync(ct);
        Assert.Contains("Success (plaintext DB-only snapshot)", refreshedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_status_separates_database_full_instance_offsite_and_restore_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var asOf = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var lastSuccess = asOf.AddHours(-48);
        var artifactName = "mailer-20260919T120000Z.db.age";
        Directory.CreateDirectory(dbOpsFixture.BackupStatusDirectory);
        File.WriteAllText(
            Path.Combine(dbOpsFixture.BackupStatusDirectory, "db-only.attempt.json"),
            """
            {"schemaVersion":1,"backupType":"database-only","recordType":"attempt","status":"failed","startedAtUtc":"2026-09-21T11:55:00Z","completedAtUtc":"2026-09-21T11:55:02Z","stage":"upload","offsiteStatus":"failed","artifactName":null,"unexpectedSecret":"receipt-secret-must-not-render"}
            """);
        File.WriteAllText(
            Path.Combine(dbOpsFixture.BackupStatusDirectory, "db-only.success.json"),
            $$"""
            {"schemaVersion":1,"backupType":"database-only","recordType":"success","status":"succeeded","startedAtUtc":"{{lastSuccess:yyyy-MM-ddTHH:mm:ssZ}}","completedAtUtc":"{{lastSuccess:yyyy-MM-ddTHH:mm:ssZ}}","stage":"complete","offsiteStatus":"succeeded","artifactName":"{{artifactName}}"}
            """);
        File.WriteAllText(
            Path.Combine(dbOpsFixture.BackupStatusDirectory, "db-only.offsite-success.json"),
            $$"""
            {"schemaVersion":1,"backupType":"database-only","recordType":"offsite-success","status":"succeeded","startedAtUtc":"{{lastSuccess:yyyy-MM-ddTHH:mm:ssZ}}","completedAtUtc":"{{lastSuccess:yyyy-MM-ddTHH:mm:ssZ}}","stage":"upload","offsiteStatus":"succeeded","artifactName":"{{artifactName}}"}
            """);
        File.WriteAllText(
            Path.Combine(dbOpsFixture.BackupStatusDirectory, "full-instance.attempt.json"),
            """
            {"schemaVersion":1,"backupType":"full-instance","recordType":"attempt","status":"failed","startedAtUtc":"2026-09-21T11:50:00Z","completedAtUtc":"2026-09-21T11:50:01Z","stage":"preflight","offsiteStatus":"not-attempted","artifactName":null}
            """);
        File.WriteAllText(Path.Combine(dbOpsFixture.BackupDirectory, artifactName), "encrypted-fixture");
        SetSafeBackupStatusPermissions(dbOpsFixture.BackupStatusDirectory);

        var reader = new AdminBackupStatusReader(
            new MailerAdminBackupStatusOptions
            {
                StatusDirectory = dbOpsFixture.BackupStatusDirectory,
                DatabaseOnlyStaleAfter = TimeSpan.FromHours(24),
                FullInstanceStaleAfter = TimeSpan.FromHours(12),
            },
            dbOpsFixture.Factory.Services.GetRequiredService<AdminAuditRepository>(),
            TimeProvider.System);
        var model = await reader.LoadAsync(asOf, ct);

        Assert.Equal(AdminBackupEvidenceState.Available, model.DatabaseOnly.AttemptEvidenceState);
        Assert.Equal(AdminBackupOutcome.Failed, model.DatabaseOnly.LatestOutcome);
        Assert.Equal(lastSuccess, model.DatabaseOnly.LastSuccessAtUtc);
        Assert.Equal(true, model.DatabaseOnly.LastSuccessArtifactPresent);
        Assert.Equal(AdminBackupOffsiteState.Failed, model.DatabaseOnly.LatestAttemptOffsiteState);
        Assert.Equal(AdminBackupFreshness.Stale, model.DatabaseOnly.Freshness);
        Assert.Equal(AdminBackupOutcome.Failed, model.FullInstance.LatestOutcome);
        Assert.Equal(AdminBackupFreshness.Stale, model.FullInstance.Freshness);
        Assert.False(model.RestoreVerificationRecorded);

        var username = "backup-status-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);
        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);
        using var page = await client.GetAsync("/admin/ops", ct);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Backup and restore status", html, StringComparison.Ordinal);
        Assert.Contains("Encrypted DB-only script", html, StringComparison.Ordinal);
        Assert.Contains("Encrypted full-instance script", html, StringComparison.Ordinal);
        Assert.Contains("Upload failed", html, StringComparison.Ordinal);
        Assert.Contains("no durable verification evidence is recorded", html, StringComparison.Ordinal);
        Assert.DoesNotContain(artifactName, html, StringComparison.Ordinal);
        Assert.DoesNotContain(dbOpsFixture.BackupDirectory, html, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt-secret-must-not-render", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_success_receipt_has_unknown_freshness_and_rejects_artifact_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var asOf = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(dbOpsFixture.BackupStatusDirectory);
        File.WriteAllText(
            Path.Combine(dbOpsFixture.BackupStatusDirectory, "db-only.success.json"),
            """
            {"schemaVersion":1,"backupType":"database-only","recordType":"success","status":"succeeded","startedAtUtc":"2026-09-21T11:00:00Z","completedAtUtc":"2026-09-21T11:00:01Z","stage":"complete","offsiteStatus":"succeeded","artifactName":"../../secrets/backup.db.age"}
            """);
        SetSafeBackupStatusPermissions(dbOpsFixture.BackupStatusDirectory);

        var reader = new AdminBackupStatusReader(
            new MailerAdminBackupStatusOptions
            {
                StatusDirectory = dbOpsFixture.BackupStatusDirectory,
                DatabaseOnlyStaleAfter = TimeSpan.FromHours(24),
            },
            dbOpsFixture.Factory.Services.GetRequiredService<AdminAuditRepository>(),
            TimeProvider.System);
        var model = await reader.LoadAsync(asOf, ct);

        Assert.Equal(AdminBackupEvidenceState.Invalid, model.DatabaseOnly.SuccessEvidenceState);
        Assert.Equal(AdminBackupFreshness.Unknown, model.DatabaseOnly.Freshness);
        Assert.Null(model.DatabaseOnly.LastSuccessAtUtc);
        Assert.Null(model.DatabaseOnly.LastSuccessArtifactPresent);
    }

    [Fact]
    public async Task Group_or_other_writable_status_evidence_is_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var ct = TestContext.Current.CancellationToken;
        var asOf = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(dbOpsFixture.BackupStatusDirectory);
        var receiptPath = Path.Combine(dbOpsFixture.BackupStatusDirectory, "db-only.success.json");
        File.WriteAllText(
            receiptPath,
            """
            {"schemaVersion":1,"backupType":"database-only","recordType":"success","status":"succeeded","startedAtUtc":"2026-09-21T11:00:00Z","completedAtUtc":"2026-09-21T11:00:01Z","stage":"complete","offsiteStatus":"succeeded","artifactName":"mailer-20260921T110000Z.db.age"}
            """);
        SetSafeBackupStatusPermissions(dbOpsFixture.BackupStatusDirectory);

        var reader = new AdminBackupStatusReader(
            new MailerAdminBackupStatusOptions
            {
                StatusDirectory = dbOpsFixture.BackupStatusDirectory,
                DatabaseOnlyStaleAfter = TimeSpan.FromHours(24),
            },
            dbOpsFixture.Factory.Services.GetRequiredService<AdminAuditRepository>(),
            TimeProvider.System);

        File.SetUnixFileMode(
            receiptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.OtherRead);
        var writableReceipt = await reader.LoadAsync(asOf, ct);
        Assert.Equal(AdminBackupEvidenceState.Invalid, writableReceipt.DatabaseOnly.SuccessEvidenceState);
        Assert.Equal(AdminBackupFreshness.Unknown, writableReceipt.DatabaseOnly.Freshness);

        File.SetUnixFileMode(
            receiptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(
            dbOpsFixture.BackupStatusDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var writableDirectory = await reader.LoadAsync(asOf, ct);
        Assert.Equal(AdminBackupEvidenceState.Invalid, writableDirectory.DatabaseOnly.SuccessEvidenceState);
        Assert.Equal(AdminBackupFreshness.Unknown, writableDirectory.DatabaseOnly.Freshness);
    }

    [Fact]
    public async Task Break_glass_admin_checkpoint_records_audit_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-checkpoint-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var csrf = await ReadCsrfTokenFromHtmlAsync(await page.Content.ReadAsStringAsync(ct));

        using var response = await client.PostAsync("/admin/ops/checkpoint", CreateCsrfContent(csrf), ct);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);

        var audit = await ReadLatestAuditAsync(AdminAuditLog.EventTypes.DbCheckpointRequested, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
        Assert.Equal(AdminAuditLog.TargetTypes.DbOps, audit.Value.TargetType);
    }

    [Fact]
    public void Disabled_db_ops_does_not_derive_backup_directory_for_memory_database()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_DB_OPS_ENABLED"] = "false",
            })
            .Build();

        var options = MailerAdminDbOpsOptions.Load(configuration, "Data Source=:memory:");

        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.BackupDirectory);
    }

    [Fact]
    public void Invalid_db_ops_enabled_boolean_fails_load_when_admin_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_DB_OPS_ENABLED"] = "yes",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MailerAdminDbOpsOptions.Load(configuration, "Data Source=:memory:", adminEnabled: true));

        Assert.Contains("AMANE_ADMIN_DB_OPS_ENABLED", exception.Message, StringComparison.Ordinal);
        Assert.Contains("true", exception.Message, StringComparison.Ordinal);
        Assert.Contains("false", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_db_ops_enabled_boolean_is_ignored_when_admin_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_DB_OPS_ENABLED"] = "yes",
            })
            .Build();

        var options = MailerAdminDbOpsOptions.Load(configuration, "Data Source=:memory:", adminEnabled: false);

        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.BackupDirectory);
    }

    [Fact]
    public async Task Backup_without_csrf_returns_bad_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-csrf-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.PostAsync("/admin/ops/backup", new StringContent(string.Empty), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scoped_admin_with_all_effective_tenant_scopes_can_run_backup()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-all-scopes-" + Guid.NewGuid().ToString("N");
        await SeedMailRequestForTenantAsync(OtherTenantId, ct);
        await CreateScopedUserAsync(
            username,
            [MailerWebApplicationFixtureBase.TenantId, OtherTenantId],
            ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/admin/ops/backup", html, StringComparison.Ordinal);

        var csrf = await ReadCsrfTokenFromHtmlAsync(html);
        using var response = await client.PostAsync("/admin/ops/backup", CreateCsrfContent(csrf), ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task RunBackupAsync_returns_lock_held_when_operation_lock_is_already_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        var operationLock = GetOperationLock(service);

        Assert.True(await operationLock.WaitAsync(0, ct));
        try
        {
            var result = await service.RunBackupAsync(ct);
            Assert.Equal(AdminDbOpsStatus.LockHeld, result.Status);
        }
        finally
        {
            operationLock.Release();
        }
    }

    [Fact]
    public async Task Backup_post_returns_conflict_when_operation_lock_is_already_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-lock-held-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var csrf = await ReadCsrfTokenFromHtmlAsync(await page.Content.ReadAsStringAsync(ct));

        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        var operationLock = GetOperationLock(service);
        Assert.True(await operationLock.WaitAsync(0, ct));
        try
        {
            using var response = await client.PostAsync("/admin/ops/backup", CreateCsrfContent(csrf), ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var failed = await ReadLatestAuditAsync(AdminAuditLog.EventTypes.DbBackupFailed, ct);
            Assert.NotNull(failed);
            Assert.Equal(AdminAuditLog.Results.Failure, failed.Value.Result);
            Assert.Equal(AdminAuditLog.ErrorCodes.LockHeld, await ReadLatestAuditErrorCodeAsync(
                AdminAuditLog.EventTypes.DbBackupFailed,
                ct));
        }
        finally
        {
            operationLock.Release();
        }
    }

    [Fact]
    public void BuildBackupFileName_uses_millisecond_utc_timestamp()
    {
        var fileName = AdminDbOpsService.BuildBackupFileName(
            new DateTimeOffset(2026, 7, 4, 3, 12, 13, 456, TimeSpan.Zero));

        Assert.Equal("mailer-20260704T031213456Z.db", fileName);
    }

    [Fact]
    public void ResolveBackupDestinationPath_rejects_path_traversal()
    {
        var service = dbOpsFixture.Factory.Services.GetRequiredService<AdminDbOpsService>();
        Assert.Throws<InvalidOperationException>(() => service.ResolveBackupDestinationPath("../escape.db"));
    }

    private async Task<Guid> SeedDeadLetterForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var internalId = Guid.NewGuid();
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at, last_error_message)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'DbOpsTest',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 3, 3,
                @Now, @Now, @Now, @Now, 'failed');
            """;
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('d', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return internalId;
    }

    private async Task SeedMailRequestForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'DbOpsTest',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 0, 3,
                @Now, @Now, @Now);
            """;
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CreateScopedUserAsync(
        string username,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken) =>
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateOrUpdateScopedUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                tenantIds,
                cancellationToken);

    private async Task<string?> ReadLatestAuditErrorCodeAsync(
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT error_code
            FROM admin_audit_events
            WHERE event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventType", eventType);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string errorCode ? errorCode : null;
    }

    private async Task<(string Result, string? TargetType, string? TargetId, string? FieldName)?> ReadLatestAuditAsync(
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(dbOpsFixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT result, target_type, target_id, field_name
            FROM admin_audit_events
            WHERE event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventType", eventType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static string TenantAdminPassword(string username) => "password-for-" + username;

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken) =>
        await LoginAsync(client, MailerAdminFixture.Username, MailerAdminFixture.Password, cancellationToken);

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenFromLoginAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, username, password),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenFromAdminPageAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<string> ReadCsrfTokenFromLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<string> ReadCsrfTokenFromHtmlAsync(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Admin page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Admin page CSRF token value was empty.");
        return Task.FromResult(html[start..end]);
    }

    private static FormUrlEncodedContent CreateCsrfContent(string csrfToken) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        });

    private static FormUrlEncodedContent CreateLoginContent(string csrfToken, string username, string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private static void SetSafeBackupStatusPermissions(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var receiptPath in Directory.EnumerateFiles(directory))
        {
            File.SetUnixFileMode(
                receiptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static SemaphoreSlim GetOperationLock(AdminDbOpsService service)
    {
        var lockField = typeof(AdminDbOpsService).GetField("_operationLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        return (SemaphoreSlim)lockField.GetValue(service)!;
    }
}
