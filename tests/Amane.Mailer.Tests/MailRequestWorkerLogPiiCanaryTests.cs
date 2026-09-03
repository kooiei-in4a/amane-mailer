using System.Text.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

/// <summary>
/// Isolated coverage for #291: Worker terminal-failure ILogger output must not
/// emit mail-payload PII or provider secret canaries. Complements the DB-only
/// sanitize assertion in <see cref="MailRequestWorkerTests"/>.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerLogPiiCanaryTests
{
    [Fact]
    public async Task Worker_terminal_failure_logs_do_not_contain_pii_or_secret_canaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-worker-log-canary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        var deliveryProvider = new CapturingMailDeliveryProvider();
        var logCapture = new CapturingLoggerProvider();

        const string recipientCanary = "pii-canary-recipient@canary.example";
        const string subjectCanary = "PII-CANARY-SUBJECT-7f3a9c";
        const string htmlBodyCanary = "PII-CANARY-HTML-BODY-d4e21b";
        const string textBodyCanary = "PII-CANARY-TEXT-BODY-a91c08";
        const string metadataCanary = "PII-CANARY-METADATA-VALUE-55bc12";
        const string bearerCanary = "canary-bearer-token-value-abc123xyz";
        const string accessKeyCanary = "CanaryAccessKeyValue==XYZ9f2";
        const string acsEndpointHost = "canary-acs.communication.azure.com";
        // AcsRequestFailed classifies as DefinitiveFailed/Failed (AttachmentProviderResultClassifier,
        // shared with plain requests since Issue #546); a generic code like the former
        // "SMTP_CONNECT_FAILED" now converges to Unknown/DeliveryUnknown instead (ADR 0023 D-04).
        const string errorCode = MailDeliveryErrorCodes.AcsRequestFailed;
        const string triageMessage = "SMTP connect failed";

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
                            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
                            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        }));
                    builder.ConfigureLogging(logging => logging.AddProvider(logCapture));
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IMailDeliveryProvider>();
                        services.AddSingleton<IMailDeliveryProvider>(deliveryProvider);
                    });
                });

            _ = factory.CreateClient();

            var seeded = MailRequestTestData.CreateRequest(
                subject: subjectCanary,
                metadata: new Dictionary<string, string>
                {
                    ["form_id"] = metadataCanary,
                }) with
            {
                To =
                [
                    new MailRecipientDto
                    {
                        Email = recipientCanary,
                        DisplayName = "PII Canary",
                    },
                ],
                HtmlBody = $"<p>{htmlBodyCanary}</p>",
                TextBody = textBodyCanary,
            };
            seeded = seeded with
            {
                PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(seeded),
            };

            var rawError =
                $"{triageMessage}: Endpoint=https://{acsEndpointHost}/;AccessKey={accessKeyCanary} " +
                $"Bearer {bearerCanary} sender={recipientCanary}";
            deliveryProvider.QueueResult(MailDeliveryResult.Failure(
                errorCode,
                rawError,
                retryable: false));

            var request = await SeedQueuedRequestAsync(factory, seeded, ct);
            SignalWorker(factory);
            await WaitUntilStatusAsync(connectionString, request.MailRequestId, MailRequestState.Failed, ct);
            await WaitUntilLogContainsAsync(logCapture, "failed terminally", ct);

            var attemptErrorCode = await ReadLatestAttemptErrorCodeAsync(connectionString, request.MailRequestId, ct);
            Assert.Equal(errorCode, attemptErrorCode);

            var logs = logCapture.JoinedOutput();
            Assert.Contains("failed terminally", logs, StringComparison.Ordinal);
            Assert.Contains(errorCode, logs, StringComparison.Ordinal);
            Assert.Contains(triageMessage, logs, StringComparison.Ordinal);

            // Recipient / bearer / ACS fragments are embedded in the raw provider
            // error and exercise ProviderErrorSanitizer before logging.
            Assert.DoesNotContain(recipientCanary, logs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(bearerCanary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(accessKeyCanary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(acsEndpointHost, logs, StringComparison.OrdinalIgnoreCase);

            // Subject / body / metadata are never interpolated into Worker failure
            // log templates today; these asserts are regression guards against a
            // future template that starts logging mail-payload fields.
            Assert.DoesNotContain(subjectCanary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(htmlBodyCanary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(textBodyCanary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(metadataCanary, logs, StringComparison.Ordinal);
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
        MailRequestCreateRequest request,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Bounded poll for a Worker log substring after terminal status is observable.
    /// Status persistence can precede the terminal-failure log write; do not dump
    /// captured log text on timeout (may contain canary secrets/PII in failure paths).
    /// </summary>
    private static async Task WaitUntilLogContainsAsync(
        CapturingLoggerProvider logCapture,
        string expectedSubstring,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var lastLineCount = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            lastLineCount = logCapture.Snapshot().Count;
            if (logCapture.JoinedOutput().Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail(
            $"Timed out waiting for captured logs to contain the expected terminal-failure marker. " +
            $"CapturedLineCount={lastLineCount}.");
    }

    private static async Task<string?> ReadLatestAttemptErrorCodeAsync(
        string connectionString,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.error_code
            FROM mail_attempts a
            INNER JOIN mail_requests r ON r.id = a.request_id
            WHERE r.mail_request_id = @MailRequestId
            ORDER BY a.attempt_number DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is string code ? code : null;
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
