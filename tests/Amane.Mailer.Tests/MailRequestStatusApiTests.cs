using System.Net;
using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestStatusApiTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");

    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Get_after_post_returns_queued_status()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        using var get = await client.GetAsync(StatusUrl(request.MailRequestId), ct);

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(request.MailRequestId, root.GetProperty("mail_request_id").GetGuid());
        Assert.Equal(MailRequestStatus.Queued, root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("attempt_count").GetInt32());
        Assert.True(root.TryGetProperty("accepted_at", out var acceptedAt));
        Assert.False(string.IsNullOrWhiteSpace(acceptedAt.GetString()));
        Assert.False(root.TryGetProperty("delivered_at", out _));
    }

    [Fact]
    public async Task Get_nonexistent_request_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();

        using var response = await client.GetAsync(StatusUrl(Guid.NewGuid()), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.NotFound,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_other_tenant_request_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(
            mailRequestId,
            OtherTenantId,
            MailerWebApplicationFixtureBase.SourceService,
            MailRequestState.Queued,
            ct);

        using var client = CreateAuthorizedClient();
        using var response = await client.GetAsync(StatusUrl(mailRequestId), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.NotFound,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_unauthorized_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(token: "wrong-token");

        using var response = await client.GetAsync(StatusUrl(Guid.NewGuid()), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.UnauthorizedTenant,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_unregistered_source_service_returns_403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var url = StatusUrl(Guid.NewGuid(), sourceService: "unknown-service");

        using var response = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.SourceServiceNotAllowed,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_invalid_mail_request_id_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var url = StatusUrl("not-a-uuid");

        using var response = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
        Assert.Equal(
            "mail_request_id must be a UUID.",
            await ReadMessageAsync(response, ct));
    }

    [Fact]
    public async Task Get_missing_tenant_id_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var url = StatusUrl(Guid.NewGuid(), includeTenantId: false);

        using var response = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_invalid_tenant_id_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var url = StatusUrl(
            Guid.NewGuid().ToString("D"),
            tenantId: "not-a-uuid");

        using var response = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_missing_source_service_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var url = StatusUrl(Guid.NewGuid(), includeSourceService: false);

        using var response = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Get_response_excludes_pii_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        using var get = await client.GetAsync(StatusUrl(request.MailRequestId), ct);

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var body = await get.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("recipient", out _));
        Assert.False(root.TryGetProperty("recipient_email", out _));
        Assert.False(root.TryGetProperty("subject", out _));
        Assert.False(root.TryGetProperty("html_body", out _));
        Assert.False(root.TryGetProperty("text_body", out _));
        Assert.False(root.TryGetProperty("payload_json", out _));
    }

    [Fact]
    public async Task Get_delivered_status_returns_expected_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = Guid.NewGuid();
        var deliveredAt = SqliteTime.UtcNow;
        await SeedMailRequestAsync(
            mailRequestId,
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            MailRequestState.Delivered,
            ct,
            attemptCount: 1,
            deliveredAt: deliveredAt);

        using var client = CreateAuthorizedClient();
        using var response = await client.GetAsync(StatusUrl(mailRequestId), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(MailRequestStatus.Delivered, root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("attempt_count").GetInt32());
        Assert.True(root.TryGetProperty("delivered_at", out _));
    }

    [Fact]
    public async Task Get_cancelled_status_returns_expected_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(
            mailRequestId,
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            MailRequestState.Cancelled,
            ct,
            attemptCount: 0);

        using var client = CreateAuthorizedClient();
        using var response = await client.GetAsync(StatusUrl(mailRequestId), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(MailRequestStatus.Cancelled, root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("attempt_count").GetInt32());
    }

    [Fact]
    public async Task Get_processing_status_returns_expected_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(
            mailRequestId,
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            MailRequestState.Processing,
            ct,
            attemptCount: 1);

        using var client = CreateAuthorizedClient();
        using var response = await client.GetAsync(StatusUrl(mailRequestId), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(MailRequestStatus.Processing, root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("attempt_count").GetInt32());
    }

    [Fact]
    public async Task Get_after_manual_retry_does_not_expose_superseded_prior_success_error_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var mailRequestId = Guid.NewGuid();
        var internalId = Guid.CreateVersion7(now);
        var tenantId = MailerWebApplicationFixtureBase.TenantId;
        var sourceService = MailerWebApplicationFixtureBase.SourceService;

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using (var insertRequest = connection.CreateCommand())
            {
                insertRequest.CommandText = """
                    INSERT INTO mail_requests (
                        id, tenant_id, source_service, mail_request_id, purpose,
                        payload_json, payload_hash, subject, recipient_email,
                        status, attempt_count, max_attempts,
                        accepted_at, created_at, updated_at, completed_at, failed_at)
                    VALUES (
                        @Id, @TenantId, @SourceService, @MailRequestId, 'test',
                        '{}', @PayloadHash, 'subject', 'user@example.com',
                        @Status, 3, 3,
                        @Now, @Now, @Now, @Now, @Now);
                    """;
                insertRequest.Parameters.AddWithValue("@Id", internalId.ToString("D"));
                insertRequest.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
                insertRequest.Parameters.AddWithValue("@SourceService", sourceService);
                insertRequest.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
                insertRequest.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
                insertRequest.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
                insertRequest.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
                await insertRequest.ExecuteNonQueryAsync(ct);
            }

            await using (var insertAttempt = connection.CreateCommand())
            {
                insertAttempt.CommandText = """
                    INSERT INTO mail_attempts (
                        request_id, attempt_number, provider, status,
                        provider_message_id, error_code, error_message, retryable,
                        lock_token, started_at, completed_at)
                    VALUES (
                        @RequestId, 3, 'mailpit', @DeliveredStatus,
                        'old-cycle-provider-msg', NULL, NULL, 0,
                        @LockToken, @StartedAt, @CompletedAt);
                    """;
                insertAttempt.Parameters.AddWithValue("@RequestId", internalId.ToString("D"));
                insertAttempt.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
                insertAttempt.Parameters.AddWithValue("@LockToken", Guid.CreateVersion7(now).ToString("D"));
                insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-2)));
                insertAttempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-1)));
                await insertAttempt.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var auditRepository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();
            var retry = await repository.TryManualRetryAsync(
                internalId,
                allowedTenantIds: null,
                now,
                auditRepository,
                new AdminAuditEvent
                {
                    EventType = AdminAuditLog.EventTypes.ManualRetryRequested,
                    Actor = "status-api-test-admin",
                    OccurredAt = now,
                    TargetType = AdminAuditLog.TargetTypes.MailRequest,
                    TargetId = internalId.ToString("D"),
                    Result = AdminAuditLog.Results.Success,
                },
                ct);
            Assert.Equal(ManualMailRequestMutationStatus.Succeeded, retry.Status);
        }

        using var client = CreateAuthorizedClient();
        using (var getAfterRetry = await client.GetAsync(StatusUrl(mailRequestId), ct))
        {
            Assert.Equal(HttpStatusCode.OK, getAfterRetry.StatusCode);
            var body = await getAfterRetry.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal(MailRequestStatus.Queued, root.GetProperty("status").GetString());
            Assert.False(root.TryGetProperty("last_error_code", out _));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var auditRepository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();
            var deliveryEvents = scope.ServiceProvider.GetRequiredService<DeliveryEventRepository>();

            var cancel = await repository.TryManualCancelAsync(
                internalId,
                allowedTenantIds: null,
                now.AddSeconds(1),
                auditRepository,
                new AdminAuditEvent
                {
                    EventType = AdminAuditLog.EventTypes.ManualCancelRequested,
                    Actor = "status-api-test-admin",
                    OccurredAt = now.AddSeconds(1),
                    TargetType = AdminAuditLog.TargetTypes.MailRequest,
                    TargetId = internalId.ToString("D"),
                    Result = AdminAuditLog.Results.Success,
                },
                ct);
            Assert.Equal(ManualMailRequestMutationStatus.Succeeded, cancel.Status);

            var webhookContext = await deliveryEvents.FindContextByInternalRequestIdAsync(internalId, ct);
            Assert.NotNull(webhookContext);
            Assert.NotEqual(
                MailRequestRepository.SupersededByManualRetryErrorCode,
                webhookContext!.LastErrorCode);
            Assert.Null(webhookContext.LastErrorCode);
        }

        using var getAfterCancel = await client.GetAsync(StatusUrl(mailRequestId), ct);
        Assert.Equal(HttpStatusCode.OK, getAfterCancel.StatusCode);
        var cancelBody = await getAfterCancel.Content.ReadAsStringAsync(ct);
        using var cancelDoc = JsonDocument.Parse(cancelBody);
        Assert.Equal(MailRequestStatus.Cancelled, cancelDoc.RootElement.GetProperty("status").GetString());
        Assert.False(cancelDoc.RootElement.TryGetProperty("last_error_code", out _));
    }

    private static string StatusUrl(
        Guid mailRequestId,
        Guid? tenantId = null,
        string? sourceService = null,
        bool includeTenantId = true,
        bool includeSourceService = true) =>
        StatusUrl(
            mailRequestId.ToString("D"),
            tenantId?.ToString("D"),
            sourceService,
            includeTenantId,
            includeSourceService);

    private static string StatusUrl(
        string mailRequestId,
        string? tenantId = null,
        string? sourceService = null,
        bool includeTenantId = true,
        bool includeSourceService = true)
    {
        var resolvedTenantId = tenantId ?? MailerWebApplicationFixtureBase.TenantId.ToString("D");
        var resolvedSourceService = sourceService ?? MailerWebApplicationFixtureBase.SourceService;

        var queryParts = new List<string>(2);
        if (includeTenantId)
        {
            queryParts.Add($"tenant_id={resolvedTenantId}");
        }

        if (includeSourceService)
        {
            queryParts.Add($"source_service={resolvedSourceService}");
        }

        var query = queryParts.Count == 0 ? string.Empty : "?" + string.Join("&", queryParts);
        return $"/internal/mail-requests/{mailRequestId}{query}";
    }

    private static async Task<string?> ReadMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
    }

    private async Task SeedMailRequestAsync(
        Guid mailRequestId,
        Guid tenantId,
        string sourceService,
        MailRequestState status,
        CancellationToken cancellationToken,
        int attemptCount = 0,
        DateTimeOffset? deliveredAt = null)
    {
        var internalId = Guid.NewGuid();
        var now = SqliteTime.UtcNow;
        var nowStorage = SqliteTime.ToStorageUtc(now);
        var deliveredAtStorage = deliveredAt is null
            ? (string?)null
            : SqliteTime.ToStorageUtc(deliveredAt.Value);
        var completedAt = status is MailRequestState.Delivered
            or MailRequestState.Failed
            or MailRequestState.DeadLettered
            or MailRequestState.Cancelled
            ? deliveredAtStorage ?? nowStorage
            : (string?)null;

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at, delivered_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, @AttemptCount, 5,
                @Now, @Now, @Now, @CompletedAt, @DeliveredAt);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@CompletedAt", (object?)completedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@DeliveredAt", (object?)deliveredAtStorage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private HttpClient CreateAuthorizedClient() =>
        CreateClient(MailerWebApplicationFixtureBase.Token);

    private HttpClient CreateClient(string token)
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
