using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminAuditLogPageTests(MailerAdminFixture fixture)
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
    public async Task Unauthenticated_audit_log_redirects_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/audit-log", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_audit_log_returns_no_store_nav_and_retention_info()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/audit-log", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("/admin/audit-log", html, StringComparison.Ordinal);
        Assert.Contains("MAILER_ADMIN_AUDIT_RETENTION_DAYS", html, StringComparison.Ordinal);
        Assert.Contains(AdminAuditLogPage.RetentionRunbookUrl, html, StringComparison.Ordinal);
        Assert.Contains("append-only", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_audit_event_appears_on_audit_log_page_without_password()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/audit-log", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AdminAuditLog.EventTypes.LoginSucceeded, html, StringComparison.Ordinal);
        Assert.Contains(MailerAdminFixture.Username, html, StringComparison.Ordinal);
        Assert.DoesNotContain(MailerAdminFixture.Password, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_view_audit_on_list_page_excludes_mail_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "audit-list@example.com";
        const string subject = "Audit List Subject";
        const string htmlBody = "audit-list-body-content";

        var mailRequestId = await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            recipient,
            subject,
            htmlBody,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var bodyView = await client.GetAsync($"/admin/mail-requests/{mailRequestId:D}/body?field=html_body", ct);
        Assert.Equal(HttpStatusCode.OK, bodyView.StatusCode);

        using var response = await client.GetAsync("/admin/audit-log", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AdminAuditLog.EventTypes.MailRequestBodyViewed, html, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient, html, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, html, StringComparison.Ordinal);
        Assert.DoesNotContain(htmlBody, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_admin_cannot_see_other_tenant_mail_request_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "audit-scope-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        var visibleMailRequestId = Guid.NewGuid();
        var hiddenMailRequestId = Guid.NewGuid();
        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var occurredAt = new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero);

        await SeedMailRequestRowAsync(visibleMailRequestId, MailerWebApplicationFixtureBase.TenantId, ct);
        await SeedMailRequestRowAsync(hiddenMailRequestId, OtherTenantId, ct);

        await repository.WriteAsync(
            NewMailRequestAuditEvent(visibleMailRequestId, occurredAt, "visible-audit"),
            ct);
        await repository.WriteAsync(
            NewMailRequestAuditEvent(hiddenMailRequestId, occurredAt.AddMinutes(1), "hidden-audit"),
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync("/admin/audit-log", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(visibleMailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenMailRequestId.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_admin_can_see_auth_events_service_wide()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "audit-auth-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        await repository.WriteAsync(
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.LoginFailed,
                Actor = "other-admin-user",
                OccurredAt = new DateTimeOffset(2026, 7, 3, 11, 0, 0, TimeSpan.Zero),
                TargetType = AdminAuditLog.TargetTypes.AdminSession,
                Result = AdminAuditLog.Results.Failure,
            },
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync("/admin/audit-log", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AdminAuditLog.EventTypes.LoginFailed, html, StringComparison.Ordinal);
        Assert.Contains("other-admin-user", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_type_filter_returns_matching_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var occurredAt = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(
            NewAuthAuditEvent(AdminAuditLog.EventTypes.Logout, "logout-only-user", occurredAt),
            ct);
        await repository.WriteAsync(
            NewAuthAuditEvent(AdminAuditLog.EventTypes.SessionExpired, "session-only-user", occurredAt.AddMinutes(1)),
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync(
            $"/admin/audit-log?event_type={Uri.EscapeDataString(AdminAuditLog.EventTypes.Logout)}",
            ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("logout-only-user", html, StringComparison.Ordinal);
        Assert.DoesNotContain("session-only-user", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Actor_filter_returns_matching_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var occurredAt = new DateTimeOffset(2026, 7, 3, 13, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(NewAuthAuditEvent(AdminAuditLog.EventTypes.Logout, "actor-alpha", occurredAt), ct);
        await repository.WriteAsync(
            NewAuthAuditEvent(AdminAuditLog.EventTypes.Logout, "actor-beta", occurredAt.AddMinutes(1)),
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/audit-log?actor=actor-alpha", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("actor-alpha", html, StringComparison.Ordinal);
        Assert.DoesNotContain("actor-beta", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_detail_returns_not_found_for_out_of_scope_mail_request_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "audit-detail-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        var hiddenMailRequestId = Guid.NewGuid();
        await SeedMailRequestRowAsync(hiddenMailRequestId, OtherTenantId, ct);

        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        await repository.WriteAsync(
            NewMailRequestAuditEvent(
                hiddenMailRequestId,
                new DateTimeOffset(2026, 7, 3, 14, 0, 0, TimeSpan.Zero),
                "hidden-detail"),
            ct);
        var row = (await repository.ListRecentAsync(1, ct))[0];

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync($"/admin/audit-log/{row.Id}", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Audit_detail_shows_in_scope_event_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestRowAsync(mailRequestId, MailerWebApplicationFixtureBase.TenantId, ct);

        var repository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        await repository.WriteAsync(
            NewMailRequestAuditEvent(
                mailRequestId,
                new DateTimeOffset(2026, 7, 3, 15, 0, 0, TimeSpan.Zero),
                "detail-user"),
            ct);
        var row = (await repository.ListRecentAsync(1, ct))[0];

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync($"/admin/audit-log/{row.Id}", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(row.EventType, html, StringComparison.Ordinal);
        Assert.Contains("detail-user", html, StringComparison.Ordinal);
        Assert.Contains(mailRequestId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("/admin/mail-requests/", html, StringComparison.Ordinal);
    }

    private static AdminAuditEvent NewMailRequestAuditEvent(
        Guid mailRequestId,
        DateTimeOffset occurredAt,
        string actor) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.ManualRetryRequested,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.MailRequest,
            TargetId = mailRequestId.ToString("D"),
            Result = AdminAuditLog.Results.Success,
        };

    private static AdminAuditEvent NewAuthAuditEvent(
        string eventType,
        string actor,
        DateTimeOffset occurredAt) =>
        new()
        {
            EventType = eventType,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.AdminSession,
            Result = AdminAuditLog.Results.Success,
        };

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
        string recipientEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await SeedMailRequestRowAsync(id, tenantId, cancellationToken, recipientEmail, subject, htmlBody);
        return id;
    }

    private async Task SeedMailRequestRowAsync(
        Guid id,
        Guid tenantId,
        CancellationToken cancellationToken,
        string recipientEmail = "seed@example.com",
        string subject = "Seed subject",
        string? htmlBody = null)
    {
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
                @Id, @TenantId, @SourceService, @MailRequestId, 'AuditLogPageTest',
                '{}', @PayloadHash, @Subject, @HtmlBody, @RecipientEmail,
                @Status, 1, 3, NULL,
                @AcceptedAt, @CreatedAt, @UpdatedAt, NULL, NULL);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Subject", subject);
        command.Parameters.AddWithValue("@HtmlBody", (object?)htmlBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
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

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken) =>
        await LoginAsync(client, MailerAdminFixture.Username, MailerAdminFixture.Password, cancellationToken);

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
}
