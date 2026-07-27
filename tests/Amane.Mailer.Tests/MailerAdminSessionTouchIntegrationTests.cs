using System.Net;
using System.Net.Http;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminSessionTouchIntegrationTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Login_cookie_ticket_expires_at_absolute_lifetime_not_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        using var loginResponse = await LoginCapturingResponseAsync(client, ct);

        var sessionId = await GetSingleActiveSessionAsync(fixture.ConnectionString, ct);
        var session = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);
        var ticketExpires = ReadAuthTicketExpiresUtc(fixture.Factory.Services, loginResponse);

        Assert.True(ticketExpires <= session.AbsoluteExpiresAt);
        Assert.Equal(AlignToUtcSeconds(session.AbsoluteExpiresAt), AlignToUtcSeconds(ticketExpires));
        Assert.True(
            ticketExpires > session.IdleExpiresAt,
            "Cookie must outlive the initial idle window; idle is enforced from DB only.");
    }

    [Fact]
    public async Task Authenticated_burst_within_touch_interval_does_not_write_or_set_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        var sessionId = await GetSingleActiveSessionAsync(fixture.ConnectionString, ct);
        var before = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);

        for (var i = 0; i < 8; i++)
        {
            using var response = await client.GetAsync("/admin/mail-requests", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(
                HasAuthSetCookie(response),
                "Touch must not issue an auth Set-Cookie.");
        }

        var after = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);
        Assert.Equal(before.LastSeenAt, after.LastSeenAt);
        Assert.Equal(before.IdleExpiresAt, after.IdleExpiresAt);
    }

    [Fact]
    public async Task Touch_after_interval_updates_session_without_set_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        var sessionId = await GetSingleActiveSessionAsync(fixture.ConnectionString, ct);
        var options = fixture.Factory.Services.GetRequiredService<MailerAdminOptions>();
        var touchInterval = AdminSessionTouch.ResolveInterval(options.SessionIdleTimeout);
        var agedLastSeen = DateTimeOffset.UtcNow - touchInterval - TimeSpan.FromSeconds(5);
        await SetLastSeenAsync(fixture.ConnectionString, sessionId, agedLastSeen, ct);

        using var first = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(
            HasAuthSetCookie(first),
            "Successful DB touch must not renew the auth cookie.");

        var afterTouch = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);
        Assert.True(afterTouch.LastSeenAt > agedLastSeen);
        Assert.True(afterTouch.IdleExpiresAt <= afterTouch.AbsoluteExpiresAt);

        using var second = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(HasAuthSetCookie(second));

        var afterSkip = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);
        Assert.Equal(afterTouch.LastSeenAt, afterSkip.LastSeenAt);
        Assert.Equal(afterTouch.IdleExpiresAt, afterSkip.IdleExpiresAt);
    }

    [Fact]
    public async Task Parallel_authenticated_requests_after_interval_touch_db_without_set_cookie()
    {
        var ct = TestContext.Current.CancellationToken;
        using var loginClient = CreateClient(fixture.Factory);
        var authCookie = await LoginAndCaptureAuthCookieAsync(loginClient, ct);

        var sessionId = await GetSingleActiveSessionAsync(fixture.ConnectionString, ct);
        var options = fixture.Factory.Services.GetRequiredService<MailerAdminOptions>();
        var touchInterval = AdminSessionTouch.ResolveInterval(options.SessionIdleTimeout);
        var agedLastSeen = DateTimeOffset.UtcNow - touchInterval - TimeSpan.FromSeconds(5);
        await SetLastSeenAsync(fixture.ConnectionString, sessionId, agedLastSeen, ct);

        var tasks = Enumerable.Range(0, 6)
            .Select(async _ =>
            {
                using var client = CreateClient(fixture.Factory);
                client.DefaultRequestHeaders.Add("Cookie", authCookie);
                using var response = await client.GetAsync("/admin/mail-requests", ct);
                return (response.StatusCode, Renewed: HasAuthSetCookie(response));
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.StatusCode));
        Assert.All(results, result => Assert.False(result.Renewed));

        var session = await ReadSessionTimestampsAsync(fixture.ConnectionString, sessionId, ct);
        Assert.True(session.LastSeenAt > agedLastSeen);
        Assert.True(session.IdleExpiresAt <= session.AbsoluteExpiresAt);
    }

    private static bool HasAuthSetCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return false;

        var prefix = AdminCookieTransportPolicy.SecureAuthCookieName + "=";
        return values.Any(value => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static DateTimeOffset ReadAuthTicketExpiresUtc(
        IServiceProvider services,
        HttpResponseMessage response)
    {
        var prefix = AdminCookieTransportPolicy.SecureAuthCookieName + "=";
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(prefix, StringComparison.Ordinal));
        var encoded = setCookie.Split(';', 2)[0][prefix.Length..];
        var ticketValue = Uri.UnescapeDataString(encoded);
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationConstants.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(ticketValue);
        Assert.NotNull(ticket);
        Assert.NotNull(ticket.Properties.ExpiresUtc);
        return ticket.Properties.ExpiresUtc.Value;
    }

    private static DateTimeOffset AlignToUtcSeconds(DateTimeOffset value) =>
        new(
            value.UtcDateTime.Year,
            value.UtcDateTime.Month,
            value.UtcDateTime.Day,
            value.UtcDateTime.Hour,
            value.UtcDateTime.Minute,
            value.UtcDateTime.Second,
            TimeSpan.Zero);

    private static async Task<(DateTimeOffset LastSeenAt, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt)>
        ReadSessionTimestampsAsync(string connectionString, string sessionId, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_seen_at, idle_expires_at, absolute_expires_at
            FROM admin_sessions
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (
            SqliteTime.FromStorage(reader.GetString(0)),
            SqliteTime.FromStorage(reader.GetString(1)),
            SqliteTime.FromStorage(reader.GetString(2)));
    }

    private static async Task SetLastSeenAsync(
        string connectionString,
        string sessionId,
        DateTimeOffset lastSeenAt,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE admin_sessions
            SET last_seen_at = @LastSeenAt
            WHERE session_id = @SessionId;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);
        command.Parameters.AddWithValue("@LastSeenAt", SqliteTime.ToStorageUtc(lastSeenAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> GetSingleActiveSessionAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id
            FROM admin_sessions
            WHERE revoked_at IS NULL
            ORDER BY issued_at DESC
            LIMIT 1;
            """;
        var result = await command.ExecuteScalarAsync(ct);
        return Assert.IsType<string>(result);
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await LoginCapturingResponseAsync(client, cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> LoginCapturingResponseAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response;
    }

    private static async Task<string> LoginAndCaptureAuthCookieAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await LoginCapturingResponseAsync(client, cancellationToken);
        var prefix = AdminCookieTransportPolicy.SecureAuthCookieName + "=";
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(prefix, StringComparison.Ordinal));
        return setCookie.Split(';')[0];
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
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });
}
