using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Webhooks;
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

    private HttpClient CreateAuthorizedClient()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", WebhookWorkerFixture.Token);
        return client;
    }

    private async Task WaitUntilMailDeliveredAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadMailStatusAsync(mailRequestId, cancellationToken);
            if (state == MailRequestState.Delivered)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Mail request {mailRequestId:D} was not delivered in time.");
    }

    private async Task WaitUntilWebhookDeadLetteredAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            if (scalar is long status && status == (long)DeliveryEventState.DeadLettered)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Webhook for mail request {mailRequestId:D} was not dead-lettered in time.");
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
}

public sealed class WebhookWorkerFixture() : MailerWebApplicationFixtureBase(workerEnabled: true)
{
    public const string WebhookSecret = "test-webhook-secret";
    public CapturingMailDeliveryProvider DeliveryProvider { get; } = new();
    public RecordingWebhookHandler WebhookHandler { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
            ["Mailer:Webhook:MaxAttempts"] = "3",
            ["Mailer:Webhook:InitialDelaySeconds"] = "1",
            ["Mailer:Webhook:MaxDelaySeconds"] = "2",
            ["Mailer:Webhook:DeliveryTimeoutSeconds"] = "2",
            ["Mailer:Webhook:LeaseDurationSeconds"] = "20",
            ["TEST_WEBHOOK_SECRET"] = WebhookSecret,
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.RemoveAll<IMailDeliveryProvider>();
        services.AddSingleton<IMailDeliveryProvider>(DeliveryProvider);
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(WebhookHandler));
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

public sealed class RecordingWebhookHandler
{
    private TaskCompletionSource<RecordedWebhookDelivery> _deliveryTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int AttemptCount { get; private set; }

    public int FailuresBeforeSuccess { get; set; }

    public void Reset()
    {
        AttemptCount = 0;
        FailuresBeforeSuccess = 0;
        _deliveryTcs = new TaskCompletionSource<RecordedWebhookDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
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
