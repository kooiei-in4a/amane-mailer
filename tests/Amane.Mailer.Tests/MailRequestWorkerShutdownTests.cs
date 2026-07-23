using System.Text.Json;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression for #271: with BatchClaimSize &gt; MaxSendConcurrency, shutdown must
/// cancel semaphore-waiting later waves so they do not start new provider sends.
/// </summary>
public sealed class MailRequestWorkerShutdownTests
{
    [Fact]
    public async Task Worker_does_not_start_later_send_wave_after_shutdown()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-worker-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        var deliveryProvider = new CapturingMailDeliveryProvider();

        await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, ct);
        await ApplyMigrationsAsync(connectionString, ct);

        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            factory = new WebApplicationFactory<global::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Mailer"] = connectionString,
                            ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                            ["Mailer:Worker:Enabled"] = "True",
                            ["Mailer:Worker:BatchClaimSize"] = "2",
                            ["Mailer:Worker:MaxSendConcurrency"] = "1",
                            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
                            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        }));
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IMailDeliveryProvider>();
                        services.AddSingleton<IMailDeliveryProvider>(deliveryProvider);
                    });
                });

            _ = factory.CreateClient();
            deliveryProvider.HoldNextSendIgnoringCancellation();

            var first = await SeedQueuedRequestAsync(factory, ct);
            var second = await SeedQueuedRequestAsync(factory, ct);
            SignalWorker(factory);

            await WaitUntilStatusAsync(connectionString, first.MailRequestId, MailRequestState.Processing, ct);
            await WaitUntilStatusAsync(connectionString, second.MailRequestId, MailRequestState.Processing, ct);
            Assert.Empty(deliveryProvider.Sent);

            // Cancel stoppingToken while the first wave is held and the second waits
            // on the send semaphore; then release so the in-flight send can finish.
            var disposeTask = factory.DisposeAsync().AsTask();
            factory = null;
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            deliveryProvider.ReleaseHeldSend();
            await disposeTask;

            Assert.Single(deliveryProvider.Sent);
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
            }
        }
    }

    private static async Task ApplyMigrationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = connectionString,
            })
            .Build();
        var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));
        await runner.ApplyPendingAsync(cancellationToken);
    }

    private static async Task<MailRequestCreateRequest> SeedQueuedRequestAsync(
        WebApplicationFactory<global::Program> factory,
        CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;

        await using var scope = factory.Services.CreateAsyncScope();
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

    private static void SignalWorker(WebApplicationFactory<global::Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMailRequestQueue>();
        queue.TrySignalWorkAvailable();
    }

    private static async Task WaitUntilStatusAsync(
        string connectionString,
        Guid mailRequestId,
        MailRequestState status,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        MailRequestState? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status
                FROM mail_requests
                WHERE mail_request_id = @MailRequestId;
                """;
            command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is long statusValue)
            {
                last = (MailRequestState)(int)statusValue;
                if (last == status)
                {
                    return;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for {mailRequestId} to reach {status}. Last={last}.");
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
