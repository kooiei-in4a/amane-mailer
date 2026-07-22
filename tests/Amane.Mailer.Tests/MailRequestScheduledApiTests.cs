using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amane.Mailer.Api;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestScheduledApiTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Post_with_future_scheduled_at_is_accepted_and_not_dispatchable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(2);
        var request = MailRequestTestData.CreateRequest(scheduledAt: scheduledAt);

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            request.MailRequestId,
            ct);
        Assert.NotNull(stored);
        Assert.Equal(MailRequestState.Queued, stored.Status);
        Assert.NotNull(stored.ScheduledAt);
        Assert.Null(stored.NextAttemptAt);
        Assert.False(MailRequestEndpoints.IsDispatchableQueued(stored, DateTimeOffset.UtcNow));

        using var get = await client.GetAsync(StatusUrl(request.MailRequestId), ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(MailRequestStatus.Queued, doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("scheduled_at", out var scheduled));
        Assert.False(string.IsNullOrWhiteSpace(scheduled.GetString()));
    }

    [Fact]
    public async Task Post_with_past_scheduled_at_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.ScheduledAtInPast,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Post_with_scheduled_at_beyond_max_horizon_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(
            scheduledAt: DateTimeOffset.UtcNow.Add(MailRequestScheduleLimits.MaxScheduledAhead).AddMinutes(1));

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.ScheduledAtTooFar,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Scheduled_at_does_not_affect_payload_hash_idempotency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        var first = MailRequestTestData.CreateRequest(
            mailRequestId: mailRequestId,
            scheduledAt: DateTimeOffset.UtcNow.AddHours(1));
        var second = MailRequestTestData.CreateRequest(
            mailRequestId: mailRequestId,
            scheduledAt: DateTimeOffset.UtcNow.AddHours(3));

        Assert.Equal(first.PayloadHash, second.PayloadHash);

        using var firstResponse = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(first),
            ct);
        using var secondResponse = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(second),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.AlreadyAccepted,
            await MailRequestTestData.ReadStatusAsync(secondResponse, ct));
    }

    [Fact]
    public async Task Cancel_queued_scheduled_request_returns_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddHours(4));

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var cancel = await client.PostAsync(CancelUrl(request.MailRequestId), content: null, ct);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(
            MailRequestStatus.Cancelled,
            await MailRequestTestData.ReadStatusAsync(cancel, ct));
    }

    [Fact]
    public async Task Cancel_already_cancelled_request_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddHours(4));

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var firstCancel = await client.PostAsync(CancelUrl(request.MailRequestId), content: null, ct);
        Assert.Equal(HttpStatusCode.OK, firstCancel.StatusCode);
        Assert.Equal(
            MailRequestStatus.Cancelled,
            await MailRequestTestData.ReadStatusAsync(firstCancel, ct));

        using var secondCancel = await client.PostAsync(CancelUrl(request.MailRequestId), content: null, ct);
        Assert.Equal(HttpStatusCode.OK, secondCancel.StatusCode);
        Assert.Equal(
            MailRequestStatus.Cancelled,
            await MailRequestTestData.ReadStatusAsync(secondCancel, ct));
    }

    [Fact]
    public async Task Cancel_delivered_request_returns_invalid_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        await MarkDeliveredAsync(request.MailRequestId, ct);

        using var cancel = await client.PostAsync(CancelUrl(request.MailRequestId), content: null, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancel.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidState,
            await MailRequestTestData.ReadCodeAsync(cancel, ct));
    }

    [Fact]
    public async Task Reschedule_updates_scheduled_at_without_touching_next_attempt_at()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddHours(2));

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var retryAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await SetNextAttemptAtAsync(request.MailRequestId, retryAt, ct);

        var newSchedule = DateTimeOffset.UtcNow.AddHours(6);
        using var reschedule = await client.PostAsync(
            RescheduleUrl(request.MailRequestId),
            JsonContent.Create(new MailRequestRescheduleRequest { ScheduledAt = newSchedule }, options: JsonOptions),
            ct);

        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);
        var body = await reschedule.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(MailRequestStatus.Queued, doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("scheduled_at", out var scheduledAt));
        Assert.NotNull(scheduledAt.GetString());
        Assert.True(doc.RootElement.TryGetProperty("next_attempt_at", out var nextAttemptAt));
        Assert.NotNull(nextAttemptAt.GetString());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            request.MailRequestId,
            ct);
        Assert.NotNull(stored);
        Assert.NotNull(stored.ScheduledAt);
        Assert.NotNull(stored.NextAttemptAt);
        Assert.True(Math.Abs((stored.ScheduledAt.Value - newSchedule.ToUniversalTime()).TotalSeconds) < 2);
        Assert.True(Math.Abs((stored.NextAttemptAt.Value - retryAt.ToUniversalTime()).TotalSeconds) < 2);
    }

    [Fact]
    public async Task Reschedule_to_null_clears_schedule_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(scheduledAt: DateTimeOffset.UtcNow.AddHours(5));

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var reschedule = await client.PostAsync(
            RescheduleUrl(request.MailRequestId),
            new StringContent("""{"scheduled_at":null}""", System.Text.Encoding.UTF8, "application/json"),
            ct);

        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            request.MailRequestId,
            ct);
        Assert.NotNull(stored);
        Assert.Null(stored.ScheduledAt);
        Assert.True(MailRequestEndpoints.IsDispatchableQueued(stored, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Reschedule_after_attempt_returns_invalid_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        await SetAttemptCountAsync(request.MailRequestId, attemptCount: 1, ct);

        using var reschedule = await client.PostAsync(
            RescheduleUrl(request.MailRequestId),
            JsonContent.Create(
                new MailRequestRescheduleRequest { ScheduledAt = DateTimeOffset.UtcNow.AddHours(3) },
                options: JsonOptions),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidState,
            await MailRequestTestData.ReadCodeAsync(reschedule, ct));
    }

    private async Task MarkDeliveredAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET status = @Status,
                delivered_at = @Now,
                completed_at = @Now,
                updated_at = @Now
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@Now", now);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetNextAttemptAtAsync(
        Guid mailRequestId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET next_attempt_at = @NextAttemptAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@NextAttemptAt", SqliteTime.ToStorageUtc(nextAttemptAt));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetAttemptCountAsync(
        Guid mailRequestId,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET attempt_count = @AttemptCount
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string StatusUrl(Guid mailRequestId) =>
        $"/internal/mail-requests/{mailRequestId:D}" +
        $"?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}" +
        $"&source_service={MailerWebApplicationFixtureBase.SourceService}";

    private static string CancelUrl(Guid mailRequestId) =>
        $"/internal/mail-requests/{mailRequestId:D}/cancel" +
        $"?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}" +
        $"&source_service={MailerWebApplicationFixtureBase.SourceService}";

    private static string RescheduleUrl(Guid mailRequestId) =>
        $"/internal/mail-requests/{mailRequestId:D}/reschedule" +
        $"?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}" +
        $"&source_service={MailerWebApplicationFixtureBase.SourceService}";

    private HttpClient CreateAuthorizedClient()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                MailerWebApplicationFixtureBase.Token);
        return client;
    }
}
