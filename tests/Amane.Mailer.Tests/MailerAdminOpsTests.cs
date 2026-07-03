using System.Globalization;
using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminOpsTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Unauthenticated_ops_redirects_to_login()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/admin/ops", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_ops_returns_no_store_and_nav_link()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/ops", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("/admin/ops", html, StringComparison.Ordinal);
        Assert.Contains("Queue metrics", html, StringComparison.Ordinal);
        Assert.Contains("Database storage", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ops_metrics_match_db_stats_cli_for_default_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-45),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Failed,
            FixedNow.AddMinutes(-20),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-45),
            nextAttemptAt: null,
            ct);

        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = fixture.ConnectionString,
                })
                .Build());
        var command = new DbStatsCommand(factory, () => FixedNow);
        var cliOutput = new StringWriter();
        var cliError = new StringWriter();
        var exitCode = await command.ExecuteAsync(
            [
                "db",
                "stats",
                "--tenant-id",
                MailerWebApplicationFixtureBase.TenantId.ToString("D"),
            ],
            cliOutput,
            cliError,
            ct);

        Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);
        var cliStats = ParseStats(cliOutput.ToString());

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);
        using var response = await client.GetAsync("/admin/ops", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            $"<dd>{cliStats["status_queued"]}</dd>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<dd>{cliStats["status_failed"]}</dd>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantId.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient@", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Subject", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ops_shows_schema_migrations_and_database_size()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/ops", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("001_initial.sql", html, StringComparison.Ordinal);
        Assert.Contains("007_mail_request_cancelled_status.sql", html, StringComparison.Ordinal);
        Assert.Contains("mailer.db", html, StringComparison.Ordinal);
        Assert.Contains("bytes", html, StringComparison.Ordinal);
        Assert.Contains("wal", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable on read-only page", html, StringComparison.Ordinal);
        Assert.DoesNotContain("busy=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_admin_ops_excludes_outside_tenant_metrics()
    {
        var ct = TestContext.Current.CancellationToken;
        var username = "ops-admin-" + Guid.NewGuid().ToString("N");
        await CreateScopedUserAsync(username, [MailerWebApplicationFixtureBase.TenantId], ct);

        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-10),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-10),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-10),
            nextAttemptAt: null,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, username, TenantAdminPassword(username), ct);

        using var response = await client.GetAsync("/admin/ops", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Scoped tenants", html, StringComparison.Ordinal);
        var queuedIndex = html.IndexOf("<dt>Queued</dt>", StringComparison.Ordinal);
        Assert.True(queuedIndex >= 0);
        var queuedSection = html.Substring(queuedIndex, Math.Min(80, html.Length - queuedIndex));
        Assert.Contains("<dd>1</dd>", queuedSection, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherTenantId.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ops_shows_provider_attempt_counts_without_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Failed,
            FixedNow.AddMinutes(-5),
            nextAttemptAt: null,
            ct);
        await SeedMailAttemptAsync(requestId, "mailpit", (int)MailRequestState.Failed, ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        using var response = await client.GetAsync("/admin/ops", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Provider attempts", html, StringComparison.Ordinal);
        Assert.Contains("mailpit", html, StringComparison.Ordinal);
        Assert.Contains("failed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error_message", html, StringComparison.OrdinalIgnoreCase);
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
        DateTimeOffset updatedAt,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, next_attempt_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'OpsTest',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, 0, 3, @NextAttemptAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", "Ops subject must not leak");
        command.Parameters.AddWithValue("@RecipientEmail", "ops-recipient@example.com");
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@NextAttemptAt", nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static async Task SeedMailAttemptAsync(
        Guid requestId,
        string provider,
        int status,
        CancellationToken cancellationToken,
        string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message,
                retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, 1, @Provider, @Status,
                NULL, 'provider_error', 'secret provider detail',
                0, @LockToken, @StartedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@Provider", provider);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(-1)));
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task SeedMailAttemptAsync(
        Guid requestId,
        string provider,
        int status,
        CancellationToken cancellationToken) =>
        SeedMailAttemptAsync(requestId, provider, status, cancellationToken, fixture.ConnectionString);

    private static Dictionary<string, string> ParseStats(string output)
    {
        var stats = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            stats[line[..separator]] = line[(separator + 1)..];
        }

        return stats;
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
