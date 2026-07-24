using System.Net;
using System.Net.Http.Headers;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #345 / #355 — outbound webhook judges delivery from
/// response headers without buffering body.
/// </summary>
public sealed class WebhookStreamingResponseTests
{
    private static readonly string MinimalPayloadJson =
        """{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""";

    [Fact]
    public async Task DeliverAsync_204_no_content_returns_success_without_body()
    {
        var handler = StreamingWebhookHandler.WithStatus(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.True(handler.ResponseDisposed);
        Assert.Equal(0, handler.BodyBytesRead);
    }

    [Fact]
    public async Task DeliverAsync_200_with_held_body_returns_success_before_body_completes()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(HttpStatusCode.OK, bodyRelease.Task, payloadSize: 64 * 1024);
        var client = CreateClient(handler);

        var deliverTask = client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken);

        var result = await deliverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(bodyRelease.Task.IsCompleted);
        Assert.Equal(0, handler.BodyBytesRead);

        bodyRelease.TrySetResult();
        Assert.True(handler.ResponseDisposed);
    }

    [Fact]
    public async Task DeliverAsync_200_with_never_ending_body_returns_success_after_headers_before_body_finishes()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(HttpStatusCode.OK, bodyRelease.Task, payloadSize: 1);
        var client = CreateClient(handler);

        var deliverTask = client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken);

        var result = await deliverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(bodyRelease.Task.IsCompleted);

        bodyRelease.TrySetResult();
        Assert.True(handler.ResponseDisposed);
    }

    [Fact]
    public async Task DeliverAsync_429_with_held_body_returns_retryable_webhook_http_429_before_body_completes()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(
            HttpStatusCode.TooManyRequests,
            bodyRelease.Task,
            payloadSize: 8 * 1024);
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_429", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.False(bodyRelease.Task.IsCompleted);

        bodyRelease.TrySetResult();
        Assert.True(handler.ResponseDisposed);
    }

    [Fact]
    public async Task DeliverAsync_500_with_held_body_returns_retryable_webhook_http_500_before_body_completes()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(
            HttpStatusCode.InternalServerError,
            bodyRelease.Task,
            payloadSize: 8 * 1024);
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_500", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.False(bodyRelease.Task.IsCompleted);

        bodyRelease.TrySetResult();
        Assert.True(handler.ResponseDisposed);
    }

    [Fact]
    public async Task DeliverAsync_400_with_held_body_returns_terminal_webhook_http_400_before_body_completes()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(
            HttpStatusCode.BadRequest,
            bodyRelease.Task,
            payloadSize: 8 * 1024);
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_400", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.False(bodyRelease.Task.IsCompleted);

        bodyRelease.TrySetResult();
        Assert.True(handler.ResponseDisposed);
    }

    [Fact]
    public async Task DeliverAsync_cancelled_before_headers_returns_retryable_webhook_timeout()
    {
        var handler = new SlowHeadersWebhookHandler(TimeSpan.FromSeconds(5));
        var client = CreateClient(handler);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TIMEOUT", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task DeliverAsync_transport_error_returns_retryable_webhook_transport_error()
    {
        var handler = new ThrowingWebhookHandler(new HttpRequestException("connection reset"));
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TRANSPORT_ERROR", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task DeliverAsync_success_disposes_response_after_headers_read_judgment()
    {
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = StreamingWebhookHandler.WithHeldBody(HttpStatusCode.OK, bodyRelease.Task, payloadSize: 1024);
        var client = CreateClient(handler);

        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            MinimalPayloadJson,
            TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(handler.ResponseDisposed);
        Assert.True(handler.ContentDisposed);

        bodyRelease.TrySetResult();
    }

    private static WebhookDeliveryClient CreateClient(HttpMessageHandler handler) =>
        new(
            new SingleHandlerClientFactory(handler),
            new WebhookSignatureService(),
            new WebhookUrlValidator(),
            TimeProvider.System,
            NullLogger<WebhookDeliveryClient>.Instance);

    private static MailerTenant CreateTenant() =>
        new()
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            Name = "example",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress { Email = "noreply@example.com" },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
            },
            Webhook = new MailerWebhookConfig
            {
                Url = "https://93.184.216.34/webhook",
                SecretEnv = "TEST_WEBHOOK_SECRET",
            },
        };

    private static MailDeliveryEventPayload CreatePayload() =>
        new()
        {
            EventId = Guid.NewGuid(),
            EventType = MailDeliveryEventType.Delivered,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            SourceService = "example-service",
            MailRequestId = Guid.NewGuid(),
            Status = MailDeliveryEventType.Delivered,
            AttemptCount = 1,
        };

    /// <summary>
    /// Real <see cref="HttpMessageHandler"/> that returns headers immediately while body
    /// reads block on a TCS — proving <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
    /// </summary>
    private sealed class StreamingWebhookHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly Task? _bodyRelease;
        private readonly int _payloadSize;
        private int _bodyBytesRead;

        private StreamingWebhookHandler(HttpStatusCode statusCode, Task? bodyRelease, int payloadSize)
        {
            _statusCode = statusCode;
            _bodyRelease = bodyRelease;
            _payloadSize = payloadSize;
        }

        public bool ResponseDisposed { get; private set; }

        public bool ContentDisposed { get; private set; }

        public int BodyBytesRead => Volatile.Read(ref _bodyBytesRead);

        public static StreamingWebhookHandler WithStatus(HttpStatusCode statusCode) =>
            new(statusCode, bodyRelease: null, payloadSize: 0);

        public static StreamingWebhookHandler WithHeldBody(
            HttpStatusCode statusCode,
            Task bodyRelease,
            int payloadSize) =>
            new(statusCode, bodyRelease, payloadSize);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new TrackingHttpResponseMessage(_statusCode, disposed =>
            {
                if (disposed)
                {
                    ResponseDisposed = true;
                }
            });

            if (_bodyRelease is not null && _payloadSize > 0)
            {
                var stream = new HoldUntilReleasedStream(
                    _bodyRelease,
                    _payloadSize,
                    bytesRead => Interlocked.Add(ref _bodyBytesRead, bytesRead),
                    () => ContentDisposed = true);
                response.Content = new StreamContent(stream)
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                        ContentLength = _payloadSize,
                    },
                };
            }

            return Task.FromResult<HttpResponseMessage>(response);
        }
    }

    private sealed class SlowHeadersWebhookHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingWebhookHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class TrackingHttpResponseMessage(
        HttpStatusCode statusCode,
        Action<bool> onDisposed) : HttpResponseMessage(statusCode)
    {
        protected override void Dispose(bool disposing)
        {
            onDisposed(disposing);
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Stream whose first <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> waits on
    /// <paramref name="release"/> — hangs under ResponseContentRead, not under HeadersRead.
    /// </summary>
    private sealed class HoldUntilReleasedStream(
        Task release,
        int payloadSize,
        Action<int> onBytesRead,
        Action onDisposed) : Stream
    {
        private int _position;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payloadSize;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_position >= payloadSize)
            {
                return 0;
            }

            var remaining = payloadSize - _position;
            var toCopy = Math.Min(remaining, buffer.Length);
            buffer.Span[..toCopy].Fill(0x41);
            _position += toCopy;
            onBytesRead(toCopy);
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    onDisposed();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }
}
