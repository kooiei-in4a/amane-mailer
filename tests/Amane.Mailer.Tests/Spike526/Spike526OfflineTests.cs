using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Spike526.Probe;

namespace Amane.Mailer.Tests.Spike526;

public sealed class Spike526OfflineTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void Consumer_envelope_matrix_is_deterministic_and_value_free()
    {
        foreach (var fixtureId in Spike526FixtureFactory.FixtureIds)
        {
            var fixture = Spike526FixtureFactory.Create(fixtureId);
            var first = Spike526FixtureFactory.SerializeRequest(fixture);
            var second = Spike526FixtureFactory.SerializeRequest(Spike526FixtureFactory.Create(fixtureId));
            var measurement = Spike526FixtureFactory.MeasureConsumerEnvelope(fixture);

            Assert.Equal(first, second);
            Assert.Equal(first.LongLength, measurement.ConsumerEnvelopeBytes);
            Assert.Equal(fixture.DecodedBinaryBytes, measurement.DecodedBinaryBytes);
            Assert.DoesNotContain("content_base64", JsonSerializer.Serialize(measurement));
        }
    }

    [Theory]
    [InlineData("F00")]
    [InlineData("F01")]
    [InlineData("F03")]
    [InlineData("F04")]
    public async Task Acs_capture_uses_real_sdk_serialization_and_estimator_does_not_underestimate(string fixtureId)
    {
        var fixture = Spike526FixtureFactory.Create(fixtureId);

        var capture = await Spike526AcsEnvelopeCapture.CaptureAsync(
            fixture,
            TestContext.Current.CancellationToken);
        var estimate = Spike526AcsEnvelopeCapture.EstimateUpperBound(fixture);

        Assert.True(capture.ResponseParsed);
        Assert.Equal("Running", capture.OperationStatus);
        Assert.True(capture.RequestBodyBytes > 0);
        Assert.Equal(64, capture.RequestBodySha256.Length);
        Assert.True(estimate >= capture.RequestBodyBytes,
            $"Estimator underflow for {fixtureId}: estimate={estimate}, actual={capture.RequestBodyBytes}");
    }

    [Fact]
    public async Task Token_buffer_candidate_processes_F03_and_cleans_all_temp_files()
    {
        var fixture = Spike526FixtureFactory.Create("F03");
        var bytes = Spike526FixtureFactory.SerializeRequest(fixture);
        var root = CreateTempRoot();
        try
        {
            var store = new Spike526TempStore(root);
            await using var stream = new MemoryStream(bytes, writable: false);

            var result = await Spike526TokenBufferProcessor.ProcessAsync(
                fixture.Id,
                stream,
                store,
                Options(bytes.LongLength),
                TestContext.Current.CancellationToken);

            Assert.Equal(fixture.AttachmentCount, result.AttachmentCount);
            Assert.Equal(fixture.DecodedBinaryBytes, result.DecodedBinaryBytes);
            Assert.True(result.PeakRetainedTokenBytes > 1 * MiB);
            Assert.Equal(fixture.DecodedBinaryBytes, result.PeakTempBytes);
            Assert.True(result.CleanupComplete);
            Assert.Equal(0, store.CountOwnedFiles());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Token_buffer_candidate_preserves_strict_utf8_and_segment_boundaries()
    {
        var fixture = CreateSmallFixture();
        var bytes = Spike526FixtureFactory.SerializeRequest(fixture);
        var root = CreateTempRoot();
        try
        {
            var store = new Spike526TempStore(root);
            await using var segmented = new SegmentedReadStream(bytes, maximumRead: 7);

            var result = await Spike526TokenBufferProcessor.ProcessAsync(
                fixture.Id,
                segmented,
                store,
                Options(bytes.LongLength),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.AttachmentCount);
            Assert.Equal(257, result.DecodedBinaryBytes);
            Assert.True(result.CleanupComplete);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_invalid_base64_and_cleans_temp()
    {
        await AssertRejectedAsync(
            Spike526FixtureFactory.SerializeRequest(Spike526FixtureFactory.Create("F08")),
            static exception => Assert.IsType<InvalidDataException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_declared_length_or_digest_mismatch()
    {
        await AssertRejectedAsync(
            Spike526FixtureFactory.SerializeRequest(Spike526FixtureFactory.Create("F07")),
            static exception => Assert.IsType<InvalidDataException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_invalid_utf8()
    {
        byte[] invalid = [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}'];
        await AssertRejectedAsync(invalid, static exception => Assert.IsType<JsonException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_truncated_json()
    {
        var valid = Spike526FixtureFactory.SerializeRequest(Spike526FixtureFactory.Create("F01"));
        await AssertRejectedAsync(valid[..^1], static exception => Assert.IsType<JsonException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_duplicate_root_property()
    {
        var json = """
            {"tenant_id":"a","tenant_id":"b","attachments":[]}
            """;
        await AssertRejectedAsync(Encoding.UTF8.GetBytes(json), static exception => Assert.IsType<JsonException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_duplicate_attachment_property()
    {
        var content = Convert.ToBase64String([1, 2, 3]);
        var digest = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();
        var json = $$"""
            {"attachments":[{"file_name":"a.bin","file_name":"b.bin","content_type":"application/octet-stream","byte_length":3,"content_sha256":"{{digest}}","content_base64":"{{content}}"}]}
            """;
        await AssertRejectedAsync(Encoding.UTF8.GetBytes(json), static exception => Assert.IsType<JsonException>(exception));
    }

    [Fact]
    public async Task Token_buffer_candidate_rejects_request_per_file_and_total_limits_before_unbounded_growth()
    {
        var fixture = Spike526FixtureFactory.Create("F01");
        var bytes = Spike526FixtureFactory.SerializeRequest(fixture);

        await AssertRejectedAsync(
            bytes,
            static exception => Assert.IsType<Spike526LimitException>(exception),
            new Spike526TokenBufferOptions(bytes.LongLength - 1, 5 * MiB, 8 * MiB));
        await AssertRejectedAsync(
            bytes,
            static exception => Assert.IsType<Spike526LimitException>(exception),
            new Spike526TokenBufferOptions(bytes.LongLength + 1, 512 * 1024, 8 * MiB));
        await AssertRejectedAsync(
            bytes,
            static exception => Assert.IsType<Spike526LimitException>(exception),
            new Spike526TokenBufferOptions(bytes.LongLength + 1, 5 * MiB, 512 * 1024));
    }

    [Fact]
    public async Task Restart_cleanup_removes_only_spike_owned_orphans()
    {
        var root = CreateTempRoot();
        var parent = Directory.GetParent(root)!.FullName;
        var outside = Path.Combine(parent, "spike526-outside-" + Guid.NewGuid().ToString("N") + ".keep");
        await File.WriteAllTextAsync(outside, "outside", TestContext.Current.CancellationToken);
        try
        {
            var orphan = await RunProbeAsync("orphan-create", root);
            Assert.Equal(73, orphan.ExitCode);
            Assert.True(new Spike526TempStore(root).CountOwnedFiles() > 0);

            var cleanup = await RunProbeAsync("cleanup", root, outside);
            Assert.Equal(0, cleanup.ExitCode);
            Assert.True(File.Exists(outside));
            Assert.Equal(0, new Spike526TempStore(root).CountOwnedFiles());
        }
        finally
        {
            DeleteRoot(root);
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    private static async Task AssertRejectedAsync(
        byte[] bytes,
        Action<Exception> assertion,
        Spike526TokenBufferOptions? options = null)
    {
        var root = CreateTempRoot();
        try
        {
            var store = new Spike526TempStore(root);
            await using var stream = new MemoryStream(bytes, writable: false);
            var exception = await Record.ExceptionAsync(() => Spike526TokenBufferProcessor.ProcessAsync(
                "negative",
                stream,
                store,
                options ?? Options(bytes.LongLength),
                TestContext.Current.CancellationToken));

            Assert.NotNull(exception);
            assertion(exception);
            Assert.Equal(0, store.CountOwnedFiles());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static Spike526Fixture CreateSmallFixture()
    {
        var bytes = Enumerable.Range(0, 257).Select(static index => (byte)(index % 251)).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var request = new Spike526Request
        {
            TenantId = "00000000-0000-0000-0000-000000000526",
            SourceService = "spike526-segmented",
            MailRequestId = "00000000-0000-0000-0000-000000000001",
            Purpose = "Spike526SegmentedRead",
            To = [new Spike526Recipient { Email = "to@example.invalid" }],
            Cc = [],
            Bcc = [],
            Subject = "境界",
            TextBody = "segment-boundary",
            HtmlBody = "<p>segment-boundary</p>",
            Attachments =
            [
                new Spike526Attachment
                {
                    FileName = "請求書.txt",
                    ContentType = "text/plain",
                    ByteLength = bytes.Length,
                    ContentSha256 = digest,
                    ContentBase64 = Convert.ToBase64String(bytes),
                },
            ],
            PayloadHash = new string('0', 64),
        };
        return new Spike526Fixture("SEGMENTED", request, bytes.Length, 1, true, true);
    }

    private static Spike526TokenBufferOptions Options(long requestBytes) =>
        new(Math.Max(requestBytes + 1, 16 * MiB), 5 * MiB, 8 * MiB, PipeMinimumReadSize: 4096);

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "amane-mailer-spike526-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunProbeAsync(params string[] arguments)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Amane.Mailer.Spike526.Probe.dll");
        Assert.True(File.Exists(assemblyPath), "Spike526 probe assembly was not copied to test output.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Spike526 probe process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class SegmentedReadStream(byte[] bytes, int maximumRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = bytes.Length - _position;
            var take = Math.Min(Math.Min(count, maximumRead), remaining);
            bytes.AsSpan(_position, take).CopyTo(buffer.AsSpan(offset, take));
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = bytes.Length - _position;
            var take = Math.Min(Math.Min(buffer.Length, maximumRead), remaining);
            bytes.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
