using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Admin;

/// <summary>
/// Coverage for #390: once an admin cancel is committed, a failing post-commit webhook enqueue
/// must not turn the committed mutation into an HTTP failure, and reconciliation must recreate
/// the missing event.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class AdminCancelPostCommitEnqueueTests(MailerAdminWebhookFixture fixture)
    : IClassFixture<MailerAdminWebhookFixture>, IAsyncLifetime
{
    private const string BlockEnqueueTrigger = "test_390_block_delivery_event_insert";
    private static readonly Guid TenantA = MailerWebApplicationFixtureBase.TenantId;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
        await DropEnqueueBlockAsync(TestContext.Current.CancellationToken);
        fixture.LogCapture.Clear();
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminDeadLetterCountCache>().ClearForTests();
    }

    public async ValueTask DisposeAsync() =>
        await DropEnqueueBlockAsync(CancellationToken.None);

    [Fact]
    public async Task Committed_cancel_survives_a_failing_post_commit_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedQueuedRequestAsync(ct);
        await InstallEnqueueBlockAsync(ct);

        using var response = await PostCancelAsync(internalId, ct);

        // Before #390 the enqueue exception escaped the handler and surfaced as 500.
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"/admin/mail-requests/{internalId:D}", response.Headers.Location?.OriginalString);

        Assert.Equal(MailRequestState.Cancelled, await ReadStatusAsync(internalId, ct));

        var audit = await ReadLatestAuditAsync(internalId, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
        Assert.Null(audit.Value.ErrorCode);

        Assert.Equal(0, await CountDeliveryEventsAsync(internalId, ct));
    }

    [Fact]
    public async Task Post_commit_enqueue_failure_is_observable_without_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedQueuedRequestAsync(ct);
        await InstallEnqueueBlockAsync(ct);

        using var response = await PostCancelAsync(internalId, ct);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);

        var warning = Assert.Single(
            fixture.LogCapture.Snapshot(),
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.State.ContainsKey("Reason") &&
                entry.State.GetValueOrDefault("InternalRequestId") == internalId.ToString("D"));
        Assert.Equal(DeliveryEventEnqueuer.PostCommitReasonException, warning.State["Reason"]);

        var joined = fixture.LogCapture.JoinedOutputWithExceptions();
        Assert.DoesNotContain(MailerAdminWebhookFixture.WebhookSecret, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("93.184.216.34", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("noreply@example.com", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconciliation_recreates_the_event_missed_by_a_failed_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedQueuedRequestAsync(ct);
        await InstallEnqueueBlockAsync(ct);

        using (var response = await PostCancelAsync(internalId, ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        }

        Assert.Equal(0, await CountDeliveryEventsAsync(internalId, ct));

        await DropEnqueueBlockAsync(ct);
        var enqueuer = fixture.Factory.Services.GetRequiredService<DeliveryEventEnqueuer>();
        await enqueuer.ReconcileMissingTerminalEventsAsync(batchSize: 8, ct);

        Assert.Equal(1, await CountDeliveryEventsAsync(internalId, ct));

        // Reconciliation is idempotent: the UNIQUE (tenant_id, mail_request_id, event_type)
        // constraint plus the missing-event query must not produce a duplicate.
        await enqueuer.ReconcileMissingTerminalEventsAsync(batchSize: 8, ct);
        Assert.Equal(1, await CountDeliveryEventsAsync(internalId, ct));
    }

    [Fact]
    public async Task Successful_cancel_still_enqueues_the_event_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedQueuedRequestAsync(ct);

        using var response = await PostCancelAsync(internalId, ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(MailRequestState.Cancelled, await ReadStatusAsync(internalId, ct));
        Assert.Equal(1, await CountDeliveryEventsAsync(internalId, ct));

        var audit = await ReadLatestAuditAsync(internalId, ct);
        Assert.NotNull(audit);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Value.Result);
    }

    [Fact]
    public async Task Post_commit_enqueue_ignores_an_already_cancelled_caller_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var internalId = await SeedQueuedRequestAsync(ct);
        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        var enqueuer = fixture.Factory.Services.GetRequiredService<DeliveryEventEnqueuer>();

        await repository.TryConsumerCancelAsync(
            TenantA,
            MailerWebApplicationFixtureBase.SourceService,
            await ReadMailRequestIdAsync(internalId, ct),
            SqliteTime.UtcNow,
            ct);

        // A disconnected client leaves the request token cancelled. The post-commit helper takes no
        // token at all, so the immediate event is still written rather than deferred to reconcile.
        using var requestAborted = new CancellationTokenSource();
        await requestAborted.CancelAsync();

        Assert.True(await enqueuer.TryEnqueueAfterCommitAsync(internalId));
        Assert.Equal(1, await CountDeliveryEventsAsync(internalId, ct));
    }

    private async Task<HttpResponseMessage> PostCancelAsync(Guid internalId, CancellationToken cancellationToken)
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        await LoginAsync(client, cancellationToken);
        var csrf = await ReadCsrfTokenAsync(client, $"/admin/mail-requests/{internalId:D}", cancellationToken);

        return await client.PostAsync(
            $"/admin/mail-requests/{internalId:D}/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
            }),
            cancellationToken);
    }

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfTokenAsync(client, "/admin/login", cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf,
                ["username"] = MailerAdminWebhookFixture.Username,
                ["password"] = MailerAdminWebhookFixture.Password,
            }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{path} did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, $"{path} CSRF token value was empty.");
        return html[start..end];
    }

    /// <summary>
    /// Fails the delivery-event insert without touching schema or data, so the post-commit enqueue
    /// throws a real SqliteException on the production path.
    /// </summary>
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

    private async Task<Guid> SeedQueuedRequestAsync(CancellationToken cancellationToken)
    {
        var internalId = Guid.NewGuid();
        var mailRequestId = Guid.NewGuid();
        var nowStorage = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                @Status, 0, 3,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", TenantA.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@Now", nowStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return internalId;
    }

    private async Task<Guid> ReadMailRequestIdAsync(Guid internalId, CancellationToken cancellationToken) =>
        Guid.Parse(await ScalarAsync<string>(
            "SELECT mail_request_id FROM mail_requests WHERE id = @Id;",
            internalId,
            cancellationToken));

    private async Task<MailRequestState> ReadStatusAsync(Guid internalId, CancellationToken cancellationToken) =>
        (MailRequestState)await ScalarAsync<long>(
            "SELECT status FROM mail_requests WHERE id = @Id;",
            internalId,
            cancellationToken);

    private async Task<int> CountDeliveryEventsAsync(Guid internalId, CancellationToken cancellationToken) =>
        (int)await ScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM delivery_events de
            JOIN mail_requests mr
              ON mr.mail_request_id = de.mail_request_id
             AND mr.tenant_id = de.tenant_id
            WHERE mr.id = @Id;
            """,
            internalId,
            cancellationToken);

    private async Task<T> ScalarAsync<T>(string sql, Guid internalId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Id", internalId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        Assert.NotNull(value);
        return (T)value!;
    }

    private async Task<(string Result, string? ErrorCode)?> ReadLatestAuditAsync(
        Guid internalId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT result, error_code
            FROM admin_audit_events
            WHERE target_id = @TargetId AND event_type = @EventType
            ORDER BY occurred_at DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@TargetId", internalId.ToString("D"));
        command.Parameters.AddWithValue("@EventType", AdminAuditLog.EventTypes.ManualCancelRequested);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
