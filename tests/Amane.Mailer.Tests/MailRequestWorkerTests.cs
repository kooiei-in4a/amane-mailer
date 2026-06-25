using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Worker_delivers_queued_request_with_provider_stub()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 1, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
        Assert.Equal("recipient@example.com", sent.To);
        Assert.Equal("mailpit", sent.Provider);
        Assert.Equal(1, await CountAttemptsAsync(stored.Id, ct));
    }

    [Fact]
    public async Task Worker_recovers_stale_processing_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        await SeedExpiredProcessingRequestAsync(request, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 2, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
        Assert.Equal(2, stored.AttemptCount);
    }

    [Fact]
    public async Task Worker_caps_retry_backoff_delay()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_TEMPORARY",
            "temporary failure",
            retryable: true));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Queued, minAttemptCount: 1, ct);

        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.NextAttemptAt);
        var delay = stored.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(delay.TotalSeconds, 0.5, 2.5);
    }

    [Fact]
    public async Task Worker_dead_letters_after_max_retry_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.QueueResult(MailDeliveryResult.Failure(
            "SMTP_TEMPORARY",
            "temporary failure",
            retryable: true));
        var request = await SeedQueuedRequestAsync(attemptCount: 2, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeadLettered, minAttemptCount: 3, ct);

        Assert.Equal(3, stored.AttemptCount);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(1, await CountAttemptsAsync(stored.Id, ct));
    }

    [Fact]
    public async Task Worker_dead_letters_expired_processing_request_at_max_attempts_without_resending()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.HoldNextSendIgnoringCancellation();
        var request = await SeedQueuedRequestAsync(attemptCount: 2, ct);

        var processing = await WaitUntilStatusAsync(
            request.MailRequestId,
            MailRequestState.Processing,
            minAttemptCount: 3,
            ct);

        await ExpireProcessingLeaseAsync(processing.Id, ct);
        fixture.DeliveryProvider.ReleaseHeldSend();
        SignalWorker();

        var deadLettered = await WaitUntilStatusAsync(
            request.MailRequestId,
            MailRequestState.DeadLettered,
            minAttemptCount: 3,
            ct);
        await WaitUntilSentCountAsync(expectedCount: 1, ct);

        Assert.Equal(3, deadLettered.AttemptCount);
        Assert.NotNull(deadLettered.CompletedAt);
        Assert.Null(deadLettered.LockToken);
        Assert.Contains("max_attempts", deadLettered.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(fixture.DeliveryProvider.Sent);

        var terminalColumns = await ReadTerminalColumnsAsync(deadLettered.Id, ct);
        Assert.NotNull(terminalColumns.CompletedAt);
        Assert.NotNull(terminalColumns.FailedAt);
        Assert.Null(terminalColumns.LockToken);
        Assert.Null(terminalColumns.LockExpiresAt);

        var attempt = await ReadSingleAttemptAsync(deadLettered.Id, ct);
        Assert.Equal(3, attempt.AttemptNumber);
        Assert.Equal("lease-reaper", attempt.Provider);
        Assert.Equal(MailRequestState.DeadLettered, attempt.Status);
        Assert.Equal("PROCESSING_LEASE_EXPIRED_MAX_ATTEMPTS", attempt.ErrorCode);
        Assert.True(attempt.Retryable);
    }

    [Fact]
    public async Task Worker_marks_request_failed_when_tenant_is_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = await SeedQueuedRequestAsync(
            attemptCount: 0,
            ct,
            tenantId: Guid.Parse("00000000-0000-0000-0000-00000000ffff"));

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Failed, minAttemptCount: 1, ct);

        Assert.Equal("Tenant is not configured.", stored.LastErrorMessage);
    }

    [Fact]
    public async Task Worker_skips_finalize_when_lock_token_is_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.HoldNextSend();
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var processing = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Processing, minAttemptCount: 1, ct);
        Assert.NotNull(processing.LockToken);
        var staleToken = processing.LockToken!.Value;

        await ExpireProcessingLeaseAsync(processing.Id, ct);
        SignalWorker();

        await WaitUntilAttemptCountAsync(request.MailRequestId, minAttemptCount: 2, ct);
        fixture.DeliveryProvider.ReleaseHeldSend();

        var delivered = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 2, ct);
        Assert.Equal(MailRequestState.Delivered, delivered.Status);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var fenced = await repository.FinalizeAsync(
            delivered.Id,
            staleToken,
            DateTimeOffset.UtcNow,
            MailRequestFinalizeOutcome.Delivered,
            nextAttemptAt: null,
            lastErrorMessage: null,
            new MailAttemptInsert
            {
                RequestId = delivered.Id,
                AttemptNumber = 99,
                Provider = "mailpit",
                Status = MailRequestState.Delivered,
                LockToken = staleToken,
                Retryable = false,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
            },
            ct);
        Assert.False(fenced);

        var afterStaleFinalize = await FindDispatchStateAsync(request.MailRequestId, ct);
        Assert.NotNull(afterStaleFinalize);
        Assert.Equal(MailRequestState.Delivered, afterStaleFinalize!.Status);
        Assert.Equal(2, afterStaleFinalize.AttemptCount);
    }

    [Fact]
    public async Task Worker_schedules_retry_when_send_times_out()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.DeliveryProvider.SetSendDelay(TimeSpan.FromSeconds(3));
        var request = await SeedQueuedRequestAsync(attemptCount: 0, ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Queued, minAttemptCount: 1, ct);

        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.NextAttemptAt);
        Assert.Contains("exceeded", stored.LastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MailRequestDispatchState> WaitUntilStatusAsync(
        Guid mailRequestId,
        MailRequestState status,
        int minAttemptCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        MailRequestDispatchState? lastStored = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var stored = await FindDispatchStateAsync(mailRequestId, cancellationToken);
            lastStored = stored;
            if (stored?.Status == status && stored.AttemptCount >= minAttemptCount)
            {
                return stored;
            }

            await Task.Delay(100, cancellationToken);
        }

        var lastState = lastStored is null
            ? "not found"
            : $"{lastStored.Status} attempt_count={lastStored.AttemptCount} lock_token={(lastStored.LockToken is null ? "null" : "present")}";
        throw new TimeoutException(
            $"Mail request did not reach status '{status}' with attempt_count >= {minAttemptCount}. Last state: {lastState}.");
    }

    private async Task WaitUntilAttemptCountAsync(
        Guid mailRequestId,
        int minAttemptCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var stored = await FindDispatchStateAsync(mailRequestId, cancellationToken);
            if (stored?.AttemptCount >= minAttemptCount)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Mail request did not reach attempt_count >= {minAttemptCount}.");
    }

    private async Task WaitUntilSentCountAsync(
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (fixture.DeliveryProvider.Sent.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Mailer provider did not record {expectedCount} sent message(s).");
    }

    private async Task<MailRequestDispatchState?> FindDispatchStateAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);
    }

    private async Task<int> CountAttemptsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.CountAttemptsAsync(requestId, cancellationToken);
    }

    private async Task<MailRequestTerminalColumns> ReadTerminalColumnsAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT completed_at, failed_at, lock_token, lock_expires_at
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));

        return new MailRequestTerminalColumns(
            reader.IsDBNull(0) ? null : SqliteTime.FromStorage(reader.GetString(0)),
            reader.IsDBNull(1) ? null : SqliteTime.FromStorage(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)));
    }

    private async Task<MailAttemptRecord> ReadSingleAttemptAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_number, provider, status, error_code, error_message, retryable
            FROM mail_attempts
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));

        var attempt = new MailAttemptRecord(
            reader.GetInt32(0),
            reader.GetString(1),
            (MailRequestState)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5) == 1);

        Assert.False(await reader.ReadAsync(cancellationToken));
        return attempt;
    }

    private async Task<MailRequestCreateRequest> SeedQueuedRequestAsync(
        int attemptCount,
        CancellationToken cancellationToken,
        Guid? tenantId = null)
    {
        var request = MailRequestTestData.CreateRequest();
        if (tenantId is not null)
        {
            request = request with { TenantId = tenantId.Value };
        }

        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = request.TenantId,
                SourceService = request.SourceService,
                MailRequestId = request.MailRequestId,
                Purpose = request.Purpose,
                PayloadJson = body,
                PayloadHash = request.PayloadHash,
                Subject = request.Subject,
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody,
                ReplyTo = request.ReplyTo,
                RecipientEmail = request.To[0].Email,
                RecipientDisplayName = request.To[0].DisplayName,
                MaxAttempts = 3,
                AcceptedAt = now,
            },
            cancellationToken);

        if (attemptCount > 0)
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
            command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        SignalWorker();
        return request;
    }

    private async Task SeedExpiredProcessingRequestAsync(
        MailRequestCreateRequest request,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = request.TenantId,
                SourceService = request.SourceService,
                MailRequestId = request.MailRequestId,
                Purpose = request.Purpose,
                PayloadJson = body,
                PayloadHash = request.PayloadHash,
                Subject = request.Subject,
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody,
                ReplyTo = request.ReplyTo,
                RecipientEmail = request.To[0].Email,
                RecipientDisplayName = request.To[0].DisplayName,
                MaxAttempts = 3,
                AcceptedAt = now,
            },
            cancellationToken);

        var lockToken = Guid.CreateVersion7(now);
        var expiredAt = now.AddMinutes(-1);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = 1,
                attempt_count = 1,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @UpdatedAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        SignalWorker();
    }

    private async Task ExpireProcessingLeaseAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET lock_expires_at = @LockExpiresAt
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(expiredAt));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void SignalWorker()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<Amane.Mailer.Queue.IMailRequestQueue>();
        queue.TrySignalWorkAvailable();
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", MailerWebApplicationFixtureBase.Token);
        return client;
    }

    private sealed record MailRequestTerminalColumns(
        DateTimeOffset? CompletedAt,
        DateTimeOffset? FailedAt,
        Guid? LockToken,
        DateTimeOffset? LockExpiresAt);

    private sealed record MailAttemptRecord(
        int AttemptNumber,
        string Provider,
        MailRequestState Status,
        string? ErrorCode,
        string? ErrorMessage,
        bool Retryable);
}
