using System.Net;
using Amane.Mailer.Admin;
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
        foreach (var file in Directory.GetFiles(dbOpsFixture.BackupDirectory, "mailer-*.db"))
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
    public async Task Concurrent_backup_requests_return_conflict_for_second_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "dbops-concurrent-" + Guid.NewGuid().ToString("N");
        await dbOpsFixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        using var client = CreateClient(dbOpsFixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var page = await client.GetAsync("/admin/ops", ct);
        var csrf = await ReadCsrfTokenFromHtmlAsync(await page.Content.ReadAsStringAsync(ct));
        var content = CreateCsrfContent(csrf);

        var first = client.PostAsync("/admin/ops/backup", content, ct);
        var second = client.PostAsync("/admin/ops/backup", content, ct);
        await Task.WhenAll(first, second);

        using var firstResponse = await first;
        using var secondResponse = await second;
        var statusCodes = new[] { firstResponse.StatusCode, secondResponse.StatusCode };

        Assert.Contains(HttpStatusCode.SeeOther, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        var failed = await ReadLatestAuditAsync(AdminAuditLog.EventTypes.DbBackupFailed, ct);
        Assert.NotNull(failed);
        Assert.Equal(AdminAuditLog.Results.Failure, failed.Value.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.LockHeld, await ReadLatestAuditErrorCodeAsync(
            AdminAuditLog.EventTypes.DbBackupFailed,
            ct));
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
}
