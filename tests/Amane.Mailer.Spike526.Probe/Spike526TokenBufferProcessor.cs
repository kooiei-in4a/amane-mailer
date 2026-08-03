using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Json;

namespace Amane.Mailer.Spike526.Probe;

public sealed record Spike526TokenBufferOptions(
    long MaxRequestBytes,
    long MaxPerFileDecodedBytes,
    long MaxTotalDecodedBytes,
    int DecodeBufferBytes = 64 * 1024,
    int PipeMinimumReadSize = 8 * 1024);

public static class Spike526TokenBufferProcessor
{
    public static async Task<Spike526TokenBufferResult> ProcessAsync(
        string fixtureId,
        Stream source,
        Spike526TempStore tempStore,
        Spike526TokenBufferOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tempStore);
        ArgumentNullException.ThrowIfNull(options);

        var capped = new CappedReadStream(source, options.MaxRequestBytes, leaveOpen: true);
        var pipe = PipeReader.Create(capped, new StreamPipeReaderOptions(
            bufferSize: Math.Max(options.PipeMinimumReadSize, 4096),
            minimumReadSize: Math.Max(options.PipeMinimumReadSize, 4096),
            leaveOpen: true));
        var state = new JsonReaderState(new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
        });
        var frames = new Stack<ObjectFrame>();
        string? currentProperty = null;
        var inAttachmentsArray = false;
        var attachmentCount = 0;
        long totalDecoded = 0;
        long currentTempBytes = 0;
        long peakTempBytes = 0;
        long peakRetainedTokenBytes = 0;

        try
        {
            while (true)
            {
                var readResult = await pipe.ReadAsync(cancellationToken);
                var buffer = readResult.Buffer;
                var json = new Utf8JsonReader(buffer, readResult.IsCompleted, state);

                try
                {
                    while (json.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        switch (json.TokenType)
                        {
                            case JsonTokenType.StartObject:
                                {
                                    var isAttachment = inAttachmentsArray && frames.Count == 1;
                                    frames.Push(new ObjectFrame(isAttachment));
                                    currentProperty = null;
                                    break;
                                }
                            case JsonTokenType.EndObject:
                                {
                                    if (frames.Count == 0)
                                    {
                                        throw new JsonException("Unexpected object terminator.");
                                    }

                                    var frame = frames.Pop();
                                    if (frame.IsAttachment)
                                    {
                                        ValidateAttachment(frame.Attachment!);
                                        attachmentCount++;
                                    }

                                    currentProperty = null;
                                    break;
                                }
                            case JsonTokenType.StartArray:
                                if (string.Equals(currentProperty, "attachments", StringComparison.Ordinal)
                                    && frames.Count == 1)
                                {
                                    inAttachmentsArray = true;
                                }

                                currentProperty = null;
                                break;
                            case JsonTokenType.EndArray:
                                if (inAttachmentsArray && frames.Count == 1)
                                {
                                    inAttachmentsArray = false;
                                }

                                currentProperty = null;
                                break;
                            case JsonTokenType.PropertyName:
                                {
                                    if (frames.Count == 0)
                                    {
                                        throw new JsonException("Property outside object.");
                                    }

                                    currentProperty = json.GetString()
                                        ?? throw new JsonException("Property name was null.");
                                    if (!frames.Peek().Properties.Add(currentProperty))
                                    {
                                        throw new JsonException("Duplicate JSON property: " + currentProperty);
                                    }

                                    break;
                                }
                            case JsonTokenType.String:
                                {
                                    if (frames.TryPeek(out var stringFrame) && stringFrame.IsAttachment)
                                    {
                                        var attachment = stringFrame.Attachment!;
                                        if (string.Equals(currentProperty, "content_sha256", StringComparison.Ordinal))
                                        {
                                            attachment.DeclaredSha256 = json.GetString();
                                        }
                                        else if (string.Equals(currentProperty, "content_base64", StringComparison.Ordinal))
                                        {
                                            if (attachment.Base64Seen)
                                            {
                                                throw new JsonException("Duplicate attachment content_base64.");
                                            }

                                            attachment.Base64Seen = true;
                                            var tokenBytes = json.HasValueSequence
                                                ? checked((int)json.ValueSequence.Length)
                                                : json.ValueSpan.Length;
                                            peakRetainedTokenBytes = Math.Max(peakRetainedTokenBytes, tokenBytes);
                                            var decode = DecodeBase64Token(
                                                ref json,
                                                tempStore,
                                                options,
                                                totalDecoded,
                                                cancellationToken);
                                            attachment.ActualLength = decode.DecodedBytes;
                                            attachment.ActualSha256 = decode.Sha256;
                                            attachment.TempPath = decode.TempPath;
                                            totalDecoded += decode.DecodedBytes;
                                            currentTempBytes += decode.DecodedBytes;
                                            peakTempBytes = Math.Max(peakTempBytes, currentTempBytes);
                                        }
                                    }

                                    currentProperty = null;
                                    break;
                                }
                            case JsonTokenType.Number:
                                if (frames.TryPeek(out var numberFrame)
                                    && numberFrame.IsAttachment
                                    && string.Equals(currentProperty, "byte_length", StringComparison.Ordinal))
                                {
                                    if (!json.TryGetInt64(out var declaredLength) || declaredLength < 0)
                                    {
                                        throw new JsonException("Invalid attachment byte_length.");
                                    }

                                    numberFrame.Attachment!.DeclaredLength = declaredLength;
                                }

                                currentProperty = null;
                                break;
                            default:
                                currentProperty = null;
                                break;
                        }
                    }
                }
                catch (JsonException)
                {
                    pipe.AdvanceTo(buffer.End);
                    throw;
                }

                state = json.CurrentState;
                var consumed = buffer.GetPosition(json.BytesConsumed);
                var retainedBytes = buffer.Slice(consumed).Length;
                peakRetainedTokenBytes = Math.Max(peakRetainedTokenBytes, retainedBytes);
                pipe.AdvanceTo(consumed, buffer.End);

                if (readResult.IsCompleted)
                {
                    if (retainedBytes != 0)
                    {
                        throw new JsonException("Incomplete JSON token at end of request.");
                    }

                    break;
                }
            }

            if (frames.Count != 0 || inAttachmentsArray)
            {
                throw new JsonException("Incomplete JSON structure.");
            }

            var peakBeforeCleanup = Math.Max(peakTempBytes, tempStore.GetOwnedBytes());
            tempStore.CleanupOwnedFiles();
            return new Spike526TokenBufferResult(
                fixtureId,
                capped.TotalBytesRead,
                totalDecoded,
                attachmentCount,
                peakRetainedTokenBytes,
                peakBeforeCleanup,
                tempStore.CountOwnedFiles() == 0);
        }
        finally
        {
            await pipe.CompleteAsync();
            tempStore.CleanupOwnedFiles();
        }
    }

    private static Base64DecodeResult DecodeBase64Token(
        ref Utf8JsonReader json,
        Spike526TempStore tempStore,
        Spike526TokenBufferOptions options,
        long totalDecodedBefore,
        CancellationToken cancellationToken)
    {
        var maximumEncodedBytes = json.HasValueSequence
            ? checked((int)json.ValueSequence.Length)
            : json.ValueSpan.Length;
        var encoded = ArrayPool<byte>.Shared.Rent(Math.Max(maximumEncodedBytes, 4));
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(options.DecodeBufferBytes, 4));
        var tempPath = tempStore.CreateFilePath();
        long fileDecoded = 0;

        try
        {
            var encodedLength = json.CopyString(encoded);
            if (encodedLength == 0 || encodedLength % 4 != 0)
            {
                throw new InvalidDataException("Attachment content_base64 has invalid length.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);

            var offset = 0;
            while (offset < encodedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = encodedLength - offset;
                var take = Math.Min(remaining, Math.Max(4, (options.DecodeBufferBytes / 3) * 4));
                if (take < remaining)
                {
                    take -= take % 4;
                }

                if (take <= 0)
                {
                    throw new InvalidDataException("Attachment Base64 chunk could not be aligned.");
                }

                var isFinal = offset + take == encodedLength;
                var status = Base64.DecodeFromUtf8(
                    encoded.AsSpan(offset, take),
                    decoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: isFinal);
                if (status != OperationStatus.Done || consumed != take)
                {
                    throw new InvalidDataException("Attachment content_base64 is invalid.");
                }

                if (fileDecoded + written > options.MaxPerFileDecodedBytes)
                {
                    throw new Spike526LimitException("Per-file decoded attachment limit exceeded.");
                }

                if (totalDecodedBefore + fileDecoded + written > options.MaxTotalDecodedBytes)
                {
                    throw new Spike526LimitException("Total decoded attachment limit exceeded.");
                }

                output.Write(decoded, 0, written);
                hash.AppendData(decoded.AsSpan(0, written));
                fileDecoded += written;
                offset += take;
            }

            output.Flush(flushToDisk: true);
            return new Base64DecodeResult(
                tempPath,
                fileDecoded,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded, clearArray: true);
            ArrayPool<byte>.Shared.Return(decoded, clearArray: true);
        }
    }

    private static void ValidateAttachment(AttachmentAccumulator attachment)
    {
        if (!attachment.Base64Seen
            || attachment.DeclaredLength is null
            || string.IsNullOrWhiteSpace(attachment.DeclaredSha256)
            || attachment.ActualLength is null
            || string.IsNullOrWhiteSpace(attachment.ActualSha256))
        {
            throw new JsonException("Attachment is missing required integrity fields.");
        }

        if (attachment.DeclaredLength.Value != attachment.ActualLength.Value)
        {
            throw new InvalidDataException("Attachment byte_length does not match decoded content.");
        }

        if (!string.Equals(
                attachment.DeclaredSha256,
                attachment.ActualSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Attachment content_sha256 does not match decoded content.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort inside the prototype. The caller's scoped cleanup is the final guard.
        }
    }

    private sealed class ObjectFrame(bool isAttachment)
    {
        internal bool IsAttachment { get; } = isAttachment;
        internal HashSet<string> Properties { get; } = new(StringComparer.Ordinal);
        internal AttachmentAccumulator? Attachment { get; } = isAttachment ? new AttachmentAccumulator() : null;
    }

    private sealed class AttachmentAccumulator
    {
        internal long? DeclaredLength { get; set; }
        internal string? DeclaredSha256 { get; set; }
        internal bool Base64Seen { get; set; }
        internal long? ActualLength { get; set; }
        internal string? ActualSha256 { get; set; }
        internal string? TempPath { get; set; }
    }

    private sealed record Base64DecodeResult(string TempPath, long DecodedBytes, string Sha256);

    private sealed class CappedReadStream(Stream inner, long maxBytes, bool leaveOpen) : Stream
    {
        private long _total;

        internal long TotalBytesRead => _total;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _total; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = LimitRequestedCount(count);
            var read = inner.Read(buffer, offset, allowed);
            Count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var allowed = LimitRequestedCount(buffer.Length);
            var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            Count(read);
            return read;
        }

        private int LimitRequestedCount(int requested)
        {
            var remainingIncludingSentinel = maxBytes - _total + 1;
            if (remainingIncludingSentinel <= 0)
            {
                throw new Spike526LimitException("Request byte limit exceeded.");
            }

            return (int)Math.Min(requested, remainingIncludingSentinel);
        }

        private void Count(int read)
        {
            _total += read;
            if (_total > maxBytes)
            {
                throw new Spike526LimitException("Request byte limit exceeded.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class Spike526LimitException(string message) : InvalidDataException(message);
