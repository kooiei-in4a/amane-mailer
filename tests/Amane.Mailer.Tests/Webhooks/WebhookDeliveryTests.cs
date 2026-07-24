using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class WebhookDeliveryTests(WebhookWorkerFixture fixture)
    : IClassFixture<WebhookWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        fixture.WebhookHandler.Reset();
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Worker_enqueues_and_delivers_signed_webhook_on_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitUntilMailDeliveredAsync(request.MailRequestId, ct);

        var delivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(request.MailRequestId, delivery.Payload.MailRequestId);
        Assert.Equal(MailDeliveryEventType.Delivered, delivery.Payload.EventType);
        Assert.DoesNotContain("recipient", delivery.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", delivery.Body, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sha256=", delivery.Signature, StringComparison.Ordinal);
        Assert.Equal(delivery.Payload.EventId.ToString("D"), delivery.EventIdHeader);

        var expected = WebhookSignatureService.ComputeSignature(
            WebhookWorkerFixture.WebhookSecret,
            long.Parse(delivery.TimestampHeader, System.Globalization.CultureInfo.InvariantCulture),
            Encoding.UTF8.GetBytes(delivery.Body));
        Assert.Equal("sha256=" + expected, delivery.Signature);
    }

    [Fact]
    public async Task Webhook_delivery_dead_letters_after_retry_exhaustion()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.WebhookHandler.FailuresBeforeSuccess = int.MaxValue;

        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitUntilMailDeliveredAsync(request.MailRequestId, ct);
        await WaitUntilWebhookDeadLetteredAsync(request.MailRequestId, ct);

        Assert.True(fixture.WebhookHandler.AttemptCount >= 1);
    }

    [Fact]
    public async Task Worker_enqueues_failed_webhook_on_non_retryable_provider_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_CONNECT_FAILED",
            "transport failure",
            retryable: false));

        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitUntilMailStatusAsync(request.MailRequestId, MailRequestState.Failed, ct);

        var delivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(MailDeliveryEventType.Failed, delivery.Payload.EventType);
        Assert.Equal("SMTP_CONNECT_FAILED", delivery.Payload.LastErrorCode);
    }

    [Fact]
    public async Task Reconciliation_re_enqueues_missing_terminal_delivery_event()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitUntilMailDeliveredAsync(request.MailRequestId, ct);
        _ = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);

        await DeleteDeliveryEventForRequestAsync(request.MailRequestId, ct);
        fixture.WebhookHandler.Reset();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var enqueuer = scope.ServiceProvider.GetRequiredService<DeliveryEventEnqueuer>();
            await enqueuer.ReconcileMissingTerminalEventsAsync(batchSize: 10, ct);
        }

        var redelivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(request.MailRequestId, redelivery.Payload.MailRequestId);
    }

    [Fact]
    public async Task Retention_purge_allows_new_webhook_after_idempotency_key_reuse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var firstResponse = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        await WaitUntilMailDeliveredAsync(request.MailRequestId, ct);

        var firstDelivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        var firstEventId = firstDelivery.Payload.EventId;

        await AgeCompletedRequestAsync(request.MailRequestId, DateTimeOffset.UtcNow.AddDays(-120), ct);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var deleted = await repository.DeleteExpiredCompletedAsync(
                DateTimeOffset.UtcNow.AddDays(-90),
                batchSize: 100,
                ct);

            Assert.Equal(1, deleted);
        }

        Assert.False(await DeliveryEventExistsAsync(request.MailRequestId, ct));
        fixture.WebhookHandler.Reset();

        using var secondResponse = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        await WaitUntilMailDeliveredAsync(request.MailRequestId, ct);

        var secondDelivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(request.MailRequestId, secondDelivery.Payload.MailRequestId);
        Assert.Equal(MailDeliveryEventType.Delivered, secondDelivery.Payload.EventType);
        Assert.NotEqual(firstEventId, secondDelivery.Payload.EventId);
    }

    [Fact]
    public async Task Admin_cancel_enqueues_cancelled_webhook()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        var internalId = await SeedCancellableMailRequestAsync(request, ct);

        var adminClient = CreateAdminClient();
        await LoginAdminAsync(adminClient, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(adminClient, $"/admin/mail-requests/{internalId:D}", ct);

        using var response = await adminClient.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        await WaitUntilMailStatusAsync(request.MailRequestId, MailRequestState.Cancelled, ct);

        var delivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(request.MailRequestId, delivery.Payload.MailRequestId);
        Assert.Equal(MailDeliveryEventType.Cancelled, delivery.Payload.EventType);
    }

    [Fact]
    public async Task Admin_manual_retry_after_failed_does_not_enqueue_second_delivered_webhook()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_CONNECT_FAILED",
            "transport failure",
            retryable: false));

        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var createResponse = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        await WaitUntilMailStatusAsync(request.MailRequestId, MailRequestState.Failed, ct);

        var failedDelivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(MailDeliveryEventType.Failed, failedDelivery.Payload.EventType);
        var failedEventId = failedDelivery.Payload.EventId;
        var attemptsAfterFailedWebhook = fixture.WebhookHandler.AttemptCount;

        var internalId = await ReadInternalRequestIdAsync(request.MailRequestId, ct);
        var adminClient = CreateAdminClient();
        await LoginAdminAsync(adminClient, ct);
        var csrf = await ReadCsrfTokenFromAdminPageAsync(adminClient, $"/admin/mail-requests/{internalId:D}", ct);

        using var retryResponse = await adminClient.PostAsync(
            $"/admin/mail-requests/{internalId:D}/retry",
            CreateCsrfContent(csrf),
            ct);

        Assert.Equal(HttpStatusCode.SeeOther, retryResponse.StatusCode);
        await WaitUntilMailStatusAsync(request.MailRequestId, MailRequestState.Delivered, ct);

        // first-wins: later Delivered must not insert/replace the Failed outbox event (#273).
        await Task.Delay(1500, ct);
        Assert.Equal(attemptsAfterFailedWebhook, fixture.WebhookHandler.AttemptCount);
        Assert.Equal(1, await CountDeliveryEventsAsync(request.MailRequestId, ct));
        Assert.Equal(MailDeliveryEventType.Failed, await ReadDeliveryEventTypeAsync(request.MailRequestId, ct));
        Assert.Equal(failedEventId, await ReadDeliveryEventIdAsync(request.MailRequestId, ct));
    }

    [Fact]
    public async Task Reaper_dead_letters_expired_processing_and_enqueues_webhook()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = await SeedExpiredProcessingAtMaxAttemptsAsync(ct);

        await WaitUntilMailStatusAsync(
            request.MailRequestId,
            MailRequestState.DeadLettered,
            ct,
            maxAttempts: 150);

        var delivery = await fixture.WebhookHandler.WaitForSuccessfulDeliveryAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(request.MailRequestId, delivery.Payload.MailRequestId);
        Assert.Equal(MailDeliveryEventType.DeadLettered, delivery.Payload.EventType);
    }

    private async Task WaitUntilMailStatusAsync(
        Guid mailRequestId,
        MailRequestState expectedStatus,
        CancellationToken cancellationToken,
        int maxAttempts = 100)
    {
        // maxAttempts retained for call-site compatibility; timeout derives from prior 100ms * attempts budget.
        var timeout = TimeSpan.FromMilliseconds(maxAttempts * 100L);
        try
        {
            await ConditionWait.UntilAsync(
                async ct => await ReadMailStatusAsync(mailRequestId, ct) == expectedStatus,
                timeout,
                cancellationToken,
                wake: fixture.DeliveryProvider.Activity);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Mail request {mailRequestId:D} did not reach {expectedStatus} in time.");
        }
    }

    private async Task DeleteDeliveryEventForRequestAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM delivery_events WHERE mail_request_id = @MailRequestId;";
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AgeCompletedRequestAsync(
        Guid mailRequestId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                completed_at = @CompletedAt,
                delivered_at = @CompletedAt,
                updated_at = @CompletedAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> DeliveryEventExistsAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM delivery_events
                WHERE mail_request_id = @MailRequestId
            );
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    private async Task<int> CountDeliveryEventsAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM delivery_events
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? (int)value : 0;
    }

    private async Task<string> ReadDeliveryEventTypeAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type
            FROM delivery_events
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<string>(result);
    }

    private async Task<Guid> ReadDeliveryEventIdAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM delivery_events
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Guid.Parse(Assert.IsType<string>(result));
    }

    private async Task<Guid> ReadInternalRequestIdAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM mail_requests
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Guid.Parse(Assert.IsType<string>(result));
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", WebhookWorkerFixture.Token);
        return client;
    }

    private async Task WaitUntilMailDeliveredAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        await WaitUntilMailStatusAsync(mailRequestId, MailRequestState.Delivered, cancellationToken);
    }

    private async Task WaitUntilWebhookDeadLetteredAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        try
        {
            await ConditionWait.UntilAsync(
                async ct => await ReadWebhookDeliveryStatusAsync(mailRequestId, ct) == DeliveryEventState.DeadLettered,
                TimeSpan.FromSeconds(20),
                cancellationToken,
                wake: fixture.WebhookHandler.Activity);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Webhook for mail request {mailRequestId:D} was not dead-lettered in time.");
        }
    }

    private async Task<DeliveryEventState?> ReadWebhookDeliveryStatusAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM delivery_events
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long status ? (DeliveryEventState)status : null;
    }

    private async Task<MailRequestState> ReadMailStatusAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM mail_requests
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long status ? (MailRequestState)status : MailRequestState.Queued;
    }

    private async Task<Guid> SeedCancellableMailRequestAsync(
        MailRequestCreateRequest request,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;
        var internalId = Guid.CreateVersion7(now);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at, failed_at, last_error_message)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                @PayloadJson, @PayloadHash, @Subject, @HtmlBody, @TextBody, @ReplyTo,
                @RecipientEmail, @RecipientDisplayName, NULL,
                @Status, @AttemptCount, @MaxAttempts,
                @Now, @Now, @Now, @Now, @Now, @LastErrorMessage);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", request.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", request.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@Purpose", request.Purpose);
        command.Parameters.AddWithValue("@PayloadJson", body);
        command.Parameters.AddWithValue("@PayloadHash", request.PayloadHash);
        command.Parameters.AddWithValue("@Subject", request.Subject);
        command.Parameters.AddWithValue("@HtmlBody", (object?)request.HtmlBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@TextBody", (object?)request.TextBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@ReplyTo", (object?)request.ReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", request.To[0].Email);
        command.Parameters.AddWithValue("@RecipientDisplayName", (object?)request.To[0].DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@AttemptCount", 1);
        command.Parameters.AddWithValue("@MaxAttempts", 3);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@LastErrorMessage", "transport failure");
        await command.ExecuteNonQueryAsync(cancellationToken);

        return internalId;
    }

    private async Task<MailRequestCreateRequest> SeedExpiredProcessingAtMaxAttemptsAsync(
        CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;
        var expiredAt = now.AddMinutes(-1);
        var lockToken = Guid.CreateVersion7(now);
        var internalId = Guid.CreateVersion7(now);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts,
                lock_token, lock_expires_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                @PayloadJson, @PayloadHash, @Subject, @HtmlBody, @TextBody, @ReplyTo,
                @RecipientEmail, @RecipientDisplayName, NULL,
                @Status, @AttemptCount, @MaxAttempts,
                @LockToken, @LockExpiresAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", request.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", request.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@Purpose", request.Purpose);
        command.Parameters.AddWithValue("@PayloadJson", body);
        command.Parameters.AddWithValue("@PayloadHash", request.PayloadHash);
        command.Parameters.AddWithValue("@Subject", request.Subject);
        command.Parameters.AddWithValue("@HtmlBody", (object?)request.HtmlBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@TextBody", (object?)request.TextBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@ReplyTo", (object?)request.ReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", request.To[0].Email);
        command.Parameters.AddWithValue("@RecipientDisplayName", (object?)request.To[0].DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@AttemptCount", 3);
        command.Parameters.AddWithValue("@MaxAttempts", 3);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(expiredAt));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return request;
    }

    private HttpClient CreateAdminClient() =>
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAdminAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenFromLoginAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, WebhookWorkerFixture.AdminUsername, WebhookWorkerFixture.AdminPassword),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenFromLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<string> ReadCsrfTokenFromAdminPageAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCsrfTokenFromHtmlAsync(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<string> ReadCsrfTokenFromHtmlAsync(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Admin page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Admin page CSRF token value was empty.");
        return Task.FromResult(html[start..end]);
    }

    private static FormUrlEncodedContent CreateCsrfContent(string csrfToken) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        });

    private static FormUrlEncodedContent CreateLoginContent(string csrfToken, string username, string password) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });
}

