using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using Azure.Core.Pipeline;
using AcsEmailAddress = Azure.Communication.Email.EmailAddress;
using AcsEmailAttachment = Azure.Communication.Email.EmailAttachment;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailRecipients = Azure.Communication.Email.EmailRecipients;

namespace Amane.Mailer.Spike526.Probe;

public static class Spike526AcsEnvelopeCapture
{
    private const string DeterministicOperationId = "00000000-0000-0000-0000-000000000526";
    private const string FakeConnectionString =
        "endpoint=https://spike526.example.invalid/;accesskey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static async Task<Spike526AcsCaptureResult> CaptureAsync(
        Spike526Fixture fixture,
        CancellationToken cancellationToken = default)
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: true);
        var options = new EmailClientOptions
        {
            Transport = new HttpClientTransport(httpClient),
        };
        var client = new AcsEmailClient(FakeConnectionString, options);
        var message = CreateMessage(fixture);
        var operation = await client.SendAsync(
            WaitUntil.Started,
            message,
            Guid.Parse(DeterministicOperationId),
            cancellationToken);

        var capture = handler.Capture
            ?? throw new InvalidOperationException("ACS request content was not captured.");
        return new Spike526AcsCaptureResult(
            fixture.Id,
            capture.Bytes,
            capture.Sha256,
            operation.HasValue ? operation.Value.Status.ToString() : "no-value",
            operation.Id == DeterministicOperationId);
    }

    public static long EstimateUpperBound(Spike526Fixture fixture)
    {
        // Test-only candidate estimator. The fixed structural allowances are intentionally
        // conservative and are qualified against exact SDK capture. They are not production policy.
        long estimate = 16 * 1024;
        estimate += EncodedJsonBytes("sender@example.invalid") + 256;
        estimate += EncodedJsonBytes(fixture.Request.Subject) + 256;
        estimate += EncodedJsonBytes(fixture.Request.TextBody) + 256;
        estimate += EncodedJsonBytes(fixture.Request.HtmlBody) + 256;
        estimate += EncodedJsonBytes(fixture.Request.ReplyTo ?? string.Empty) + 128;

        foreach (var recipient in fixture.Request.To.Concat(fixture.Request.Cc).Concat(fixture.Request.Bcc))
        {
            estimate += EncodedJsonBytes(recipient.Email) + EncodedJsonBytes(recipient.DisplayName ?? string.Empty) + 512;
        }

        foreach (var attachment in fixture.Request.Attachments)
        {
            estimate += Base64EncodedLength(attachment.ByteLength);
            estimate += EncodedJsonBytes(attachment.FileName);
            estimate += EncodedJsonBytes(attachment.ContentType);
            estimate += 1024;
        }

        return estimate;
    }

    private static AcsEmailMessage CreateMessage(Spike526Fixture fixture)
    {
        var recipients = new AcsEmailRecipients(
            fixture.Request.To.Select(static item => new AcsEmailAddress(item.Email, item.DisplayName)).ToList(),
            fixture.Request.Cc.Select(static item => new AcsEmailAddress(item.Email, item.DisplayName)).ToList(),
            fixture.Request.Bcc.Select(static item => new AcsEmailAddress(item.Email, item.DisplayName)).ToList());
        var content = new AcsEmailContent(fixture.Request.Subject)
        {
            PlainText = fixture.Request.TextBody,
            Html = fixture.Request.HtmlBody,
        };
        var message = new AcsEmailMessage("sender@example.invalid", recipients, content);
        if (!string.IsNullOrWhiteSpace(fixture.Request.ReplyTo))
        {
            message.ReplyTo.Add(new AcsEmailAddress(fixture.Request.ReplyTo));
        }

        foreach (var attachment in fixture.Request.Attachments)
        {
            var bytes = Spike526FixtureFactory.DecodeAttachment(attachment);
            message.Attachments.Add(new AcsEmailAttachment(
                attachment.FileName,
                attachment.ContentType,
                BinaryData.FromBytes(bytes)));
        }

        return message;
    }

    private static long EncodedJsonBytes(string value) =>
        JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;

    private static long Base64EncodedLength(long binaryLength) =>
        checked(((binaryLength + 2) / 3) * 4);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal Capture? Capture { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                throw new InvalidOperationException("ACS SDK request had no content.");
            }

            await using var sink = new CountingHashStream();
            await request.Content.CopyToAsync(sink, cancellationToken);
            Capture = sink.Complete();

            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    "{\"id\":\"00000000-0000-0000-0000-000000000526\",\"status\":\"Running\"}",
                    Encoding.UTF8,
                    "application/json"),
                RequestMessage = request,
            };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "spike526-offline");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            return response;
        }
    }

    private sealed class CountingHashStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;
        private bool _completed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfCompleted();
            _hash.AppendData(buffer);
            _bytes += buffer.Length;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        internal Capture Complete()
        {
            ThrowIfCompleted();
            _completed = true;
            return new Capture(
                _bytes,
                Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void ThrowIfCompleted()
        {
            if (_completed)
            {
                throw new InvalidOperationException("Capture stream is already complete.");
            }
        }
    }

    private sealed record Capture(long Bytes, string Sha256);
}
