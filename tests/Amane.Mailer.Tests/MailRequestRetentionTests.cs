using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Mailer.Contracts.MailRequests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestRetentionTests(MailerRetentionFixture fixture)
    : IClassFixture<MailerRetentionFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task T16_retention_purges_expired_completed_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        await SeedDeliveredRequestAsync(request, completedAt: DateTimeOffset.UtcNow.AddDays(-120), ct);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var state = await repository.FindDispatchStateByMailRequestIdAsync(request.MailRequestId, ct);
            if (state is null)
            {
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException("Expired completed mail request was not purged by retention service.");
    }

    [Fact]
    public async Task DeleteExpiredCompletedAsync_removes_only_terminal_rows_past_cutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var expiredRequest = MailRequestTestData.CreateRequest();
        var recentRequest = MailRequestTestData.CreateRequest();

        await SeedDeliveredRequestAsync(expiredRequest, DateTimeOffset.UtcNow.AddDays(-120), ct);
        await SeedDeliveredRequestAsync(recentRequest, DateTimeOffset.UtcNow.AddDays(-1), ct);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var deleted = await repository.DeleteExpiredCompletedAsync(
            DateTimeOffset.UtcNow.AddDays(-90),
            batchSize: 100,
            ct);

        Assert.Equal(1, deleted);

        var expiredState = await repository.FindDispatchStateByMailRequestIdAsync(expiredRequest.MailRequestId, ct);
        var recentState = await repository.FindDispatchStateByMailRequestIdAsync(recentRequest.MailRequestId, ct);
        Assert.Null(expiredState);
        Assert.NotNull(recentState);
    }

    private async Task SeedDeliveredRequestAsync(
        MailRequestCreateRequest request,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(request);
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

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = @Status,
                attempt_count = 1,
                completed_at = @CompletedAt,
                delivered_at = @CompletedAt,
                updated_at = @CompletedAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