public sealed class WebhookWorkerFixture() : MailerWebApplicationFixtureBase(workerEnabled: true)
{
    public const string WebhookSecret = "test-webhook-secret";
    public const string AdminUsername = "admin";
    public const string AdminPassword = "correct horse battery staple";
    private static readonly string AdminPasswordHash = AdminPasswordHasher.Hash(AdminPassword);

    public CapturingMailDeliveryProvider DeliveryProvider { get; } = new();
    public RecordingWebhookHandler WebhookHandler { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
            ["Mailer:Sweep:IntervalSeconds"] = "1",
            ["Mailer:Webhook:MaxAttempts"] = "3",
            ["Mailer:Webhook:InitialDelaySeconds"] = "1",
            ["Mailer:Webhook:MaxDelaySeconds"] = "2",
            ["Mailer:Webhook:DeliveryTimeoutSeconds"] = "2",
            ["Mailer:Webhook:LeaseDurationSeconds"] = "20",
            ["TEST_WEBHOOK_SECRET"] = WebhookSecret,
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = AdminUsername,
            ["AMANE_ADMIN_PASSWORD_HASH"] = AdminPasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.RemoveAll<IMailDeliveryProvider>();
        services.AddSingleton<IMailDeliveryProvider>(DeliveryProvider);
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(WebhookHandler));
        services.AddSingleton<IStartupFilter>(new AdminLocalAddressStartupFilter(IPAddress.Loopback));
    }

    protected override string BuildTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{TenantId}}",
              "name": "example-develop",
              "source_services": ["{{SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              },
              "webhook": {
                "url": "https://93.184.216.34/internal/mailer/webhooks",
                "secret_env": "TEST_WEBHOOK_SECRET"
              }
            }
          ]
        }
        """;
}

internal sealed class AdminLocalAddressStartupFilter(IPAddress localAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.LocalIpAddress ??= localAddress;
                await nextMiddleware();
            });

            next(app);
        };
}

public sealed class RecordingWebhookHandler
{
    private readonly AsyncPulse _activity = new();
    private TaskCompletionSource<RecordedWebhookDelivery> _deliveryTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int AttemptCount { get; private set; }

    public int FailuresBeforeSuccess { get; set; }

    /// <summary>
    /// Pulsed on every webhook HTTP attempt (success or failure).
    /// </summary>
    internal AsyncPulse Activity => _activity;

    public void Reset()
    {
        AttemptCount = 0;
        FailuresBeforeSuccess = 0;
        _deliveryTcs = new TaskCompletionSource<RecordedWebhookDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activity.Pulse();
    }

    public Task<RecordedWebhookDelivery> WaitForSuccessfulDeliveryAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        return _deliveryTcs.Task.WaitAsync(linked.Token);
    }

    public HttpResponseMessage Handle(HttpRequestMessage request)
    {
        AttemptCount++;
        _activity.Pulse();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

        var payload = JsonSerializer.Deserialize(body, MailerContractsJsonContext.Default.MailDeliveryEventPayload)
            ?? throw new InvalidOperationException("Webhook body was not valid JSON.");

        if (AttemptCount <= FailuresBeforeSuccess)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        var delivery = new RecordedWebhookDelivery(
            body,
            payload,
            request.Headers.GetValues(WebhookSignatureService.EventIdHeaderName).Single(),
            request.Headers.GetValues(WebhookSignatureService.TimestampHeaderName).Single(),
            request.Headers.GetValues(WebhookSignatureService.SignatureHeaderName).Single());

        _deliveryTcs.TrySetResult(delivery);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

public sealed record RecordedWebhookDelivery(
    string Body,
    MailDeliveryEventPayload Payload,
    string EventIdHeader,
    string TimestampHeader,
    string Signature);

public sealed class StubHttpClientFactory(RecordingWebhookHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(new StubWebhookMessageHandler(handler))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

public sealed class StubWebhookMessageHandler(RecordingWebhookHandler handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(handler.Handle(request));
}
