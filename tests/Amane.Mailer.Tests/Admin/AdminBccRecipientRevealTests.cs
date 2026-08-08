using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminBccRecipientRevealTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Bcc_reveal_requires_capability_audits_and_revokes_existing_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = Guid.NewGuid();
        const string bccAddress = "bcc-secret@example.com";
        await SeedBccRequestAsync(fixture.ConnectionString, requestId, bccAddress, ct);

        using var beforeGrant = CreateClient(fixture.Factory);
        await LoginAsync(beforeGrant, ct);

        using (var detail = await beforeGrant.GetAsync($"/admin/mail-requests/{requestId:D}", ct))
        {
            var html = await detail.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Contains(">***</td>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(bccAddress, html, StringComparison.Ordinal);
            Assert.DoesNotContain("/recipients/bcc/0", html, StringComparison.Ordinal);
        }

        var users = fixture.Factory.Services.GetRequiredService<AdminUserRepository>();
        Assert.Equal(
            AdminCapabilityMutationResult.Changed,
            await users.SetCapabilityAsync(
                MailerAdminFixture.Username,
                AdminCapabilities.BccRecipientReveal,
                grant: true,
                ct));

        // Capability changes increment credential_epoch and revoke sessions atomically.
        using (var revoked = await beforeGrant.GetAsync($"/admin/mail-requests/{requestId:D}", ct))
            Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);

        using var afterGrant = CreateClient(fixture.Factory);
        await LoginAsync(afterGrant, ct);
        using (var detail = await afterGrant.GetAsync($"/admin/mail-requests/{requestId:D}", ct))
        {
            var html = await detail.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Contains($"/admin/mail-requests/{requestId:D}/recipients/bcc/0", html, StringComparison.Ordinal);
            Assert.DoesNotContain(bccAddress, html, StringComparison.Ordinal);
        }

        using (var reveal = await afterGrant.GetAsync(
                   $"/admin/mail-requests/{requestId:D}/recipients/bcc/0",
                   ct))
        {
            var html = await reveal.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
            Assert.Equal("no-store", reveal.Headers.CacheControl?.ToString());
            Assert.Contains(bccAddress, html, StringComparison.Ordinal);
            Assert.Contains("Secret BCC", html, StringComparison.Ordinal);
        }

        using (var wrongOrdinal = await afterGrant.GetAsync(
                   $"/admin/mail-requests/{requestId:D}/recipients/bcc/1",
                   ct))
            Assert.Equal(HttpStatusCode.NotFound, wrongOrdinal.StatusCode);

        var toOnlyRequestId = Guid.NewGuid();
        await SeedBccRequestAsync(
            fixture.ConnectionString,
            toOnlyRequestId,
            "to-only@example.com",
            ct,
            MailRecipientRole.To,
            "To only");
        using (var wrongRole = await afterGrant.GetAsync(
                   $"/admin/mail-requests/{toOnlyRequestId:D}/recipients/bcc/0",
                   ct))
            Assert.Equal(HttpStatusCode.NotFound, wrongRole.StatusCode);

        var audit = await ReadRevealAuditAsync(fixture.ConnectionString, ct);
        Assert.Equal(MailerAdminFixture.Username, audit.Actor);
        Assert.Equal(requestId.ToString("D"), audit.TargetId);
        Assert.Equal(MailerWebApplicationFixtureBase.TenantId.ToString("D"), audit.TenantId);
        Assert.Equal("bcc[0]", audit.FieldName);
        Assert.Equal("success", audit.Result);
        Assert.DoesNotContain(bccAddress, audit.AllValues);

        await users.SetCapabilityAsync(
            MailerAdminFixture.Username,
            AdminCapabilities.BccRecipientReveal,
            grant: false,
            ct);

        using var afterRevoke = CreateClient(fixture.Factory);
        await LoginAsync(afterRevoke, ct);
        using var denied = await afterRevoke.GetAsync(
            $"/admin/mail-requests/{requestId:D}/recipients/bcc/0",
            ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["username"] = MailerAdminFixture.Username,
                ["password"] = MailerAdminFixture.Password,
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
        Assert.True(start >= 0);
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static async Task SeedBccRequestAsync(
        string connectionString,
        Guid requestId,
        string address,
        CancellationToken cancellationToken,
        MailRecipientRole role = MailRecipientRole.Bcc,
        string displayName = "Secret BCC")
    {
        var now = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 8, 7, 1, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'BccRevealTest',
                '{}', @PayloadHash, 'BCC subject', @LegacyRecipient,
                0, 0, 3, @Now, @Now, @Now);

            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key, display_name,
                delivery_state, provider_message_id, provider_status_detail, created_at, updated_at)
            VALUES (
                @Id, @Role, 0, @Address, @AddressKey, @DisplayName,
                0, NULL, NULL, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@LegacyRecipient", MailRequestLegacyShadow.BccOnlyRecipientEmail);
        command.Parameters.AddWithValue("@Address", address);
        command.Parameters.AddWithValue("@AddressKey", RecipientEmailNormalizer.Normalize(address));
        command.Parameters.AddWithValue("@Role", (int)role);
        command.Parameters.AddWithValue("@DisplayName", displayName);
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AuditRow> ReadRevealAuditAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT actor, target_id, tenant_id, field_name, result
            FROM admin_audit_events
            WHERE event_type = @EventType
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventType", AdminAuditLog.EventTypes.BccRecipientRevealed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new AuditRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    private sealed record AuditRow(
        string Actor,
        string TargetId,
        string TenantId,
        string FieldName,
        string Result)
    {
        public IEnumerable<string> AllValues => [Actor, TargetId, TenantId, FieldName, Result];
    }
}
