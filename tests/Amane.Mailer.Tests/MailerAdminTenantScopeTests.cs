using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminTenantScopeMigrationTests
{
    [Fact]
    public async Task Db_migrate_creates_admin_user_scope_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-users", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.Contains("006_admin_users_and_tenant_scopes.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await TableExistsAsync(connection, "admin_users", ct));
            Assert.True(await TableExistsAsync(connection, "admin_user_tenant_scopes", ct));
            Assert.Contains("idx_admin_user_tenant_scopes_tenant", await GetIndexNamesAsync(connection, ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @Name LIMIT 1;";
        command.Parameters.AddWithValue("@Name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlySet<string>> GetIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            indexes.Add(reader.GetString(0));

        return indexes;
    }
}

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminTenantScopeTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Env_admin_is_seeded_with_configured_tenant_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/admin/login", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var access = await fixture.Factory.Services
            .GetRequiredService<AdminUserRepository>()
            .GetTenantAccessAsync(MailerAdminFixture.Username, ct);

        Assert.NotNull(access);
        Assert.False(access.IsBreakGlass);
        Assert.Contains(MailerWebApplicationFixtureBase.TenantId, access.TenantIds);
    }

    [Fact]
    public async Task Scoped_admin_cannot_view_or_filter_outside_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "tenant-admin-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        var visibleId = await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Queued,
            "visible@example.com",
            "Visible Subject",
            htmlBody: "visible-body",
            completedAt: null,
            ct);
        var hiddenId = await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            "hidden@example.com",
            "Hidden Subject",
            htmlBody: "hidden-body",
            completedAt: null,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var list = await client.GetAsync("/admin/mail-requests", ct);
        var listHtml = await list.Content.ReadAsStringAsync(ct);
        using var outsideFilter = await client.GetAsync($"/admin/mail-requests?tenant_id={OtherTenantId:D}", ct);
        using var hiddenDetail = await client.GetAsync($"/admin/mail-requests/{hiddenId:D}", ct);
        using var hiddenBody = await client.GetAsync($"/admin/mail-requests/{hiddenId:D}/body?field=html_body", ct);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(visibleId.ToString("D"), listHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenId.ToString("D"), listHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantId.ToString("D"), listHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, outsideFilter.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenBody.StatusCode);
    }

    [Fact]
    public async Task Scoped_dead_letters_and_badge_exclude_outside_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "deadletter-admin-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        var visibleMailRequestId = Guid.NewGuid();
        var hiddenMailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.DeadLettered,
            "visible-dead@example.com",
            "Visible Dead",
            htmlBody: null,
            completedAt: new DateTimeOffset(2026, 6, 29, 1, 0, 0, TimeSpan.Zero),
            ct,
            mailRequestId: visibleMailRequestId);
        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.DeadLettered,
            "hidden-dead@example.com",
            "Hidden Dead",
            htmlBody: null,
            completedAt: new DateTimeOffset(2026, 6, 29, 1, 1, 0, TimeSpan.Zero),
            ct,
            mailRequestId: hiddenMailRequestId);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync("/admin/dead-letters", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(visibleMailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenMailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"1 件\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_scope_change_revokes_existing_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "revoked-admin-" + Guid.NewGuid().ToString("N");
        var repository = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var beforeChange = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, beforeChange.StatusCode);

        await repository.ReplaceTenantScopesAsync(username, [OtherTenantId], ct);

        using var afterChange = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.Redirect, afterChange.StatusCode);
        Assert.Contains("/admin/login", afterChange.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_capability_requires_break_glass_or_all_effective_tenant_scopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        var oneTenant = "backup-one-" + Guid.NewGuid().ToString("N");
        var allTenants = "backup-all-" + Guid.NewGuid().ToString("N");
        var breakGlass = "break-glass-" + Guid.NewGuid().ToString("N");

        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            "history@example.com",
            "History Tenant",
            htmlBody: null,
            completedAt: null,
            ct);

        await CreateScopedUserAsync(oneTenant, [MailerWebApplicationFixtureBase.TenantId], ct);
        await repository.CreateOrUpdateScopedUserAsync(
            allTenants,
            AdminPasswordHasher.Hash(TenantAdminPassword(allTenants)),
            [MailerWebApplicationFixtureBase.TenantId, OtherTenantId],
            ct);
        await repository.CreateBreakGlassUserAsync(
            breakGlass,
            AdminPasswordHasher.Hash(TenantAdminPassword(breakGlass)),
            ct);

        var configuredTenants = new[] { MailerWebApplicationFixtureBase.TenantId };

        Assert.False(await repository.CanRunServiceWideBackupAsync(oneTenant, configuredTenants, ct));
        Assert.True(await repository.CanRunServiceWideBackupAsync(allTenants, configuredTenants, ct));
        Assert.True(await repository.CanRunServiceWideBackupAsync(breakGlass, configuredTenants, ct));
    }

    [Fact]
    public async Task Break_glass_login_and_body_view_are_strongly_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "break-glass-audit-" + Guid.NewGuid().ToString("N");
        await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateBreakGlassUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                ct);

        var id = await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            "body@example.com",
            "Body Subject",
            htmlBody: "break-glass-body",
            completedAt: null,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync($"/admin/mail-requests/{id:D}/body?field=html_body", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditRows = await fixture.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, ct);

        Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.BreakGlassLoginSucceeded);
        Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.BreakGlassMailRequestBodyViewed);
    }

    [Fact]
    public async Task Multi_tenant_admin_fails_closed_when_existing_users_have_no_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-scope-fail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, TwoTenantConfigJson(), ct);
        var connectionString = $"Data Source={databasePath}";

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration)).ApplyPendingAsync(ct);
            await InsertAdminUserWithoutScopeAsync(connectionString, ct);

            await using var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash(MailerAdminFixture.Password),
                new Dictionary<string, string?>
                {
                    ["MAIL_SERVICE_TOKEN_2"] = "second-test-token",
                });

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                using var client = CreateClient(factory);
                using var response = client.GetAsync("/admin/login", ct).GetAwaiter().GetResult();
            });

            Assert.Contains("multiple tenants", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task CreateScopedUserAsync(
        string username,
        IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateOrUpdateScopedUserAsync(
                username,
                AdminPasswordHasher.Hash(TenantAdminPassword(username)),
                tenantIds,
                cancellationToken);
    }

    private async Task<Guid> SeedMailRequestAsync(
        Guid tenantId,
        MailRequestState status,
        string recipientEmail,
        string subject,
        string? htmlBody,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken,
        Guid? mailRequestId = null)
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, recipient_email,
                status, attempt_count, max_attempts, last_error_message,
                accepted_at, created_at, updated_at, completed_at, failed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AdminTenantScopeTest',
                '{}', @PayloadHash, @Subject, @HtmlBody, @RecipientEmail,
                @Status, 1, 3, @LastErrorMessage,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt, @FailedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", (mailRequestId ?? Guid.NewGuid()).ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", subject);
        command.Parameters.AddWithValue("@HtmlBody", (object?)htmlBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@LastErrorMessage", status == MailRequestState.DeadLettered ? "provider failed" : DBNull.Value);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(completedAt ?? now));
        command.Parameters.AddWithValue("@CompletedAt", completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
        command.Parameters.AddWithValue("@FailedAt", status == MailRequestState.DeadLettered && completedAt is not null
            ? SqliteTime.ToStorageUtc(completedAt.Value)
            : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static async Task InsertAdminUserWithoutScopeAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_users (
                username, password_hash, disabled, credential_epoch,
                is_break_glass, created_at, updated_at)
            VALUES (
                'legacy-admin', @PasswordHash, 0, 0,
                0, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@PasswordHash", AdminPasswordHasher.Hash("legacy-password"));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 6, 29, 0, 0, 0, TimeSpan.Zero)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string TenantAdminPassword(string username) =>
        "password-for-" + username;

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, username, password),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Login page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Login page CSRF token value was empty.");
        return html[start..end];
    }

    private static FormUrlEncodedContent CreateLoginContent(
        string csrfToken,
        string username,
        string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private static string TwoTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "name": "example-develop",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            },
            {
              "tenant_id": "{{OtherTenantId}}",
              "name": "example-staging",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN_2",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            }
          ]
        }
        """;
}
