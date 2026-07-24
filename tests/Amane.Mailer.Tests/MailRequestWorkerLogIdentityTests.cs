using System.Text.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

/// <summary>
/// Coverage for #285: Worker / reaper request logs must include non-PII identity
/// fields so the same <c>mail_request_id</c> can be distinguished across tenants.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerLogIdentityTests
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");

    [Fact]
    public async Task Worker_terminal_failure_logs_distinguish_same_mail_request_id_across_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-worker-log-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        var deliveryProvider = new CapturingMailDeliveryProvider();
        var logCapture = new CapturingLoggerProvider();
        var sharedMailRequestId = Guid.CreateVersion7();

        await File.WriteAllTextAsync(tenantConfigPath, TwoTenantConfigJson, ct);
        await ApplyMigrationsAsync(connectionString, ct);

        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            factory = CreateFactory(
                connectionString,
                tenantConfigPath,
                deliveryProvider,
                logCapture,
                workerEnabled: true);
            _ = factory.CreateClient();

            deliveryProvider.QueueResult(MailDeliveryResult.Failure(
                "SMTP_CONNECT_FAILED",
                "SMTP connect failed",
                retryable: false));
            deliveryProvider.QueueResult(MailDeliveryResult.Failure(
                "SMTP_CONNECT_FAILED",
                "SMTP connect failed",
                retryable: false));

            var first = await SeedQueuedRequestAsync(
                factory,
                MailerWebApplicationFixtureBase.TenantId,
                sharedMailRequestId,
                ct);
            var second = await SeedQueuedRequestAsync(
                factory,
                OtherTenantId,
                sharedMailRequestId,
                ct);

            SignalWorker(factory);
            await WaitUntilStatusAsync(
                connectionString,
                MailerWebApplicationFixtureBase.TenantId,
                sharedMailRequestId,
                MailRequestState.Failed,
                ct);
            await WaitUntilStatusAsync(
                connectionString,
                OtherTenantId,
                sharedMailRequestId,
                MailRequestState.Failed,
                ct);

            var firstId = await ReadInternalRequestIdAsync(
                connectionString,
                MailerWebApplicationFixtureBase.TenantId,
                sharedMailRequestId,
                ct);
            var secondId = await ReadInternalRequestIdAsync(
                connectionString,
                OtherTenantId,
                sharedMailRequestId,
                ct);

            var failureEntries = logCapture.Snapshot()
                .Where(static entry =>
                    entry.FormattedMessage.Contains("failed terminally", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, failureEntries.Count);

            AssertContainsIdentity(failureEntries, sharedMailRequestId, firstId, MailerWebApplicationFixtureBase.TenantId);
            AssertContainsIdentity(failureEntries, sharedMailRequestId, secondId, OtherTenantId);
            Assert.DoesNotContain(first.Subject, logCapture.JoinedOutput(), StringComparison.Ordinal);
            Assert.DoesNotContain(first.To[0].Email, logCapture.JoinedOutput(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupTempRootAsync(factory, root);
        }
    }

    [Fact]
    public async Task Reaper_dead_letter_logs_include_request_and_tenant_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-reaper-log-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        var deliveryProvider = new CapturingMailDeliveryProvider();
        var logCapture = new CapturingLoggerProvider();
        var sharedMailRequestId = Guid.CreateVersion7();

        await File.WriteAllTextAsync(tenantConfigPath, TwoTenantConfigJson, ct);
        await ApplyMigrationsAsync(connectionString, ct);

        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            factory = CreateFactory(
                connectionString,
                tenantConfigPath,
                deliveryProvider,
                logCapture,
                workerEnabled: false);
            _ = factory.CreateClient();

            var firstId = await SeedExpiredProcessingAtMaxAttemptsAsync(
                connectionString,
                MailerWebApplicationFixtureBase.TenantId,
                sharedMailRequestId,
                ct);
            var secondId = await SeedExpiredProcessingAtMaxAttemptsAsync(
                connectionString,
                OtherTenantId,
                sharedMailRequestId,
                ct);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var reaper = scope.ServiceProvider.GetRequiredService<ExpiredProcessingReaper>();
                await reaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(DateTimeOffset.UtcNow, ct);
            }

            await WaitUntilStatusAsync(
                connectionString,
                MailerWebApplicationFixtureBase.TenantId,
                sharedMailRequestId,
                MailRequestState.DeadLettered,
                ct);
            await WaitUntilStatusAsync(
                connectionString,
                OtherTenantId,
                sharedMailRequestId,
                MailRequestState.DeadLettered,
                ct);

            var deadLetterEntries = logCapture.Snapshot()
                .Where(static entry =>
                    entry.Category.Contains(nameof(ExpiredProcessingReaper), StringComparison.Ordinal) &&
                    entry.FormattedMessage.Contains("processing lease expired", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, deadLetterEntries.Count);

            AssertContainsIdentity(deadLetterEntries, sharedMailRequestId, firstId, MailerWebApplicationFixtureBase.TenantId);
            AssertContainsIdentity(deadLetterEntries, sharedMailRequestId, secondId, OtherTenantId);
        }
        finally
        {
            await CleanupTempRootAsync(factory, root);
        }
    }

    private static async Task CleanupTempRootAsync(
        WebApplicationFactory<global::Program>? factory,
        string root)
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(50);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; Windows can retain a SQLite lock briefly.
                return;
            }
        }
    }

    private static void AssertContainsIdentity(
        IReadOnlyList<CapturedLogEntry> entries,
        Guid mailRequestId,
        Guid requestId,
        Guid tenantId)
    {
        var match = Assert.Single(
            entries,
            entry =>
                entry.State.TryGetValue("RequestId", out var loggedRequestId) &&
                entry.State.TryGetValue("TenantId", out var loggedTenantId) &&
                string.Equals(loggedRequestId, requestId.ToString("D"), StringComparison.Ordinal) &&
                string.Equals(loggedTenantId, tenantId.ToString("D"), StringComparison.Ordinal));

        Assert.True(match.State.TryGetValue("MailRequestId", out var loggedMailRequestId));
        Assert.Equal(mailRequestId.ToString("D"), loggedMailRequestId);
    }

    private static WebApplicationFactory<global::Program> CreateFactory(
        string connectionString,
        string tenantConfigPath,
        CapturingMailDeliveryProvider deliveryProvider,
        CapturingLoggerProvider logCapture,
        bool workerEnabled) =>
        new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["Mailer:Worker:Enabled"] = workerEnabled ? "True" : "False",
                        ["Mailer:Worker:SendTimeoutSeconds"] = "2",
                        ["Mailer:Worker:LeaseDurationSeconds"] = "30",
                        ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        ["MAIL_SERVICE_TOKEN_2"] = "test-mail-service-token-2",
                    }));
                builder.ConfigureLogging(logging => logging.AddProvider(logCapture));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IMailDeliveryProvider>();
                    services.AddSingleton<IMailDeliveryProvider>(deliveryProvider);
                });
            });

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
        Guid tenantId,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest() with
        {
            TenantId = tenantId,
            MailRequestId = mailRequestId,
        };
        request = request with
        {
            PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
        };

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

    private static async Task<Guid> SeedExpiredProcessingAtMaxAttemptsAsync(
        string connectionString,
        Guid tenantId,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        var request = MailRequestTestData.CreateRequest() with
        {
            TenantId = tenantId,
            MailRequestId = mailRequestId,
        };
        request = request with
        {
            PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
        };

        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;
        var expiredAt = now.AddMinutes(-1);
        var lockToken = Guid.CreateVersion7(now);
        var id = Guid.CreateVersion7(now);

        await using var connection = new SqliteConnection(connectionString);
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
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", request.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
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
        return id;
    }

    private static void SignalWorker(WebApplicationFactory<global::Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IMailRequestQueue>();
        queue.TrySignalWorkAvailable();
    }

    private static async Task WaitUntilStatusAsync(
        string connectionString,
        Guid tenantId,
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
                WHERE tenant_id = @TenantId
                  AND mail_request_id = @MailRequestId;
                """;
            command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
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

        Assert.Fail($"Timed out waiting for {tenantId}/{mailRequestId} to reach {status}. Last={last}.");
    }

    private static async Task<Guid> ReadInternalRequestIdAsync(
        string connectionString,
        Guid tenantId,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM mail_requests
            WHERE tenant_id = @TenantId
              AND mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        Assert.IsType<string>(scalar);
        return Guid.Parse((string)scalar);
    }

    private static string TwoTenantConfigJson =>
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
            },
            {
              "tenant_id": "{{OtherTenantId}}",
              "name": "example-staging",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN_2",
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
