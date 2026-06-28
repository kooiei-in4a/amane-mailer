using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminHashNetworkTests(MailerAdminHashNetworkFixture fixture)
    : IClassFixture<MailerAdminHashNetworkFixture>, IAsyncLifetime
{
    private const string RawIp = "127.0.0.1";

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Failed_login_hides_raw_ip_in_throttle_and_auth_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        var csrfToken = await ReadCsrfTokenAsync(client, ct);
        using var failedLogin = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
            ct);
        Assert.Equal(HttpStatusCode.Unauthorized, failedLogin.StatusCode);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using (var throttleCommand = connection.CreateCommand())
        {
            throttleCommand.CommandText = "SELECT throttle_key FROM admin_login_throttle LIMIT 1;";
            var throttleKey = Assert.IsType<string>(await throttleCommand.ExecuteScalarAsync(ct));
            Assert.DoesNotContain(RawIp, throttleKey, StringComparison.Ordinal);
        }

        await using (var auditCommand = connection.CreateCommand())
        {
            auditCommand.CommandText = """
                SELECT source_ip
                FROM admin_audit_events
                WHERE event_type = 'auth.login_failed'
                  AND source_ip IS NOT NULL;
                """;
            var sourceIp = Assert.IsType<string>(await auditCommand.ExecuteScalarAsync(ct));
            Assert.DoesNotContain(RawIp, sourceIp, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Body_view_hides_raw_ip_in_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = await SeedMailRequestWithBodyAsync(fixture.ConnectionString, ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var bodyView = await client.GetAsync($"/admin/mail-requests/{requestId:D}/body?field=html_body", ct);
        Assert.Equal(HttpStatusCode.OK, bodyView.StatusCode);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var auditCommand = connection.CreateCommand();
        auditCommand.CommandText = """
            SELECT source_ip
            FROM admin_audit_events
            WHERE event_type = 'mail_request.body_viewed'
              AND source_ip IS NOT NULL;
            """;
        var sourceIp = Assert.IsType<string>(await auditCommand.ExecuteScalarAsync(ct));
        Assert.DoesNotContain(RawIp, sourceIp, StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedMailRequestWithBodyAsync(string connectionString, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, metadata_json, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AdminHashModeTest',
                '{}', @PayloadHash, 'subject', @HtmlBody, 'text', '{}', 'user@example.com',
                0, 0, 3,
                @At, @At, @At);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@HtmlBody", "secret-body");
        command.Parameters.AddWithValue("@At", now);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
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
