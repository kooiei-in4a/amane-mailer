using System.Globalization;
using System.Net;
using System.Text;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailerMetricsTests(MailerMetricsFixture fixture)
    : IClassFixture<MailerMetricsFixture>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");
    private static readonly DateTimeOffset FixedNow = MailerMetricsFixture.FixedNow;
    private const string MetricsBearerToken = "metrics-scrape-token";

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        fixture.Factory.Services.GetRequiredService<MailerRuntimeMetrics>().ClearForTests();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Metrics_returns_200_and_text_plain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);

        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.Contains("# TYPE mail_queue_ready_count gauge", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_includes_required_metric_names()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-10),
            nextAttemptAt: null,
            ct);

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mail_requests_accepted_total", body, StringComparison.Ordinal);
        Assert.Contains("mail_deliveries_total", body, StringComparison.Ordinal);
        Assert.Contains("mail_delivery_duration_seconds", body, StringComparison.Ordinal);
        Assert.Contains("mail_queue_ready_count", body, StringComparison.Ordinal);
        Assert.Contains("mail_queue_oldest_age_seconds", body, StringComparison.Ordinal);
        Assert.Contains("mail_retries_total", body, StringComparison.Ordinal);
        Assert.Contains("mail_finalize_skipped_total", body, StringComparison.Ordinal);
        Assert.Contains("mail_dead_letters_total", body, StringComparison.Ordinal);
        Assert.Contains("mail_webhook_events_pending", body, StringComparison.Ordinal);
        Assert.Contains("mail_webhook_events_dead_lettered", body, StringComparison.Ordinal);
        Assert.Contains("mail_worker_heartbeat_age_seconds", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_webhook_values_match_db_stats()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedDeliveryEventAsync(
            MailerWebApplicationFixtureBase.TenantId,
            DeliveryEventState.Pending,
            ct);
        await SeedDeliveryEventAsync(
            MailerWebApplicationFixtureBase.TenantId,
            DeliveryEventState.Delivering,
            ct);
        await SeedDeliveryEventAsync(
            OtherTenantId,
            DeliveryEventState.DeadLettered,
            ct);
        await SeedDeliveryEventAsync(
            OtherTenantId,
            DeliveryEventState.Delivered,
            ct);

        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = fixture.ConnectionString,
                })
                .Build());
        var command = new DbStatsCommand(factory, () => FixedNow);
        var cliOutput = new StringWriter();
        var cliError = new StringWriter();
        var exitCode = await command.ExecuteAsync(["db", "stats"], cliOutput, cliError, ct);

        Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);
        var cliStats = ParseStats(cliOutput.ToString());

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("# TYPE mail_webhook_events_pending gauge", body, StringComparison.Ordinal);
        Assert.Contains("# TYPE mail_webhook_events_dead_lettered gauge", body, StringComparison.Ordinal);
        Assert.Contains(
            "mail_webhook_events_pending " + cliStats["webhook_events_pending"],
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "mail_webhook_events_dead_lettered " + cliStats["webhook_events_dead_lettered"],
            body,
            StringComparison.Ordinal);
        Assert.Contains("mail_queue_ready_count", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_queue_values_match_db_stats_for_all_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-45),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Failed,
            FixedNow.AddMinutes(-20),
            nextAttemptAt: null,
            ct);
        await SeedMailRequestAsync(
            OtherTenantId,
            MailRequestState.Queued,
            FixedNow.AddMinutes(-45),
            nextAttemptAt: null,
            ct);

        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = fixture.ConnectionString,
                })
                .Build());
        var command = new DbStatsCommand(factory, () => FixedNow);
        var cliOutput = new StringWriter();
        var cliError = new StringWriter();
        var exitCode = await command.ExecuteAsync(["db", "stats"], cliOutput, cliError, ct);

        Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);
        var cliStats = ParseStats(cliOutput.ToString());

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "mail_queue_ready_count " + cliStats["ready_backlog_count"],
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "mail_queue_oldest_age_seconds " + cliStats["oldest_queued_age_seconds"],
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "mail_dead_letters_total " + cliStats["status_dead_lettered"],
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_output_contains_no_pii_labels()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestId = await SeedMailRequestAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailRequestState.Failed,
            FixedNow.AddMinutes(-5),
            nextAttemptAt: null,
            ct);
        await SeedMailAttemptAsync(requestId, "mailpit", (int)MailRequestState.Failed, ct);

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("recipient_email", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mail_request_id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant_id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("source_service", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ops-recipient@example.com", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ops subject must not leak", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_disabled_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Mailer:Metrics:Enabled"] = "false",
        });

        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/metrics", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_bearer_required_when_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Mailer:Metrics:BearerToken"] = MetricsBearerToken,
        });

        using var client = CreateClient(factory);
        using var unauthorized = await client.GetAsync("/metrics", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var authorized = await SendMetricsAsync(client, MetricsBearerToken, ct);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task Metrics_returns_503_when_schema_is_not_migrated()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer-unmigrated.db");
        var tenantConfigDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(tenantConfigDirectory);
        var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, UnmigratedTenantConfigJson, ct);

        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                    ["Mailer:Worker:Enabled"] = "false",
                    ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(Microsoft.Extensions.Hosting.IHostedService));
            });
        });

        try
        {
            using var client = CreateClient(factory);
            using var response = await client.GetAsync("/metrics", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Metrics_accepted_total_increments_on_new_mail_request()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient(fixture.Factory);
        var request = MailRequestTestData.CreateRequest();

        using var post = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var metrics = await client.GetAsync("/metrics", ct);
        var body = await metrics.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Contains("mail_requests_accepted_total 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_delivery_and_retry_counters_increment_on_finalize()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var internalId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        var startedAt = FixedNow.AddSeconds(-2);
        var completedAt = FixedNow;

        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = internalId,
                TenantId = MailerWebApplicationFixtureBase.TenantId,
                SourceService = MailerWebApplicationFixtureBase.SourceService,
                MailRequestId = Guid.NewGuid(),
                Purpose = "MetricsTest",
                PayloadJson = "{}",
                PayloadHash = new string('1', 64),
                Subject = "metrics subject",
                RecipientEmail = "metrics-recipient@example.com",
                MaxAttempts = 3,
                AcceptedAt = FixedNow.AddMinutes(-5),
            },
            ct);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var claim = connection.CreateCommand();
        claim.CommandText = """
            UPDATE mail_requests
            SET status = @ProcessingStatus,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                attempt_count = 2,
                updated_at = @UpdatedAt
            WHERE id = @Id;
            """;
        claim.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        claim.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        claim.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(5)));
        claim.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(FixedNow));
        claim.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        await claim.ExecuteNonQueryAsync(ct);

        var finalized = await repository.FinalizeAsync(
            internalId,
            lockToken,
            completedAt,
            MailRequestFinalizeOutcome.RetryScheduled,
            FixedNow.AddMinutes(1),
            "provider_error",
            new MailAttemptInsert
            {
                RequestId = internalId,
                AttemptNumber = 2,
                Provider = "mailpit",
                Status = MailRequestState.Failed,
                ErrorCode = "provider_error",
                ErrorMessage = "sanitized",
                Retryable = true,
                LockToken = lockToken,
                StartedAt = startedAt,
                CompletedAt = completedAt,
            },
            ct);
        Assert.True(finalized);

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("mail_deliveries_total{result=\"failed\",provider=\"mailpit\"} 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_retries_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_delivery_duration_seconds_count{provider=\"mailpit\"} 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_finalize_skipped_increments_when_delivered_finalize_loses_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var internalId = Guid.NewGuid();
        var mailRequestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        var startedAt = FixedNow.AddSeconds(-2);
        var completedAt = FixedNow;

        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = internalId,
                TenantId = MailerWebApplicationFixtureBase.TenantId,
                SourceService = MailerWebApplicationFixtureBase.SourceService,
                MailRequestId = mailRequestId,
                Purpose = "MetricsFinalizeSkip",
                PayloadJson = "{}",
                PayloadHash = new string('2', 64),
                Subject = "finalize skip subject",
                RecipientEmail = "finalize-skip@example.com",
                MaxAttempts = 3,
                AcceptedAt = FixedNow.AddMinutes(-5),
            },
            ct);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var claim = connection.CreateCommand();
        claim.CommandText = """
            UPDATE mail_requests
            SET status = @ProcessingStatus,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                attempt_count = 1,
                updated_at = @UpdatedAt
            WHERE id = @Id;
            """;
        claim.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        claim.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        claim.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(-1)));
        claim.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(FixedNow));
        claim.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        await claim.ExecuteNonQueryAsync(ct);

        var finalized = await repository.FinalizeAsync(
            internalId,
            lockToken,
            completedAt,
            MailRequestFinalizeOutcome.Delivered,
            nextAttemptAt: null,
            lastErrorMessage: null,
            new MailAttemptInsert
            {
                RequestId = internalId,
                AttemptNumber = 1,
                Provider = "mailpit",
                Status = MailRequestState.Delivered,
                ProviderMessageId = "provider-msg-finalize-skip",
                Retryable = false,
                LockToken = lockToken,
                StartedAt = startedAt,
                CompletedAt = completedAt,
            },
            ct);
        // Strict lease fencing fails, but the same lock_token still owns the row so
        // the request is completed via the expired-lease delivered recovery path.
        Assert.True(finalized);

        var priorSuccess = await repository.FindSuccessfulDeliveryAttemptAsync(internalId, ct);
        Assert.NotNull(priorSuccess);
        Assert.Equal("provider-msg-finalize-skip", priorSuccess!.ProviderMessageId);

        var state = await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, ct);
        Assert.NotNull(state);
        Assert.Equal(MailRequestState.Delivered, state!.Status);

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("mail_finalize_skipped_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_deliveries_total{result=\"delivered\",provider=\"mailpit\"} 1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_finalize_skipped_persists_delivered_evidence_when_row_already_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var internalId = Guid.NewGuid();
        var mailRequestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        var startedAt = FixedNow.AddSeconds(-2);
        var completedAt = FixedNow;

        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = internalId,
                TenantId = MailerWebApplicationFixtureBase.TenantId,
                SourceService = MailerWebApplicationFixtureBase.SourceService,
                MailRequestId = mailRequestId,
                Purpose = "MetricsFinalizeSkipDeadLetter",
                PayloadJson = "{}",
                PayloadHash = new string('3', 64),
                Subject = "finalize skip dead letter subject",
                RecipientEmail = "finalize-skip-dl@example.com",
                MaxAttempts = 3,
                AcceptedAt = FixedNow.AddMinutes(-5),
            },
            ct);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using var deadLetter = connection.CreateCommand();
        deadLetter.CommandText = """
            UPDATE mail_requests
            SET status = @DeadLetteredStatus,
                attempt_count = 3,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @UpdatedAt,
                completed_at = @UpdatedAt,
                failed_at = @UpdatedAt,
                last_error_message = @LastErrorMessage
            WHERE id = @Id;
            """;
        deadLetter.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
        deadLetter.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(FixedNow));
        deadLetter.Parameters.AddWithValue("@LastErrorMessage", "Processing lease expired after the request reached max_attempts.");
        deadLetter.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        await deadLetter.ExecuteNonQueryAsync(ct);

        await using (var insertReaperAttempt = connection.CreateCommand())
        {
            insertReaperAttempt.CommandText = """
                INSERT INTO mail_attempts (
                    request_id, attempt_number, provider, status,
                    provider_message_id, error_code, error_message, retryable,
                    lock_token, started_at, completed_at)
                VALUES (
                    @RequestId, 3, 'lease-reaper', @DeadLetteredStatus,
                    NULL, 'PROCESSING_LEASE_EXPIRED_MAX_ATTEMPTS', @ErrorMessage, 1,
                    @LockToken, @StartedAt, @CompletedAt);
                """;
            insertReaperAttempt.Parameters.AddWithValue("@RequestId", internalId.ToString("D"));
            insertReaperAttempt.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
            insertReaperAttempt.Parameters.AddWithValue("@ErrorMessage", "Processing lease expired after the request reached max_attempts.");
            insertReaperAttempt.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            insertReaperAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(startedAt));
            insertReaperAttempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
            await insertReaperAttempt.ExecuteNonQueryAsync(ct);
        }

        var finalized = await repository.FinalizeAsync(
            internalId,
            lockToken,
            completedAt,
            MailRequestFinalizeOutcome.Delivered,
            nextAttemptAt: null,
            lastErrorMessage: null,
            new MailAttemptInsert
            {
                RequestId = internalId,
                AttemptNumber = 3,
                Provider = "mailpit",
                Status = MailRequestState.Delivered,
                ProviderMessageId = "provider-msg-after-reaper",
                Retryable = false,
                LockToken = lockToken,
                StartedAt = startedAt,
                CompletedAt = completedAt,
            },
            ct);
        Assert.False(finalized);

        var priorSuccess = await repository.FindSuccessfulDeliveryAttemptAsync(internalId, ct);
        Assert.NotNull(priorSuccess);
        Assert.Equal("provider-msg-after-reaper", priorSuccess!.ProviderMessageId);

        var state = await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, ct);
        Assert.NotNull(state);
        Assert.Equal(MailRequestState.DeadLettered, state!.Status);

        using var client = CreateClient(fixture.Factory);
        using var response = await client.GetAsync("/metrics", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("mail_finalize_skipped_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_deliveries_total{result=\"delivered\",provider=\"mailpit\"} 1", body, StringComparison.Ordinal);
    }

    private WebApplicationFactory<global::Program> CreateFactory(IReadOnlyDictionary<string, string?> extraConfiguration)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Mailer"] = fixture.ConnectionString,
            ["MAILER_TENANTS_PATH"] = fixture.TenantConfigPath,
            ["Mailer:Worker:Enabled"] = "false",
            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
        };

        foreach (var (key, value) in extraConfiguration)
        {
            settings[key] = value;
        }

        return new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(Microsoft.Extensions.Hosting.IHostedService));
            });
        });
    }

    private static async Task<HttpResponseMessage> SendMetricsAsync(
        HttpClient client,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static HttpClient CreateAuthorizedClient(WebApplicationFactory<global::Program> factory)
    {
        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);
        return client;
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private async Task<Guid> SeedMailRequestAsync(
        Guid tenantId,
        MailRequestState status,
        DateTimeOffset updatedAt,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, next_attempt_at,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'MetricsTest',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, 0, 3, @NextAttemptAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Subject", "Ops subject must not leak");
        command.Parameters.AddWithValue("@RecipientEmail", "ops-recipient@example.com");
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@NextAttemptAt", nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private async Task SeedMailAttemptAsync(
        Guid requestId,
        string provider,
        int status,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message,
                retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, 1, @Provider, @Status,
                NULL, 'provider_error', 'secret provider detail',
                0, @LockToken, @StartedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@Provider", provider);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(-1)));
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SeedDeliveryEventAsync(
        Guid tenantId,
        DeliveryEventState status,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_events (
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, next_attempt_at,
                created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @EventType, @PayloadJson,
                @Status, 0, 3, NULL,
                @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@EventType", MailDeliveryEventType.Delivered);
        command.Parameters.AddWithValue("@PayloadJson", """{"event_id":"00000000-0000-0000-0000-000000000099"}""");
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseStats(string output)
    {
        var stats = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            stats[line[..separator]] = line[(separator + 1)..];
        }

        return stats;
    }

    private static string UnmigratedTenantConfigJson =>
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
