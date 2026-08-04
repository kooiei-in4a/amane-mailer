using System.Diagnostics;
using System.Text.Json;
using Amane.Mailer.Spike526.Probe;

const int UsageError = 2;
const int ExpectedOrphanExit = 73;

if (args.Length == 0)
{
    WriteUsage();
    return UsageError;
}

try
{
    switch (args[0])
    {
        case "warmup" when args.Length == 3:
            await RunOnceAsync(args[1], args[2], MakeRoot("warmup"));
            return 0;
        case "measure" when args.Length is 3 or 4:
            {
                var concurrency = args.Length == 4 ? int.Parse(args[3]) : 1;
                if (concurrency is not (1 or 2))
                {
                    throw new ArgumentOutOfRangeException(nameof(args), concurrency, "Concurrency must be 1 or 2.");
                }

                var result = await ProfileAsync(args[1], args[2], concurrency);
                Console.WriteLine(JsonSerializer.Serialize(result, Spike526JsonContext.Default.Spike526ProbeResult));
                return 0;
            }
        case "orphan-create" when args.Length == 2:
            {
                var store = new Spike526TempStore(args[1]);
                var path = store.CreateFilePath();
                await File.WriteAllBytesAsync(path, new byte[4096]);
                Console.Error.WriteLine("spike526 orphan fixture created; exiting without cleanup");
                Environment.Exit(ExpectedOrphanExit);
                return ExpectedOrphanExit;
            }
        case "cleanup" when args.Length is 2 or 3:
            {
                var store = new Spike526TempStore(args[1]);
                var report = store.CleanupAndReport(args.Length == 3 ? args[2] : null);
                Console.WriteLine(JsonSerializer.Serialize(
                    report,
                    Spike526JsonContext.Default.Spike526CleanupResult));
                return report.RemainingFiles == 0 && report.OutsideFilePreserved ? 0 : 1;
            }
        case "self-check" when args.Length == 1:
            {
                var fixture = Spike526FixtureFactory.Create("F01");
                var bytes = Spike526FixtureFactory.SerializeRequest(fixture);
                var root = MakeRoot("self-check");
                var store = new Spike526TempStore(root);
                await using var stream = new MemoryStream(bytes, writable: false);
                var result = await Spike526TokenBufferProcessor.ProcessAsync(
                    fixture.Id,
                    stream,
                    store,
                    DefaultOptions(bytes.LongLength));
                Directory.Delete(root, recursive: true);
                return result.CleanupComplete ? 0 : 1;
            }
        default:
            WriteUsage();
            return UsageError;
    }
}
catch (Exception ex)
{
    // Exception messages (IOException, UnauthorizedAccessException, etc.) can
    // contain absolute temp-root or input paths. Emit only a fixed
    // classification so probe stderr/CI logs never carry a private path.
    Console.Error.WriteLine("Spike526 probe failed: " + ClassifyFailure(ex));
    return 1;
}

static string ClassifyFailure(Exception ex) => ex switch
{
    ArgumentException => "INVALID_INPUT",
    JsonException => "INVALID_INPUT",
    InvalidDataException => "INVALID_INPUT",
    OperationCanceledException => "CANCELLED",
    OutOfMemoryException => "OUT_OF_MEMORY",
    IOException => "IO_ERROR",
    UnauthorizedAccessException => "IO_ERROR",
    _ => "UNEXPECTED",
};

// Single-process profiling: an in-process warm-up pass (JIT, assembly load, SDK
// init) runs and is discarded first, so the measured pass below excludes
// process-startup cost that a separate `dotnet run` invocation could never
// exclude. `concurrency` requests then run as concurrent Tasks inside the same
// process, sharing the same GC/heap, so the peak sampled during that window
// reflects genuine concurrent load rather than two independent cold processes.
static async Task<Spike526ProbeResult> ProfileAsync(string fixtureId, string mode, int concurrency)
{
    await RunOnceAsync(fixtureId, mode, MakeRoot("profile-warmup"));

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

    var peakHeap = heapBefore;
    using var samplerCts = new CancellationTokenSource();
    var samplerTask = SampleHeapPeakAsync(value => peakHeap = value, samplerCts.Token);

    var stopwatch = Stopwatch.StartNew();
    var runs = await Task.WhenAll(Enumerable.Range(0, concurrency)
        .Select(index => RunOnceAsync(fixtureId, mode, MakeRoot("profile-" + index))));
    stopwatch.Stop();

    await samplerCts.CancelAsync();
    await samplerTask;

    var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
    var heapAfter = GC.GetTotalMemory(forceFullCollection: false);

    var process = Process.GetCurrentProcess();
    process.Refresh();

    var first = runs[0];
    var cleanupComplete = runs.All(static run => run.CleanupComplete);
    var totalPeakTemp = runs.Sum(static run => run.PeakTempBytes);

    return new Spike526ProbeResult(
        fixtureId,
        mode,
        concurrency,
        first.DecodedBinaryBytes,
        first.ConsumerEnvelopeBytes,
        first.AcsEnvelopeBytes,
        Math.Max(0, allocatedAfter - allocatedBefore),
        heapBefore,
        Math.Max(peakHeap, heapAfter),
        heapAfter,
        stopwatch.ElapsedMilliseconds,
        process.PeakWorkingSet64,
        totalPeakTemp,
        cleanupComplete,
        ProviderInvoked: false,
        Result: cleanupComplete ? "PASS" : "HOLD");
}

