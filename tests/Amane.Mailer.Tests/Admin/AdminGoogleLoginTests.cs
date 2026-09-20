using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminGoogleLoginTests(AdminGoogleLoginFixture fixture)
    : IClassFixture<AdminGoogleLoginFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await RestoreDefaultAdminAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Backchannel.Subject = AdminGoogleLoginFixture.DefaultSubject;
        fixture.Backchannel.Email = AdminGoogleLoginFixture.DefaultEmail;
        fixture.Backchannel.FailTokenExchange = false;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Login_page_shows_google_button_when_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var response = await client.GetAsync("/admin/login", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Googleでログイン", html, StringComparison.Ordinal);
        Assert.Contains("または", html, StringComparison.Ordinal);
        Assert.Contains("Sign in", html, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminGoogleLoginFixture.ClientSecret, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_login_still_works_when_google_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        await PasswordLoginAsync(client, ct);

        using var page = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(1, await CountActiveSessionsAsync(ct));
    }

    [Fact]
    public async Task Google_challenge_redirects_with_correlation_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var challenge = await ChallengeGoogleAsync(client, ct);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var location = Assert.IsType<Uri>(challenge.Headers.Location);
        Assert.Equal("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.True(query.TryGetValue("redirect_uri", out var redirectUri));
        Assert.Contains("/admin/signin-google", redirectUri.ToString(), StringComparison.Ordinal);
        Assert.Contains(AdminGoogleLoginFixture.ClientId, location.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminGoogleLoginFixture.ClientSecret, location.Query, StringComparison.Ordinal);
        Assert.Contains(
            challenge.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("Correlation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Registered_google_identity_creates_admin_session_and_strict_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        await LinkCurrentAdminAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var html = await complete.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Contains("Googleログインが完了しました。", html, StringComparison.Ordinal);
        Assert.Contains("管理画面へ進む", html, StringComparison.Ordinal);
        Assert.Contains(
            complete.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("amane-admin-auth", StringComparison.Ordinal)
                && cookie.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase)
                && !cookie.Contains("amane-admin-external", StringComparison.Ordinal));
        Assert.Equal(1, await CountActiveSessionsAsync(ct));

        using var home = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        var audit = await ReadAuditAsync(AdminAuditLog.EventTypes.GoogleLoginSucceeded, ct);
        Assert.Equal(MailerAdminFixture.Username, Assert.Single(audit).Actor);
        AssertNoSecretLeak(html, audit);
    }

    [Fact]
    public async Task Unknown_google_identity_requires_link_and_does_not_create_session()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var html = await complete.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Contains("Googleアカウントはまだ管理者に紐付いていません。", html, StringComparison.Ordinal);
        Assert.Contains("このGoogleアカウントを紐付ける", html, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminGoogleLoginFixture.DefaultSubject, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminGoogleLoginFixture.DefaultEmail, html, StringComparison.Ordinal);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
        Assert.False(HasAdminAuthCookie(complete));
    }

    [Fact]
    public async Task Wrong_password_does_not_link_and_is_throttled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var csrf = ReadCsrfToken(await complete.Content.ReadAsStringAsync(ct));

        for (var i = 0; i < 5; i++)
        {
            using var failed = await client.PostAsync(
                AdminGoogleAuthenticationConstants.LinkPath,
                CreateLinkContent(csrf, MailerAdminFixture.Username, "wrong-password"),
                ct);
            if (i < 4)
                Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            else
                Assert.Equal(HttpStatusCode.TooManyRequests, failed.StatusCode);
        }

        Assert.Null(await FindMappingAsync(AdminGoogleLoginFixture.DefaultSubject, ct));
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
        Assert.Contains(
            await ReadAuditAsync(AdminAuditLog.EventTypes.GoogleIdentityLinkFailed, ct),
            row => row.ErrorCode == AdminAuditLog.ErrorCodes.InvalidCredentials);
        Assert.NotEmpty(await ReadAuditAsync(AdminAuditLog.EventTypes.AccountTemporarilyLocked, ct));
    }

    [Fact]
    public async Task Correct_password_creates_explicit_mapping_and_admin_session()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var csrf = ReadCsrfToken(await complete.Content.ReadAsStringAsync(ct));
        using var linked = await client.PostAsync(
            AdminGoogleAuthenticationConstants.LinkPath,
            CreateLinkContent(csrf, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);
        var html = await linked.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Contains("Googleログインが完了しました。", html, StringComparison.Ordinal);
        Assert.NotNull(await FindMappingAsync(AdminGoogleLoginFixture.DefaultSubject, ct));
        Assert.Equal(1, await CountActiveSessionsAsync(ct));
        Assert.True(HasAdminAuthCookie(linked));
        Assert.Contains(
            await ReadAuditAsync(AdminAuditLog.EventTypes.GoogleIdentityLinked, ct),
            row => row.Actor == MailerAdminFixture.Username);
        AssertNoSecretLeak(html, await ReadAuditAsync(null, ct));
    }

    [Fact]
    public async Task Matching_email_does_not_auto_link_or_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = AdminGoogleLoginFixture.DefaultEmail;
        await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .CreateOrUpdateScopedUserAsync(
                username,
                AdminPasswordHasher.Hash("email-user-password-not-real"),
                [MailerWebApplicationFixtureBase.TenantId],
                ct);
        fixture.Backchannel.Email = username;

        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var html = await complete.Content.ReadAsStringAsync(ct);

        Assert.Contains("Googleアカウントはまだ管理者に紐付いていません。", html, StringComparison.Ordinal);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
        Assert.Null(await FindMappingAsync(AdminGoogleLoginFixture.DefaultSubject, ct));
    }

    [Fact]
    public async Task Mapped_disabled_admin_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await LinkCurrentAdminAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        await DisableUserAsync(MailerAdminFixture.Username, ct);

        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
        Assert.Contains(
            await ReadAuditAsync(AdminAuditLog.EventTypes.GoogleLoginFailed, ct),
            row => row.ErrorCode == AdminAuditLog.ErrorCodes.Disabled);
    }

    [Fact]
    public async Task Break_glass_cannot_be_google_linked_but_password_login_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "break-glass-google-" + Guid.NewGuid().ToString("N");
        const string password = "break-glass-password-not-real";
        var users = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        var userId = await users.CreateBreakGlassUserAsync(username, AdminPasswordHasher.Hash(password), ct);

        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var csrf = ReadCsrfToken(await complete.Content.ReadAsStringAsync(ct));
        using var linked = await client.PostAsync(
            AdminGoogleAuthenticationConstants.LinkPath,
            CreateLinkContent(csrf, username, password),
            ct);
        var html = await linked.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Contains("This administrator cannot be linked to Google.", html, StringComparison.Ordinal);
        Assert.Null(await FindMappingAsync(AdminGoogleLoginFixture.DefaultSubject, ct));
        Assert.Equal(0, await CountActiveSessionsAsync(ct));

        await PasswordLoginAsync(client, username, password, ct);
        using var page = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var mappingResult = await fixture.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>()
            .TryLinkAsync(
                userId,
                AdminGoogleAuthenticationConstants.Issuer,
                "google-subject-break-glass-existing",
                DateTimeOffset.UtcNow,
                ct);
        Assert.Equal(AdminGoogleLinkResult.Linked, mappingResult);
        fixture.Backchannel.Subject = "google-subject-break-glass-existing";
        using var secondClient = CreateClient();
        using var rejected = await CompleteGoogleAsync(secondClient, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task Tenant_scoped_admin_keeps_scope_after_google_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "google-scoped-" + Guid.NewGuid().ToString("N");
        const string password = "scoped-google-password-not-real";
        var users = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        var userId = await users.CreateOrUpdateScopedUserAsync(
            username,
            AdminPasswordHasher.Hash(password),
            [MailerWebApplicationFixtureBase.TenantId],
            ct);
        await fixture.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>()
            .TryLinkAsync(
                userId,
                AdminGoogleAuthenticationConstants.Issuer,
                AdminGoogleLoginFixture.DefaultSubject,
                DateTimeOffset.UtcNow,
                ct);

        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using var list = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var access = await users.GetTenantAccessAsync(username, ct);
        Assert.NotNull(access);
        Assert.False(access.IsBreakGlass);
        Assert.Contains(MailerWebApplicationFixtureBase.TenantId, access.TenantIds);
        Assert.DoesNotContain(
            Guid.Parse("00000000-0000-0000-0000-000000000202"),
            access.TenantIds);
    }

    [Fact]
    public async Task Logout_revokes_google_derived_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await LinkCurrentAdminAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(1, await CountActiveSessionsAsync(ct));

        using var home = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        var sessionId = await GetSingleActiveSessionAsync(ct);
        await fixture.Factory.Services.GetRequiredService<AdminSessionRepository>()
            .RevokeSessionAsync(
                sessionId,
                AdminSessionRevokeReasons.Logout,
                DateTimeOffset.UtcNow,
                ct);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));

        using var rejected = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
    }

    [Fact]
    public async Task Credential_epoch_invalidates_google_derived_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await LinkCurrentAdminAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        Assert.True(await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .ResetPasswordAsync(MailerAdminFixture.Username, AdminPasswordHasher.Hash("rotated-password-not-real"), ct));

        using var rejected = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Contains("/admin/login", rejected.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_validator_rejects_expired_google_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await LinkCurrentAdminAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var sessionId = await GetSingleActiveSessionAsync(ct);
        await ExpireSessionAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        using var rejected = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
    }

    [Fact]
    public async Task Invalid_state_fails_closed_without_session()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var response = await client.GetAsync(
            $"{AdminGoogleAuthenticationConstants.CallbackPath}?code=not-a-code&state=not-a-state",
            ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
        Assert.NotEmpty(await ReadAuditAsync(AdminAuditLog.EventTypes.GoogleLoginFailed, ct));
        AssertNoSecretLeak(string.Empty, await ReadAuditAsync(null, ct));
    }

    [Fact]
    public async Task Audit_does_not_record_google_secrets_tokens_email_or_subject()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var csrf = ReadCsrfToken(await complete.Content.ReadAsStringAsync(ct));
        using var linked = await client.PostAsync(
            AdminGoogleAuthenticationConstants.LinkPath,
            CreateLinkContent(csrf, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);

        AssertNoSecretLeak(
            await linked.Content.ReadAsStringAsync(ct),
            await ReadAuditAsync(null, ct));
    }

    [Fact]
    public async Task Conflicting_link_is_fail_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = "already-linked-" + Guid.NewGuid().ToString("N");
        var users = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        var otherId = await users.CreateOrUpdateScopedUserAsync(
            other,
            AdminPasswordHasher.Hash("other-password-not-real"),
            [MailerWebApplicationFixtureBase.TenantId],
            ct);

        using var client = CreateClient();
        using var complete = await CompleteGoogleAsync(client, ct);
        var completeHtml = await complete.Content.ReadAsStringAsync(ct);
        Assert.Contains("このGoogleアカウントを紐付ける", completeHtml, StringComparison.Ordinal);

        var mapped = await fixture.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>()
            .TryLinkAsync(
                otherId,
                AdminGoogleAuthenticationConstants.Issuer,
                AdminGoogleLoginFixture.DefaultSubject,
                DateTimeOffset.UtcNow,
                ct);
        Assert.Equal(AdminGoogleLinkResult.Linked, mapped);

        var csrf = ReadCsrfToken(completeHtml);
        using var linked = await client.PostAsync(
            AdminGoogleAuthenticationConstants.LinkPath,
            CreateLinkContent(csrf, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);

        Assert.Contains("This Google account cannot be linked.", await linked.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var mapping = await FindMappingAsync(AdminGoogleLoginFixture.DefaultSubject, ct);
        Assert.NotNull(mapping);
        Assert.Equal(otherId, mapping.AdminUserId);
        Assert.Equal(0, await CountActiveSessionsAsync(ct));

        await LinkCurrentAdminAsync("google-subject-already-on-admin", ct);
        fixture.Backchannel.Subject = "google-subject-test-002";
        using var secondClient = CreateClient();
        using var secondComplete = await CompleteGoogleAsync(secondClient, ct);
        var secondCsrf = ReadCsrfToken(await secondComplete.Content.ReadAsStringAsync(ct));
        using var secondLinked = await secondClient.PostAsync(
            AdminGoogleAuthenticationConstants.LinkPath,
            CreateLinkContent(secondCsrf, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);
        Assert.Contains("This Google account cannot be linked.", await secondLinked.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Null(await FindMappingAsync("google-subject-test-002", ct));
        Assert.Equal(0, await CountActiveSessionsAsync(ct));
    }

    private HttpClient CreateClient() =>
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task PasswordLoginAsync(HttpClient client, CancellationToken cancellationToken) =>
        await PasswordLoginAsync(client, MailerAdminFixture.Username, MailerAdminFixture.Password, cancellationToken);

    private static async Task PasswordLoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfFromLoginAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["username"] = username,
                ["password"] = password,
            }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ChallengeGoogleAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfFromLoginAsync(client, cancellationToken);
        return await ChallengeGoogleWithCsrfAsync(client, csrf, cancellationToken);
    }

    private static Task<HttpResponseMessage> ChallengeGoogleWithCsrfAsync(
        HttpClient client,
        string csrf,
        CancellationToken cancellationToken) =>
        client.PostAsync(
            AdminGoogleAuthenticationConstants.ChallengePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
            }),
            cancellationToken);

    private static async Task<HttpResponseMessage> CompleteGoogleAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfFromLoginAsync(client, cancellationToken);
        return await CompleteGoogleWithCsrfAsync(client, csrf, cancellationToken);
    }

    private static async Task<HttpResponseMessage> CompleteGoogleWithCsrfAsync(
        HttpClient client,
        string csrf,
        CancellationToken cancellationToken)
    {
        using var challenge = await ChallengeGoogleWithCsrfAsync(client, csrf, cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var location = Assert.IsType<Uri>(challenge.Headers.Location);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.True(query.TryGetValue("state", out var state));
        using var callback = await client.GetAsync(
            $"{AdminGoogleAuthenticationConstants.CallbackPath}?code=amane-mailer-test-google-code-not-real&state={Uri.EscapeDataString(state.ToString())}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var completeUri = Assert.IsType<Uri>(callback.Headers.Location);
        var completePath = completeUri.IsAbsoluteUri
            ? completeUri.AbsolutePath
            : completeUri.OriginalString.Split('?', '#')[0];
        Assert.Equal(AdminGoogleAuthenticationConstants.CompletionPath, completePath);
        return await client.GetAsync(completeUri, cancellationToken);
    }

    private async Task LinkCurrentAdminAsync(string subject, CancellationToken cancellationToken)
    {
        var user = await fixture.Factory.Services.GetRequiredService<AdminUserRepository>()
            .GetActiveUserByUsernameAsync(MailerAdminFixture.Username, cancellationToken);
        Assert.NotNull(user);
        var result = await fixture.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>()
            .TryLinkAsync(
                user.Id,
                AdminGoogleAuthenticationConstants.Issuer,
                subject,
                DateTimeOffset.UtcNow,
                cancellationToken);
        Assert.Equal(AdminGoogleLinkResult.Linked, result);
    }

    private async Task RestoreDefaultAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_users
            SET disabled = 0,
                password_hash = @PasswordHash
            WHERE username = @Username;
            """;
        command.Parameters.AddWithValue("@Username", MailerAdminFixture.Username);
        command.Parameters.AddWithValue("@PasswordHash", MailerAdminFixture.PasswordHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountActiveSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admin_sessions WHERE revoked_at IS NULL;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> GetSingleActiveSessionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM admin_sessions WHERE revoked_at IS NULL LIMIT 2;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var sessionId = reader.GetString(0);
        Assert.False(await reader.ReadAsync(cancellationToken));
        return sessionId;
    }

    private async Task ExpireSessionAsync(
        string sessionId,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET absolute_expires_at = @AbsoluteExpiresAt,
                idle_expires_at = @IdleExpiresAt
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@AbsoluteExpiresAt", SqliteTime.ToStorageUtc(absoluteExpiresAt));
        command.Parameters.AddWithValue("@IdleExpiresAt", SqliteTime.ToStorageUtc(idleExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DisableUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admin_users SET disabled = 1 WHERE username = @Username;";
        command.Parameters.AddWithValue("@Username", username);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Amane.Mailer.Data.Sqlite.Models.AdminGoogleIdentityRow?> FindMappingAsync(
        string subject,
        CancellationToken cancellationToken) =>
        await fixture.Factory.Services.GetRequiredService<AdminGoogleIdentityRepository>()
            .FindByIssuerSubjectAsync(AdminGoogleAuthenticationConstants.Issuer, subject, cancellationToken);

    private async Task<List<AdminAuditRow>> ReadAuditAsync(string? eventType, CancellationToken cancellationToken)
    {
        var rows = new List<AdminAuditRow>();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, actor, target_id, result, error_code, user_agent_summary
            FROM admin_audit_events
            WHERE (@EventType IS NULL OR event_type = @EventType)
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("@EventType", (object?)eventType ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<string> ReadCsrfFromLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReadCsrfToken(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string ReadCsrfToken(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "HTML did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "CSRF token value was empty.");
        return html[start..end];
    }

    private static FormUrlEncodedContent CreateLinkContent(string csrfToken, string username, string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private static bool HasAdminAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie =>
            cookie.Contains("amane-admin-auth", StringComparison.Ordinal)
            && !cookie.Contains("amane-admin-external", StringComparison.Ordinal));

    private static void AssertNoSecretLeak(string html, IEnumerable<AdminAuditRow> auditRows)
    {
        var values = new List<string> { html };
        foreach (var row in auditRows)
        {
            values.Add(row.EventType);
            values.Add(row.Actor);
            if (row.TargetId is not null)
                values.Add(row.TargetId);
            values.Add(row.Result);
            if (row.ErrorCode is not null)
                values.Add(row.ErrorCode);
            if (row.UserAgentSummary is not null)
                values.Add(row.UserAgentSummary);
        }

        foreach (var value in values)
        {
            Assert.DoesNotContain(AdminGoogleLoginFixture.ClientSecret, value, StringComparison.Ordinal);
            Assert.DoesNotContain("amane-mailer-test-google-access-token-not-real", value, StringComparison.Ordinal);
            Assert.DoesNotContain("amane-mailer-test-google-code-not-real", value, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminGoogleLoginFixture.DefaultEmail, value, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminGoogleLoginFixture.DefaultSubject, value, StringComparison.Ordinal);
            Assert.DoesNotContain("google-subject-test-001", value, StringComparison.Ordinal);
        }
    }

    private sealed record AdminAuditRow(
        string EventType,
        string Actor,
        string? TargetId,
        string Result,
        string? ErrorCode,
        string? UserAgentSummary);
}

[Collection(MailerTestCollection.Name)]
public sealed class AdminGooglePasswordFallbackTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Unconfigured_google_keeps_password_login_and_hides_google_button()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        using var loginPage = await client.GetAsync("/admin/login", ct);
        var html = await loginPage.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);

        var csrf = ReadCsrf(html);
        using var login = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["username"] = MailerAdminFixture.Username,
                ["password"] = MailerAdminFixture.Password,
            }),
            ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var challenge = await client.PostAsync(
            AdminGoogleAuthenticationConstants.ChallengePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
            }),
            ct);
        Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);
    }

    [Theory]
    [InlineData("AMANE_ADMIN_GOOGLE_CLIENT_ID", "amane-mailer-test-google-client-id.apps.googleusercontent.com")]
    [InlineData("AMANE_ADMIN_GOOGLE_CLIENT_SECRET", "amane-mailer-test-google-client-secret-not-real")]
    public async Task Partial_google_config_keeps_password_login(string key, string value)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [key] = value,
                }));
        });
        using var client = CreateClient(factory);
        using var loginPage = await client.GetAsync("/admin/login", ct);
        var html = await loginPage.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);
        var csrf = ReadCsrf(html);
        using var login = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["username"] = MailerAdminFixture.Username,
                ["password"] = MailerAdminFixture.Password,
            }),
            ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static string ReadCsrf(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }
}
