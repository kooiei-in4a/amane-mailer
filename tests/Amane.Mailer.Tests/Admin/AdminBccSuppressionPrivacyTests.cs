using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminBccSuppressionPrivacyTests(MailerAdminUnmaskedListFixture fixture)
    : IClassFixture<MailerAdminUnmaskedListFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Visible_suppression_list_keeps_bcc_origin_masked()
    {
        var ct = TestContext.Current.CancellationToken;
        const string address = "bcc-suppression-secret@example.com";
        await SeedSuppressionAsync(
            fixture.ConnectionString,
            address,
            MailRecipientRole.Bcc,
            address,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);
        using var response = await client.GetAsync(
            $"/admin/suppressions?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}",
            ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(address, html, StringComparison.Ordinal);
        Assert.Contains(">***</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visible_suppression_list_masks_recipient_identity_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        const string suppressionAddress = "mismatched-bcc-secret@example.com";
        const string sourceAddress = "unrelated-to@example.com";
        await SeedSuppressionAsync(
            fixture.ConnectionString,
            suppressionAddress,
            MailRecipientRole.To,
            sourceAddress,
            ct);

        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);
        using var response = await client.GetAsync(
            $"/admin/suppressions?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}",
            ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(suppressionAddress, html, StringComparison.Ordinal);
        Assert.Contains(">***</td>", html, StringComparison.Ordinal);
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
        using var login = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginHtml = await login.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = loginHtml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = loginHtml.IndexOf('"', start);
        Assert.True(end > start);
        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = loginHtml[start..end],
                ["username"] = MailerAdminUnmaskedListFixture.Username,
                ["password"] = MailerAdminUnmaskedListFixture.Password,
            }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task SeedSuppressionAsync(
        string connectionString,
        string suppressionAddress,
        MailRecipientRole sourceRole,
        string sourceAddress,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var mailRequestId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 8, 7, 3, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, attachment_count,
                accepted_at, created_at, updated_at)
            VALUES (
                @RequestId, @TenantId, 'example-service', @MailRequestId, 'bcc-suppression-test',
                '{}', @PayloadHash, 'subject', @LegacyRecipient,
                3, 1, 3, 0, @Now, @Now, @Now);

            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key,
                display_name, delivery_state, created_at, updated_at)
            VALUES (
                @RequestId, @Role, 0, @SourceAddress, @SourceAddressKey, 'Source recipient', 3, @Now, @Now);

            INSERT INTO recipient_delivery_events (
                id, tenant_id, source_service, mail_request_id,
                recipient_role, recipient_ordinal, provider,
                provider_event_id, provider_message_id, provider_status,
                applied_delivery_state, status_message, occurred_at, created_at)
            VALUES (
                @EventId, @TenantId, 'example-service', @MailRequestId,
                @Role, 0, 'acs', @ProviderEventId, @ProviderMessageId, 'Bounced',
                3, 'sanitized', @Now, @Now);

            INSERT INTO mail_suppressions (
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
            VALUES (
                @SuppressionId, @TenantId, @SuppressionAddressKey, 'hard_bounce', @EventId, @Now);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
        command.Parameters.AddWithValue("@LegacyRecipient", MailRequestLegacyShadow.BccOnlyRecipientEmail);
        command.Parameters.AddWithValue("@Role", (int)sourceRole);
        command.Parameters.AddWithValue("@SourceAddress", sourceAddress);
        command.Parameters.AddWithValue("@SourceAddressKey", RecipientEmailNormalizer.Normalize(sourceAddress));
        command.Parameters.AddWithValue("@EventId", eventId.ToString("D"));
        command.Parameters.AddWithValue("@ProviderEventId", "event-" + eventId.ToString("N"));
        command.Parameters.AddWithValue("@ProviderMessageId", "message-" + eventId.ToString("N"));
        command.Parameters.AddWithValue("@SuppressionId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue(
            "@SuppressionAddressKey",
            RecipientEmailNormalizer.Normalize(suppressionAddress));
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
