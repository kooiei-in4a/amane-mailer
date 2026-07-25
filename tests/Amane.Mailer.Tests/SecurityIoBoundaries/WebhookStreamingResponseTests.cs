using System.Net;
using System.Net.Http;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

public sealed class WebhookDeliveryClientTests
{
    private static readonly byte[] LargeBodyMarker = Encoding.UTF8.GetBytes("WEBHOOK_BODY_SHOULD_NOT_BE_BUFFERED");

    [Fact]
    public async Task DeliverAsync_returns_timeout_instead_of_throwing_when_delivery_exceeds_limit()
    {
        var factory = new SingleHandlerClientFactory(new SlowHeadersWebhookMessageHandler(TimeSpan.FromSeconds(5)));
        var client = CreateClient(factory);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            """{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""",
            timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TIMEOUT", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task DeliverAsync_succeeds_on_204_no_content()
    {
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.NoContent));
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task DeliverAsync_succeeds_on_200_without_buffering_large_body()
    {
        var content = new TrackingStreamContent(CreateLargeBodyStream(length: 8 * 1024 * 1024));
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.OK, content));
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.True(result.Succeeded);
        Assert.Equal(0, content.BytesRead);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task DeliverAsync_succeeds_on_200_without_waiting_for_never_ending_body()
    {
        var bodyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyReadAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new TrackingStreamContent(new GateStream(bodyReadAttempted, bodyGate.Task));
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.OK, content));
        var client = CreateClient(factory);

        var deliverTask = DeliverAsync(client);
        var completed = await Task.WhenAny(
            deliverTask,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(deliverTask, completed);
        var result = await deliverTask;
        Assert.True(result.Succeeded);
        Assert.False(bodyReadAttempted.Task.IsCompleted);
        Assert.True(content.WasDisposed);

        bodyGate.SetResult();
    }

    [Fact]
    public async Task DeliverAsync_treats_429_with_body_as_retryable_failure()
    {
        var content = new TrackingStreamContent(CreateLargeBodyStream(length: 1024 * 1024));
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.TooManyRequests, content));
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_429", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.Equal(0, content.BytesRead);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task DeliverAsync_treats_500_with_body_as_retryable_failure()
    {
        var content = new TrackingStreamContent(CreateLargeBodyStream(length: 1024 * 1024));
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.InternalServerError, content));
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_500", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.Equal(0, content.BytesRead);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task DeliverAsync_treats_400_with_body_as_terminal_failure()
    {
        var content = new TrackingStreamContent(CreateLargeBodyStream(length: 1024 * 1024));
        var factory = new SingleHandlerClientFactory(
            new StatusCodeWebhookMessageHandler(HttpStatusCode.BadRequest, content));
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_HTTP_400", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Equal(0, content.BytesRead);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task DeliverAsync_returns_transport_error_on_HttpRequestException()
    {
        var factory = new SingleHandlerClientFactory(new ThrowingWebhookMessageHandler());
        var client = CreateClient(factory);

        var result = await DeliverAsync(client);

        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TRANSPORT_ERROR", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task DeliverAsync_returns_timeout_and_does_not_create_body_when_canceled_before_headers()
    {
        var headersGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new TrackingStreamContent(CreateLargeBodyStream(length: 64 * 1024));
        var factory = new SingleHandlerClientFactory(
            new GatedHeadersWebhookMessageHandler(headersGate.Task, HttpStatusCode.OK, content));
        var client = CreateClient(factory);

        using var cts = new CancellationTokenSource();
        var deliverTask = client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            """{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""",
            cts.Token);

        cts.Cancel();
        headersGate.SetResult();

        var result = await deliverTask;
        Assert.False(result.Succeeded);
        Assert.Equal("WEBHOOK_TIMEOUT", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.False(content.WasCreated);
        Assert.Equal(0, content.BytesRead);
    }

    private static WebhookDeliveryClient CreateClient(IHttpClientFactory factory) =>
        new(
            factory,
            new WebhookSignatureService(),
            new WebhookUrlValidator(),
            TimeProvider.System,
            NullLogger<WebhookDeliveryClient>.Instance);

    private static Task<WebhookDeliveryResult> DeliverAsync(WebhookDeliveryClient client) =>
        client.DeliverAsync(
            CreateTenant(),
            "secret",
            CreatePayload(),
            """{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""",
            CancellationToken.None);

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

    private static Stream CreateLargeBodyStream(int length)
    {
        var buffer = new byte[length];
        for (var offset = 0; offset < length; offset += LargeBodyMarker.Length)
        {
            var copyLength = Math.Min(LargeBodyMarker.Length, length - offset);
            Buffer.BlockCopy(LargeBodyMarker, 0, buffer, offset, copyLength);
        }

        return new MemoryStream(buffer, writable: false);
    }

    private sealed class SlowHeadersWebhookMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class GatedHeadersWebhookMessageHandler(
        Task headersReady,
        HttpStatusCode statusCode,
        TrackingStreamContent content) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await headersReady.WaitAsync(cancellationToken);
            content.MarkCreated();
            return new HttpResponseMessage(statusCode)
            {
                Content = content,
            };
        }
    }

    private sealed class StatusCodeWebhookMessageHandler(
        HttpStatusCode statusCode,
        HttpContent? content = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (content is TrackingStreamContent tracking)
            {
                tracking.MarkCreated();
            }

            var response = new HttpResponseMessage(statusCode);
            if (content is not null)
            {
                response.Content = content;
            }

            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingWebhookMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated transport failure");
    }

    private sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class TrackingStreamContent : HttpContent
    {
        private readonly Stream _stream;

        public TrackingStreamContent(Stream stream)
        {
            _stream = stream;
        }

        public long BytesRead { get; private set; }

        public bool WasDisposed { get; private set; }

        public bool WasCreated { get; private set; }

        public void MarkCreated() => WasCreated = true;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read == 0)
                {
                    break;
                }

                BytesRead += read;
                await stream.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(_stream, bytes => BytesRead += bytes));

        protected override bool TryComputeLength(out long length)
        {
            if (_stream.CanSeek)
            {
                length = _stream.Length;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(Stream inner, Action<long> onRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                onRead(read);
            }

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0)
            {
                onRead(read);
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                onRead(read);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GateStream(
        TaskCompletionSource bodyReadAttempted,
        Task bodyGate) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            bodyReadAttempted.TrySetResult();
            await bodyGate.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
