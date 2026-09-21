using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminUsersPageTests
{
    private const string OwnerUsername = "users-owner";
    private const string OwnerPassword = "users-owner-password";
    private const string ScopedUsername = "users-scoped";
    private const string ScopedPassword = "users-scoped-password";
    private const string BreakGlassUsername = "users-break-glass";
    private const string BreakGlassPassword = "users-break-glass-password";
    private const string TargetUsername = "users-target";
    private const string TargetPassword = "users-target-password";
    private const string GoogleSubject = "google-subject-admin-users-ui-001";
    private const string GoogleEmailCanary = "admin-users-ui-canary@example.invalid";

    [Fact]
    public async Task Instance_owner_can_manage_admin_users_while_non_owners_are_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);

        using var scopedClient = CreateClient(harness.Factory);
        await LoginAsync(scopedClient, ScopedUsername, ScopedPassword, ct);
        using (var denied = await scopedClient.GetAsync(AdminUsersPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var breakGlassClient = CreateClient(harness.Factory);
        await LoginAsync(breakGlassClient, BreakGlassUsername, BreakGlassPassword, ct);
        using (var denied = await breakGlassClient.GetAsync(AdminUsersPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);
        using var page = await ownerClient.GetAsync(AdminUsersPage.PagePath, ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Contains("管理者", html, StringComparison.Ordinal);
        Assert.Contains(OwnerUsername, html, StringComparison.Ordinal);
        Assert.Contains(ScopedUsername, html, StringComparison.Ordinal);
        Assert.Contains(BreakGlassUsername, html, StringComparison.Ordinal);
        Assert.Contains(TargetUsername, html, StringComparison.Ordinal);
        Assert.Contains("enabled", html, StringComparison.Ordinal);
        Assert.Contains("Instance Owner", html, StringComparison.Ordinal);
        Assert.Contains("break-glass admin", html, StringComparison.Ordinal);
        Assert.Contains("tenant-scoped admin", html, StringComparison.Ordinal);
        Assert.Contains(harness.ScopedTenantId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("Google linked", html, StringComparison.Ordinal);
        Assert.Contains("Google not linked", html, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.TargetPasswordHash, html, StringComparison.Ordinal);
        Assert.DoesNotContain(GoogleSubject, html, StringComparison.Ordinal);
        Assert.DoesNotContain(GoogleEmailCanary, html, StringComparison.Ordinal);
        Assert.DoesNotContain("password_hash", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owner_can_disable_and_reenable_non_owner_with_session_and_login_effects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);
        var users = harness.Factory.Services.GetRequiredService<AdminUserRepository>();
        var sessions = harness.Factory.Services.GetRequiredService<AdminSessionRepository>();
        var target = await RequireUserAsync(users, TargetUsername, ct);
        var epochBefore = target.CredentialEpoch;

        var now = DateTimeOffset.UtcNow;
        var sessionId = await sessions.CreateSessionAsync(
            new AdminSessionRow(
                Guid.NewGuid().ToString("N"),
                TargetUsername,
                now,
                now,
                now.AddHours(8),
                now.AddMinutes(30),
                null,
                null,
                target.CredentialEpoch),
            maxConcurrentSessions: 3,
            ct);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);

        using (var noCsrf = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/disable",
            Form(string.Empty, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        }

        var token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var noConfirm = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/disable",
            Form(token),
            ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        }

        token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var disable = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/disable",
            Form(token, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, disable.StatusCode);
            Assert.Equal(
                $"{AdminUsersPage.PagePath}?notice=disabled",
                disable.Headers.Location?.ToString());
        }

        target = await RequireUserAsync(users, TargetUsername, ct);
        Assert.True(target.Disabled);
        Assert.Equal(epochBefore + 1, target.CredentialEpoch);
        var revoked = await sessions.GetSessionAsync(sessionId, ct);
        Assert.NotNull(revoked?.RevokedAt);
        Assert.Equal(AdminSessionRevokeReasons.CredentialChanged, revoked.RevokeReason);

        using var targetClient = CreateClient(harness.Factory);
        using (var login = await targetClient.PostAsync(
            "/admin/api/login",
            Form(
                await ReadCsrfTokenAsync(targetClient, "/admin/login", ct),
                ("username", TargetUsername),
                ("password", TargetPassword)),
            ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        Assert.Null(await users.GetActiveUserByUsernameAsync(TargetUsername, ct));
        Assert.NotNull(await users.GetUserByIdAsync(target.Id, ct));
        Assert.True((await users.GetUserByIdAsync(target.Id, ct))!.Disabled);

        var epochDisabled = target.CredentialEpoch;
        token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var enable = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/enable",
            Form(token, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, enable.StatusCode);
            Assert.Equal(
                $"{AdminUsersPage.PagePath}?notice=enabled",
                enable.Headers.Location?.ToString());
        }

        target = await RequireUserAsync(users, TargetUsername, ct);
        Assert.False(target.Disabled);
        Assert.Equal(epochDisabled + 1, target.CredentialEpoch);
        revoked = await sessions.GetSessionAsync(sessionId, ct);
        Assert.NotNull(revoked?.RevokedAt);

        await LoginAsync(CreateClient(harness.Factory), TargetUsername, TargetPassword, ct);

        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(50, ct);
        Assert.Contains(
            audits,
            a => a.EventType == AdminAuditLog.EventTypes.AdminUserDisabled
                && a.TargetType == AdminAuditLog.TargetTypes.AdminUser
                && a.TargetId == TargetUsername);
        Assert.Contains(
            audits,
            a => a.EventType == AdminAuditLog.EventTypes.AdminUserEnabled
                && a.TargetType == AdminAuditLog.TargetTypes.AdminUser
                && a.TargetId == TargetUsername);
        foreach (var audit in audits.Where(a => a.TargetType == AdminAuditLog.TargetTypes.AdminUser))
        {
            Assert.DoesNotContain(harness.TargetPasswordHash, audit.TargetId ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(GoogleSubject, audit.TargetId ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(GoogleEmailCanary, audit.FieldName ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Instance_owner_disable_is_rejected_without_mutating_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);
        var users = harness.Factory.Services.GetRequiredService<AdminUserRepository>();
        var sessions = harness.Factory.Services.GetRequiredService<AdminSessionRepository>();
        var owner = await RequireUserAsync(users, OwnerUsername, ct);
        var epochBefore = owner.CredentialEpoch;
        var now = DateTimeOffset.UtcNow;
        var sessionId = await sessions.CreateSessionAsync(
            new AdminSessionRow(
                Guid.NewGuid().ToString("N"),
                OwnerUsername,
                now,
                now,
                now.AddHours(8),
                now.AddMinutes(30),
                null,
                null,
                owner.CredentialEpoch),
            maxConcurrentSessions: 3,
            ct);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var disable = await ownerClient.PostAsync(
            $"/admin/users/{owner.Id}/disable",
            Form(token, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, disable.StatusCode);
            Assert.Equal(
                $"{AdminUsersPage.PagePath}?notice=owner_protected",
                disable.Headers.Location?.ToString());
        }

        owner = await RequireUserAsync(users, OwnerUsername, ct);
        Assert.False(owner.Disabled);
        Assert.Equal(epochBefore, owner.CredentialEpoch);
        var session = await sessions.GetSessionAsync(sessionId, ct);
        Assert.Null(session?.RevokedAt);
    }

    [Fact]
    public async Task Google_unlink_matches_cli_semantics_and_keeps_password_login()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);
        var users = harness.Factory.Services.GetRequiredService<AdminUserRepository>();
        var identities = harness.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>();
        var sessions = harness.Factory.Services.GetRequiredService<AdminSessionRepository>();
        var target = await RequireUserAsync(users, TargetUsername, ct);
        var epochBefore = target.CredentialEpoch;

        Assert.NotNull(await identities.FindByAdminUserIdAsync(target.Id, ct));
        var now = DateTimeOffset.UtcNow;
        var sessionId = await sessions.CreateSessionAsync(
            new AdminSessionRow(
                Guid.NewGuid().ToString("N"),
                TargetUsername,
                now,
                now,
                now.AddHours(8),
                now.AddMinutes(30),
                null,
                null,
                target.CredentialEpoch),
            maxConcurrentSessions: 3,
            ct);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);

        var token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var noConfirm = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/google-unlink",
            Form(token),
            ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        }

        token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var unlink = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/google-unlink",
            Form(token, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, unlink.StatusCode);
            Assert.Equal(
                $"{AdminUsersPage.PagePath}?notice=google_unlinked",
                unlink.Headers.Location?.ToString());
        }

        Assert.Null(await identities.FindByAdminUserIdAsync(target.Id, ct));
        Assert.Null(await identities.FindByIssuerSubjectAsync(
            AdminGoogleAuthenticationConstants.Issuer,
            GoogleSubject,
            ct));
        var session = await sessions.GetSessionAsync(sessionId, ct);
        Assert.NotNull(session?.RevokedAt);
        Assert.Equal(AdminSessionRevokeReasons.GoogleIdentityUnlinked, session.RevokeReason);

        target = await RequireUserAsync(users, TargetUsername, ct);
        Assert.Equal(epochBefore, target.CredentialEpoch);
        Assert.False(target.Disabled);

        await LoginAsync(CreateClient(harness.Factory), TargetUsername, TargetPassword, ct);

        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(50, ct);
        Assert.Contains(
            audits,
            a => a.EventType == AdminAuditLog.EventTypes.GoogleIdentityUnlinked
                && a.TargetType == AdminAuditLog.TargetTypes.AdminUser
                && a.TargetId == TargetUsername);
        foreach (var audit in audits.Where(a => a.EventType == AdminAuditLog.EventTypes.GoogleIdentityUnlinked))
        {
            Assert.DoesNotContain(GoogleSubject, audit.TargetId ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(GoogleEmailCanary, audit.TargetId ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(GoogleSubject, audit.FieldName ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Scoped_admin_cannot_see_instance_wide_admin_user_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);
        var users = harness.Factory.Services.GetRequiredService<AdminUserRepository>();
        var auditRepository = harness.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var target = await RequireUserAsync(users, TargetUsername, ct);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(ownerClient, AdminUsersPage.PagePath, ct);
        using (var disable = await ownerClient.PostAsync(
            $"/admin/users/{target.Id}/disable",
            Form(token, ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, disable.StatusCode);
        }

        var ownerVisible = await auditRepository.ListForAdminAsync(
            new AdminAuditListQuery
            {
                IncludeManagedConfiguration = true,
                PageSize = 50,
            },
            ct);
        Assert.Contains(
            ownerVisible.Items,
            a => a.EventType == AdminAuditLog.EventTypes.AdminUserDisabled
                && a.TargetId == TargetUsername);

        var scopedHidden = await auditRepository.ListForAdminAsync(
            new AdminAuditListQuery
            {
                IncludeManagedConfiguration = false,
                PageSize = 50,
            },
            ct);
        Assert.DoesNotContain(
            scopedHidden.Items,
            a => a.EventType == AdminAuditLog.EventTypes.AdminUserDisabled
                && a.TargetId == TargetUsername);
    }

    [Fact]
    public async Task List_summaries_omit_password_hash_and_google_identity_values()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await UsersHarness.CreateAsync(ct);
        var users = harness.Factory.Services.GetRequiredService<AdminUserRepository>();
        var summaries = await users.ListUserSummariesAsync(ct);
        var target = Assert.Single(summaries, u => u.Username == TargetUsername);

        Assert.True(target.GoogleLinked);
        Assert.False(target.IsInstanceOwner);
        Assert.False(target.IsBreakGlass);
        Assert.Contains(harness.ScopedTenantId, target.TenantScopes);
        Assert.DoesNotContain(
            harness.TargetPasswordHash,
            target.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(GoogleSubject, target.ToString(), StringComparison.Ordinal);
    }

    private static async Task<AdminUserRow> RequireUserAsync(
        AdminUserRepository users,
        string username,
        CancellationToken cancellationToken)
    {
        var summaries = await users.ListUserSummariesAsync(cancellationToken);
        var summary = Assert.Single(summaries, u => u.Username == username);
        var user = await users.GetUserByIdAsync(summary.Id, cancellationToken);
        Assert.NotNull(user);
        return user;
    }

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
        var token = await ReadCsrfTokenAsync(client, "/admin/login", cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            Form(token, ("username", username), ("password", password)),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No CSRF token in {path}.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static FormUrlEncodedContent Form(
        string csrfToken,
        params (string Name, string Value)[] values)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        };
        foreach (var (name, value) in values)
            form[name] = value;
        return new FormUrlEncodedContent(form);
    }

    private sealed class UsersHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private UsersHarness(
            string root,
            Guid scopedTenantId,
            string targetPasswordHash,
            WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            ScopedTenantId = scopedTenantId;
            TargetPasswordHash = targetPasswordHash;
            _factory = factory;
        }

        public Guid ScopedTenantId { get; }

        public string TargetPasswordHash { get; }

        public WebApplicationFactory<global::Program> Factory => _factory;

        public static async Task<UsersHarness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-admin-users",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, cancellationToken);
            var acsSecretPath = Path.Combine(root, "secrets", "acs_connection_string");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(cancellationToken);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(
                acsSecretPath,
                "Endpoint=https://example.communication.azure.com/;AccessKey=abc123"));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(acsSecretPath, cancellationToken));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                OwnerUsername,
                AdminPasswordHasher.Hash(OwnerPassword),
                cancellationToken));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var firstSender = await senders.CreateAsync("first@example.com", "First", cancellationToken);
            Assert.True(await instance.FinalizeAsync(cancellationToken));

            await users.CreateOrUpdateScopedUserAsync(
                ScopedUsername,
                AdminPasswordHasher.Hash(ScopedPassword),
                [firstSender.SenderId],
                cancellationToken);
            await users.CreateBreakGlassUserAsync(
                BreakGlassUsername,
                AdminPasswordHasher.Hash(BreakGlassPassword),
                cancellationToken);
            var targetPasswordHash = AdminPasswordHasher.Hash(TargetPassword);
            var targetId = await users.CreateOrUpdateScopedUserAsync(
                TargetUsername,
                targetPasswordHash,
                [firstSender.SenderId],
                cancellationToken);

            var identities = new AdminGoogleIdentityRepository(connections);
            Assert.Equal(
                AdminGoogleLinkResult.Linked,
                await identities.TryLinkAsync(
                    targetId,
                    AdminGoogleAuthenticationConstants.Issuer,
                    GoogleSubject,
                    DateTimeOffset.UtcNow,
                    cancellationToken));

            var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                new Dictionary<string, string?>
                {
                    ["AMANE_ADMIN_ENABLED"] = "false",
                    ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                },
                useEarlyInstanceProbe: true);

            return new UsersHarness(root, firstSender.SenderId, targetPasswordHash, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
