using System.Text.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// Suppression terminal-fail paths that need a configured tenant webhook so post-commit
/// enqueue actually attempts a <c>delivery_events</c> insert (#303 review).
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerSuppressionEnqueueTests(WebhookWorkerFixture fixture)
    : IClassFixture<WebhookWorkerFixture>, IAsyncLifetime
{
    private const string BlockEnqueueTrigger = "block_delivery_events_insert_for_suppression_metric";

    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        fixture.WebhookHandler.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await DropEnqueueBlockAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Suppressed_send_increments_metric_when_post_commit_enqueue_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSuppressionAsync("recipient@example.com", ct);
        await InstallEnqueueBlockAsync(ct);

        var metrics = fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>();
        metrics.ClearForTests();

        var request = await SeedQueuedRequestAsync(ct);

        var stored = await WaitUntilFailedAsync(request.MailRequestId, ct);
        Assert.Equal(MailRequestState.Failed, stored.Status);
        Assert.Equal("Recipient is on the suppression list.", stored.LastErrorMessage);
        Assert.Empty(fixture.DeliveryProvider.Sent);
        Assert.Equal(1, metrics.CaptureSnapshot().SuppressedSendsTotal);
        Assert.Equal(0, await CountDeliveryEventsAsync(stored.Id, ct));

        var attempt = await ReadSingleAttemptAsync(stored.Id, ct);
        Assert.Equal(MailDeliveryErrorCodes.RecipientSuppressed, attempt.ErrorCode);
        Assert.False(attempt.Retryable);
    }

    private async Task SeedSuppressionAsync(string recipientEmail, CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var suppressions = scope.ServiceProvider.GetRequiredService<MailSuppressionRepository>();
        var now = DateTimeOffset.UtcNow;
        Assert.True(await suppressions.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = MailerWebApplicationFixtureBase.TenantId,
                RecipientEmail = recipientEmail,
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = now,
            },
            cancellationToken));
    }

    private async Task<MailRequestCreateRequest> SeedQueuedRequestAsync(CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest();
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

        using (var signalScope = fixture.Factory.Services.CreateScope())
        {
            signalScope.ServiceProvider.GetRequiredService<IMailRequestQueue>().TrySignalWorkAvailable();
        }

        return request;
    }

    private async Task<MailRequestDispatchState> WaitUntilFailedAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        // Provider is never called on the suppress path; rely on ConditionWait fallback polling.
        return await ConditionWait.UntilAsync(
            async ct =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
                return await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, ct);
            },
            stored => stored.Status == MailRequestState.Failed && stored.AttemptCount >= 1,
            ConditionWait.DefaultTimeout,
            cancellationToken);
    }

    private async Task InstallEnqueueBlockAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
            CREATE TRIGGER IF NOT EXISTS {BlockEnqueueTrigger}
            BEFORE INSERT ON delivery_events
            BEGIN
                SELECT RAISE(ABORT, 'injected post-commit enqueue failure');
            END;
            """,
            cancellationToken);

    private async Task DropEnqueueBlockAsync(CancellationToken cancellationToken) =>
        await ExecuteAsync($"DROP TRIGGER IF EXISTS {BlockEnqueueTrigger};", cancellationToken);

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountDeliveryEventsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM delivery_events de
            INNER JOIN mail_requests mr ON mr.mail_request_id = de.mail_request_id
            WHERE mr.id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<(string? ErrorCode, bool Retryable)> ReadSingleAttemptAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT error_code, retryable
            FROM mail_attempts
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var errorCode = reader.IsDBNull(0) ? null : reader.GetString(0);
        var retryable = reader.GetInt32(1) == 1;
        Assert.False(await reader.ReadAsync(cancellationToken));
        return (errorCode, retryable);
    }
}
