using System.Net;
using System.Net.Http.Json;
using System.Text;
using Amane.Mailer.Api;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class SenderApiKeyIdentityTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SenderRepository>()
            .EnableAsync(MailerWebApplicationFixtureBase.TenantId, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Sender_email_is_normalized_unique_and_disabled_identity_remains_resolvable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var email = $" Sender-{Guid.NewGuid():N}@EXAMPLE.COM ";

        var sender = await senders.CreateAsync(email, " Sender ", ct);
        Assert.Equal(email.Trim().ToLowerInvariant(), sender.Email);
        await Assert.ThrowsAsync<SqliteException>(() =>
            senders.CreateAsync(email.Trim().ToLowerInvariant(), "duplicate", ct));

        await senders.DisableAsync(sender.SenderId, ct);
        var historical = await senders.FindAsync(sender.SenderId, ct);
        Assert.NotNull(historical);
        Assert.False(historical.Enabled);
        Assert.NotNull(historical.DisabledAt);
    }

    [Fact]
    public async Task Sender_can_have_multiple_keys_and_plaintext_is_never_persisted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var first = await senders.CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "first", ct);
        var second = await senders.CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "second", ct);

        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.NotEqual(first.Plaintext, second.Plaintext);
        Assert.StartsWith($"amk_{first.KeyId:N}.", first.Plaintext, StringComparison.Ordinal);
        var authenticated = await senders.AuthenticateAsync(second.Plaintext, ct);
        Assert.NotNull(authenticated);
        Assert.Equal(second.KeyId, authenticated.KeyId);
        Assert.Equal(second.SenderId, authenticated.Sender.SenderId);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), MIN(length(secret_digest)), MAX(typeof(secret_digest)),
                   SUM(CASE WHEN name = @Plaintext THEN 1 ELSE 0 END)
            FROM api_keys
            WHERE sender_id = @SenderId AND key_id IN (@First, @Second);
            """;
        command.Parameters.AddWithValue("@Plaintext", first.Plaintext);
        command.Parameters.AddWithValue("@SenderId", first.SenderId.ToString("D"));
        command.Parameters.AddWithValue("@First", first.KeyId.ToString("D"));
        command.Parameters.AddWithValue("@Second", second.KeyId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(32, reader.GetInt32(1));
        Assert.Equal("blob", reader.GetString(2));
        Assert.Equal(0, reader.GetInt32(3));
    }

    [Fact]
    public async Task Sender_and_api_key_admin_queries_return_counts_and_metadata_without_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var isolatedSender = await senders.CreateAsync($"admin-query-{Guid.NewGuid():N}@example.com", "Admin query", ct);
        var first = await senders.CreateApiKeyAsync(isolatedSender.SenderId, "first", ct);
        var second = await senders.CreateApiKeyAsync(isolatedSender.SenderId, "second", ct);

        var sender = Assert.Single(
            await senders.ListAsync(ct),
            item => item.SenderId == isolatedSender.SenderId);
        Assert.Equal(2, sender.ApiKeyCount);

        var metadata = await senders.ListApiKeysAsync(sender.SenderId, ct);
        Assert.Equal(2, metadata.Count);
        Assert.Contains(metadata, key => key.KeyId == first.KeyId && key.Name == first.Name);
        Assert.Contains(metadata, key => key.KeyId == second.KeyId && key.Name == second.Name);
        Assert.DoesNotContain(
            first.Plaintext,
            metadata.SelectMany(key => new[] { key.KeyId.ToString("D"), key.Name }));
    }

    [Fact]
    public async Task Scoped_revoke_rejects_a_key_from_another_sender()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var other = await senders.CreateAsync($"other-{Guid.NewGuid():N}@example.com", "Other", ct);
        var ownerKey = await senders.CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "owner", ct);
        var otherKey = await senders.CreateApiKeyAsync(other.SenderId, "other", ct);

        Assert.False(await senders.RevokeApiKeyAsync(other.SenderId, ownerKey.KeyId, ct));
        Assert.NotNull(await senders.AuthenticateAsync(ownerKey.Plaintext, ct));

        Assert.True(await senders.RevokeApiKeyAsync(other.SenderId, otherKey.KeyId, ct));
        Assert.Null(await senders.AuthenticateAsync(otherKey.Plaintext, ct));
    }

    [Fact]
    public async Task Invalid_unknown_revoked_and_disabled_credentials_share_stable_unauthorized_response()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var revocable = await senders.CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "revoke", ct);

        await AssertUnauthorizedAsync($"amk_{Guid.NewGuid():N}.{new string('A', 43)}", ct);
        await AssertUnauthorizedAsync(
            $"amk_{MailerWebApplicationFixtureBase.TenantId:N}.{new string('B', 43)}",
            ct);

        await senders.RevokeApiKeyAsync(revocable.KeyId, ct);
        await AssertUnauthorizedAsync(revocable.Plaintext, ct);

        await senders.DisableAsync(MailerWebApplicationFixtureBase.TenantId, ct);
        await AssertUnauthorizedAsync(MailerWebApplicationFixtureBase.Token, ct);
        await senders.EnableAsync(MailerWebApplicationFixtureBase.TenantId, ct);
    }

    [Fact]
    public async Task Same_sender_idempotency_key_survives_key_rotation_and_records_accepting_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var rotated = await scope.ServiceProvider.GetRequiredService<SenderRepository>()
            .CreateApiKeyAsync(MailerWebApplicationFixtureBase.TenantId, "rotated", ct);
        var request = MailRequestTestData.CreateRequest();

        using var firstClient = CreateClient(MailerWebApplicationFixtureBase.Token);
        using var rotatedClient = CreateClient(rotated.Plaintext);
        using var first = await firstClient.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        using var replay = await rotatedClient.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        using var status = await rotatedClient.GetAsync($"/api/mail-requests/{request.MailRequestId}", ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(MailRequestAcceptanceStatus.AlreadyAccepted,
            await MailRequestTestData.ReadStatusAsync(replay, ct));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted_api_key_id FROM mail_requests WHERE mail_request_id = @Id;";
        command.Parameters.AddWithValue("@Id", request.MailRequestId.ToString("D"));
        Assert.Equal(
            MailerWebApplicationFixtureBase.TenantId.ToString("D"),
            Assert.IsType<string>(await command.ExecuteScalarAsync(ct)));
    }

    [Fact]
    public async Task Same_id_is_independent_between_senders_and_cross_sender_mutations_are_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        var other = await senders.CreateAsync($"other-{Guid.NewGuid():N}@example.com", "Other", ct);
        var otherKey = await senders.CreateApiKeyAsync(other.SenderId, "other", ct);
        var request = MailRequestTestData.CreateRequest();

        using var ownerClient = CreateClient(MailerWebApplicationFixtureBase.Token);
        using var otherClient = CreateClient(otherKey.Plaintext);
        using var ownerPost = await ownerClient.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        using var otherPost = await otherClient.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(request), ct);
        Assert.Equal(HttpStatusCode.Accepted, ownerPost.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, otherPost.StatusCode);
        Assert.Equal(MailRequestAcceptanceStatus.Accepted,
            await MailRequestTestData.ReadStatusAsync(otherPost, ct));

        using var hiddenGet = await otherClient.GetAsync(
            $"/api/mail-requests/{request.MailRequestId}", ct);
        Assert.Equal(HttpStatusCode.OK, hiddenGet.StatusCode);

        var ownerOnly = MailRequestTestData.CreateRequest();
        using var ownerOnlyPost = await ownerClient.PostAsync(
            "/api/mail-requests", MailRequestTestData.ToJsonContent(ownerOnly), ct);
        using var crossGet = await otherClient.GetAsync($"/api/mail-requests/{ownerOnly.MailRequestId}", ct);
        using var crossCancel = await otherClient.PostAsync(
            $"/api/mail-requests/{ownerOnly.MailRequestId}/cancel", null, ct);
        using var crossReschedule = await otherClient.PostAsync(
            $"/api/mail-requests/{ownerOnly.MailRequestId}/reschedule",
            new StringContent("{\"scheduled_at\":null}", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.NotFound, crossGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossCancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossReschedule.StatusCode);
    }

    [Fact]
    public async Task Authentication_attempt_limiter_rejects_after_fixed_window_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var senders = scope.ServiceProvider.GetRequiredService<SenderRepository>();
        using var limiter = new ApiAuthenticationRateLimiter();

        for (var i = 0; i < ApiAuthenticationRateLimiter.PermitLimit; i++)
        {
            var context = CreateAuthenticationContext("192.0.2.10");
            var result = await ApiKeyRequestAuthorizer.AuthorizeAsync(context, senders, limiter, ct);
            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(result.Error).StatusCode);
        }

        var limitedContext = CreateAuthenticationContext("192.0.2.10");
        var limited = await ApiKeyRequestAuthorizer.AuthorizeAsync(limitedContext, senders, limiter, ct);
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(limited.Error).StatusCode);
    }

    [Fact]
    public void Canonical_payload_hash_excludes_all_v1_identity_fields()
    {
        const string first = """
            {"tenant_id":"00000000-0000-0000-0000-000000000001","source_service":"one","mail_request_id":"00000000-0000-0000-0000-000000000003","purpose":"notice","to":[{"email":"user@example.com"}],"subject":"subject","text_body":"body","payload_hash":"first"}
            """;
        const string second = """
            {"tenant_id":"00000000-0000-0000-0000-000000000002","source_service":"two","mail_request_id":"00000000-0000-0000-0000-000000000004","purpose":"notice","to":[{"email":"user@example.com"}],"subject":"subject","text_body":"body","payload_hash":"second"}
            """;

        Assert.Equal(
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(first),
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(second));
    }

    [Fact]
    public async Task Populated_v1_state_is_rejected_instead_of_inferred_as_sender_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO mail_suppressions (
                    id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
                VALUES (
                    @Id, @TenantId, 'legacy@example.com', 'manual', NULL,
                    '2026-01-01T00:00:00.0000000Z');
                """;
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@TenantId", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            V2IdentityMigration.Step.ValidatePreconditionBeforeScriptAsync(connection, ct));
        Assert.Contains("Unsupported major upgrade", exception.Message, StringComparison.Ordinal);
    }

    private async Task AssertUnauthorizedAsync(string token, CancellationToken cancellationToken)
    {
        using var client = CreateClient(token);
        using var response = await client.GetAsync($"/api/mail-requests/{Guid.NewGuid()}", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MailerErrorCodes.Unauthorized,
            await MailRequestTestData.ReadCodeAsync(response, cancellationToken));
    }

    private HttpClient CreateClient(string token)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static DefaultHttpContext CreateAuthenticationContext(string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Headers.Authorization = $"Bearer amk_{Guid.NewGuid():N}.{new string('A', 43)}";
        return context;
    }
}
