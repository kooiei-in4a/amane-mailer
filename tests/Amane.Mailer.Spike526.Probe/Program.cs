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
            await MeasureAsync(args[1], args[2], emitResult: false);
            return 0;
        case "measure" when args.Length == 3:
            await MeasureAsync(args[1], args[2], emitResult: true);
            return 0;
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
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-spike526-self-check", Guid.NewGuid().ToString("N"));
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
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 1;
}

static async Task MeasureAsync(string fixtureId, string mode, bool emitResult)
{
    var fixture = Spike526FixtureFactory.Create(fixtureId);
    var consumer = Spike526FixtureFactory.MeasureConsumerEnvelope(fixture);
    var requestBytes = Spike526FixtureFactory.SerializeRequest(fixture);
    var root = Path.Combine(Path.GetTempPath(), "amane-mailer-spike526-probe", Guid.NewGuid().ToString("N"));
    var store = new Spike526TempStore(root);

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var process = Process.GetCurrentProcess();
    process.Refresh();

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

    var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
    process.Refresh();
    var result = new Spike526ProbeResult(
        fixture.Id,
        mode,
        fixture.DecodedBinaryBytes,
        consumer.ConsumerEnvelopeBytes,
        acsBytes,
        Math.Max(0, allocatedAfter - allocatedBefore),
        process.PeakWorkingSet64,
        peakTemp,
        cleanupComplete,
        ProviderInvoked: false,
        Result: cleanupComplete ? "PASS" : "HOLD");

    store.CleanupOwnedFiles();
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    if (emitResult)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, Spike526JsonContext.Default.Spike526ProbeResult));
    }
}

static Spike526TokenBufferOptions DefaultOptions(long requestBytes) =>
    new(
        MaxRequestBytes: Math.Max(requestBytes + 1, 16 * 1024 * 1024),
        MaxPerFileDecodedBytes: 5 * 1024 * 1024,
        MaxTotalDecodedBytes: 8 * 1024 * 1024);

static void WriteUsage() => Console.Error.WriteLine(
    "Usage: Amane.Mailer.Spike526.Probe " +
    "<warmup|measure> <F00..F08> <buffered|token> | " +
    "orphan-create <temp-root> | cleanup <temp-root> [outside-file] | self-check");