static async Task SampleHeapPeakAsync(Action<long> reportPeak, CancellationToken cancellationToken)
{
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            reportPeak(GC.GetTotalMemory(forceFullCollection: false));
            await Task.Delay(2, cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Expected: the sampler is stopped once the measured pass completes.
    }
}

static async Task<Spike526RunOnceResult> RunOnceAsync(string fixtureId, string mode, string root)
{
    var fixture = Spike526FixtureFactory.Create(fixtureId);
    var consumer = Spike526FixtureFactory.MeasureConsumerEnvelope(fixture);
    var requestBytes = Spike526FixtureFactory.SerializeRequest(fixture);
    var store = new Spike526TempStore(root);

    try
    {
        long peakTemp = 0;
        bool cleanupComplete;
        if (string.Equals(mode, "buffered", StringComparison.Ordinal))
        {
            foreach (var attachment in fixture.Request.Attachments)
            {
                _ = Spike526FixtureFactory.DecodeAttachment(attachment);
            }

            using var document = JsonDocument.Parse(requestBytes);
            _ = document.RootElement.GetProperty("tenant_id").GetString();
            cleanupComplete = true;
        }
        else if (string.Equals(mode, "token", StringComparison.Ordinal))
        {
            await using var stream = new MemoryStream(requestBytes, writable: false);
            var tokenResult = await Spike526TokenBufferProcessor.ProcessAsync(
                fixture.Id,
                stream,
                store,
                DefaultOptions(requestBytes.LongLength));
            peakTemp = tokenResult.PeakTempBytes;
            cleanupComplete = tokenResult.CleanupComplete;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Mode must be buffered or token.");
        }

        var acsBytes = 0L;
        if (fixture.ExpectedValidBase64 && fixture.ExpectedDeclaredMetadataMatch)
        {
            var capture = await Spike526AcsEnvelopeCapture.CaptureAsync(fixture);
            acsBytes = capture.RequestBodyBytes;
        }

        return new Spike526RunOnceResult(
            fixture.DecodedBinaryBytes,
            consumer.ConsumerEnvelopeBytes,
            acsBytes,
            peakTemp,
            cleanupComplete);
    }
    finally
    {
        // Runs even on failure (including OutOfMemoryException under a
        // constrained container-memory profile) so a failed measurement never
        // leaves a Spike526-owned temp root behind.
        store.CleanupOwnedFiles();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static string MakeRoot(string label) =>
    Path.Combine(Path.GetTempPath(), "amane-mailer-spike526-probe", label + "-" + Guid.NewGuid().ToString("N"));

static Spike526TokenBufferOptions DefaultOptions(long requestBytes) =>
    new(
        MaxRequestBytes: Math.Max(requestBytes + 1, 16 * 1024 * 1024),
        MaxPerFileDecodedBytes: 5 * 1024 * 1024,
        MaxTotalDecodedBytes: 8 * 1024 * 1024);

static void WriteUsage() => Console.Error.WriteLine(
    "Usage: Amane.Mailer.Spike526.Probe " +
    "<warmup|measure> <F00..F08> <buffered|token> [concurrency: measure only, 1|2] | " +
    "orphan-create <temp-root> | cleanup <temp-root> [outside-file] | self-check");

internal sealed record Spike526RunOnceResult(
    long DecodedBinaryBytes,
    long ConsumerEnvelopeBytes,
    long AcsEnvelopeBytes,
    long PeakTempBytes,
    bool CleanupComplete);
