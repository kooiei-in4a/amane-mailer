using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminSuppressionsListUnmaskedAuditTests
    : IClassFixture<MailerAdminUnmaskedListFixture>,
      IClassFixture<MailerAdminFixture>,
      IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 27, 1, 0, 0, TimeSpan.Zero);

    private readonly MailerAdminUnmaskedListFixture _unmaskedFixture;
    private readonly MailerAdminFixture _maskedFixture;

    public AdminSuppressionsListUnmaskedAuditTests(
        MailerAdminUnmaskedListFixture unmaskedFixture,
        MailerAdminFixture maskedFixture)
    {
        _unmaskedFixture = unmaskedFixture;
        _maskedFixture = maskedFixture;
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _unmaskedFixture.ResetAsync(ct);
        await _maskedFixture.ResetAsync(ct);
        ClearAdminCaches(_unmaskedFixture);
        ClearAdminCaches(_maskedFixture);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Unmasked_suppressions_list_writes_audit_without_recipient()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "audit-suppression@example.com";
        await SeedSuppressionAsync(
            _unmaskedFixture.ConnectionString,
            MailerWebApplicationFixtureBase.TenantId,
            recipient,
            ct);

        using var client = CreateClient(_unmaskedFixture.Factory);
        await LoginAsync(client, MailerAdminUnmaskedListFixture.Username, MailerAdminUnmaskedListFixture.Password, ct);

        var tenantId = MailerWebApplicationFixtureBase.TenantId;
        using var response = await client.GetAsync($"/admin/suppressions?tenant_id={tenantId:D}", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A suppression without a durable request/role/ordinal correlation is fail-closed:
        // unmasked list mode must not turn an unknown origin into raw recipient output.
        Assert.DoesNotContain(recipient, html, StringComparison.Ordinal);
        Assert.Contains(">***</td>", html, StringComparison.Ordinal);

        var rows = await ReadAuditEventsAsync(
            _unmaskedFixture.ConnectionString,
            AdminAuditLog.EventTypes.MailSuppressionsListUnmasked,
            ct);
        var row = Assert.Single(rows);
        Assert.Equal(MailerAdminUnmaskedListFixture.Username, row.Actor);
        Assert.Equal(AdminAuditLog.TargetTypes.MailSuppressions, row.TargetType);
        Assert.Equal("success", row.Result);
        Assert.Equal("result_count=1;tenant_filter=specific", row.FieldName);
        Assert.Equal(tenantId.ToString("D"), row.TenantId);
        foreach (var value in row.AllValues)
        {
            if (value is null)
                continue;
            Assert.DoesNotContain(recipient, value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Masked_suppressions_list_does_not_write_list_unmasked_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSuppressionAsync(
            _maskedFixture.ConnectionString,
            MailerWebApplicationFixtureBase.TenantId,
            "masked-suppression@example.com",
            ct);

        using var client = CreateClient(_maskedFixture.Factory);
        await LoginAsync(client, MailerAdminFixture.Username, MailerAdminFixture.Password, ct);

        using var response = await client.GetAsync("/admin/suppressions", ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("masked-suppression@example.com", html, StringComparison.Ordinal);
        Assert.Contains(">***</td>", html, StringComparison.Ordinal);

        var rows = await ReadAuditEventsAsync(
            _maskedFixture.ConnectionString,
            AdminAuditLog.EventTypes.MailSuppressionsListUnmasked,
            ct);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Unmasked_suppressions_list_fails_closed_when_audit_cannot_be_persisted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brokenFixture = new MailerAdminUnmaskedListFixture();
        await brokenFixture.InitializeAsync();

        const string recipient = "fail-closed-suppression@example.com";
        await SeedSuppressionAsync(
            brokenFixture.ConnectionString,
            MailerWebApplicationFixtureBase.TenantId,
            recipient,
            ct);

        using var client = CreateClient(brokenFixture.Factory);
        await LoginAsync(
            client,
            MailerAdminUnmaskedListFixture.Username,
            MailerAdminUnmaskedListFixture.Password,
            ct);

        // Remove the audit store so the fail-closed unmasked-list write cannot land.
        await using (var connection = new SqliteConnection(brokenFixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE admin_audit_events;";
            await drop.ExecuteNonQueryAsync(ct);
        }

        using var response = await client.GetAsync(
            $"/admin/suppressions?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}",
            ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(recipient, content, StringComparison.Ordinal);
        Assert.DoesNotContain("f***@e***.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("抑制リスト", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unmasked_suppressions_without_tenant_redirects_when_single_tenant_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(_unmaskedFixture.Factory);
        await LoginAsync(
            client,
            MailerAdminUnmaskedListFixture.Username,
            MailerAdminUnmaskedListFixture.Password,
            ct);

        using var response = await client.GetAsync("/admin/suppressions", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"/admin/suppressions?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}",
            response.Headers.Location?.OriginalString);
        Assert.Empty(await ReadAuditEventsAsync(
            _unmaskedFixture.ConnectionString,
            AdminAuditLog.EventTypes.MailSuppressionsListUnmasked,
            ct));
    }

    private static void ClearAdminCaches(MailerWebApplicationFixtureBase fixture)
    {
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        return client;
    }

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrfToken,
                ["username"] = username,
                ["password"] = password,
            }),
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
        Assert.True(end > start);
        return html[start..end];
    }

    private static async Task SeedSuppressionAsync(
        string connectionString,
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_suppressions (
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
            VALUES (
                @Id, @TenantId, @RecipientEmail, @Reason, NULL, @Now);
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", RecipientEmailNormalizer.Normalize(recipientEmail));
        command.Parameters.AddWithValue("@Reason", MailSuppressionReasons.HardBounce);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<AuditRow>> ReadAuditEventsAsync(
        string connectionString,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT actor, target_type, target_id, tenant_id, field_name, result,
                   source_ip, user_agent_summary, error_code, event_type
            FROM admin_audit_events
            WHERE event_type = @EventType
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("@EventType", eventType);

        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(
                Actor: reader.GetString(0),
                TargetType: reader.IsDBNull(1) ? null : reader.GetString(1),
                TargetId: reader.IsDBNull(2) ? null : reader.GetString(2),
                TenantId: reader.IsDBNull(3) ? null : reader.GetString(3),
                FieldName: reader.IsDBNull(4) ? null : reader.GetString(4),
                Result: reader.GetString(5),
                SourceIp: reader.IsDBNull(6) ? null : reader.GetString(6),
                UserAgentSummary: reader.IsDBNull(7) ? null : reader.GetString(7),
                ErrorCode: reader.IsDBNull(8) ? null : reader.GetString(8),
                EventType: reader.GetString(9)));
        }

        return rows;
    }

    private sealed record AuditRow(
        string Actor,
        string? TargetType,
        string? TargetId,
        string? TenantId,
        string? FieldName,
        string Result,
        string? SourceIp,
        string? UserAgentSummary,
        string? ErrorCode,
        string EventType)
    {
        public IEnumerable<string?> AllValues =>
        [
            Actor, TargetType, TargetId, TenantId, FieldName, Result,
            SourceIp, UserAgentSummary, ErrorCode, EventType,
        ];
    }
}
