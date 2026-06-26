using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Worker;
using Amane.Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestSweepServiceTests(MailerSweepFixture fixture)
    : IClassFixture<MailerSweepFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Sweep_service_is_registered_with_worker()
    {
        var hosted = fixture.Factory.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, service => service is MailRequestSweepService);
        Assert.Contains(hosted, service => service is MailRequestWorker);
    }

    [Fact]
    public async Task Sweep_signals_worker_to_deliver_queued_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = await SeedQueuedRequestWithoutSignalAsync(ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.Delivered, minAttemptCount: 1, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(request.MailRequestId, sent.MailRequestId);
        Assert.Equal(MailRequestState.Delivered, stored.Status);
    }

    [Fact]
    public async Task Sweep_dead_letters_expired_processing_request_at_max_attempts_without_worker_signal()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = await SeedExpiredProcessingRequestAtMaxAttemptsWithoutSignalAsync(ct);

        var stored = await WaitUntilStatusAsync(request.MailRequestId, MailRequestState.DeadLettered, minAttemptCount: 3, ct);

        Assert.Empty(fixture.DeliveryProvider.Sent);
        Assert.Equal(3, stored.AttemptCount);
        Assert.NotNull(stored.CompletedAt);
        Assert.Null(stored.LockToken);
        Assert.Contains("max_attempts", stored.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, await CountAttemptsAsync(stored.Id, ct));
    }

    [Fact]
    public async Task Sweep_recovers_after_database_query_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-sweep-unavailable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var badDatabasePath = Path.Combine(root, "blocked");
        Directory.CreateDirectory(badDatabasePath);

        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, ct);

        WebApplicationFactory<global::Program>? unavailableFactory = null;

        try
        {
            unavailableFactory = new WebApplicationFactory<global::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Mailer"] = $"Data Source={badDatabasePath}",
                            ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                            ["Mailer:Worker:Enabled"] = "True",
                            ["Mailer:Sweep:IntervalSeconds"] = "1",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        }));
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IMailDeliveryProvider>();
                        services.AddSingleton<IMailDeliveryProvider>(fixture.DeliveryProvider);
                    });
                });

            _ = unavailableFactory.CreateClient();

            await Task.Delay(TimeSpan.FromSeconds(2.5), ct);

            var hosted = unavailableFactory.Services.GetServices<IHostedService>().ToList();
            Assert.Contains(hosted, service => service is MailRequestSweepService);
        }
        finally
        {
            if (unavailableFactory is not null)
            {
                await unavailableFactory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
        }
    }

    private async Task<MailRequestCreateRequest> SeedQueuedRequestWithoutSignalAsync(CancellationToken cancellationToken)
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

        return request;
    }

    private async Task<MailRequestCreateRequest> SeedExpiredProcessingRequestAtMaxAttemptsWithoutSignalAsync(
        CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;
        var expiredAt = now.AddMinutes(-1);
        var lockToken = Guid.CreateVersion7(now);
        var id = Guid.CreateVersion7(now);

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
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
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

    private async Task<MailRequestDispatchState> WaitUntilStatusAsync(
        Guid mailRequestId,
        MailRequestState status,
        int minAttemptCount,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            var stored = await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);
            if (stored?.Status == status && stored.AttemptCount >= minAttemptCount)
            {
                return stored;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Mail request did not reach status '{status}' with attempt_count >= {minAttemptCount}.");
    }

    private async Task<int> CountAttemptsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.CountAttemptsAsync(requestId, cancellationToken);
    }

    private static string TenantConfigJson =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "name": "example-develop",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
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
              }
            }
          ]
        }
        """;
}
