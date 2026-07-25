using System.Diagnostics;
using System.Globalization;
using System.Text;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Webhooks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Webhooks;

/// <summary>
/// Synthetic head-of-line (HOL) measurement for sequential webhook delivery (#361).
/// Mirrors <c>WebhookDeliveryWorker</c>: claim one → simulated endpoint latency → finalize.
/// Parallel simulation is comparison-only; product code stays sequential.
/// </summary>
public sealed class WebhookDeliverySequentialBenchmarkTests
{
    private static readonly Guid SlowTenantId = Guid.Parse("00000000-0000-0000-0000-000000000361");
    private static readonly Guid FastTenantId = Guid.Parse("00000000-0000-0000-0000-000000000362");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    private const int SlowEventCount = 8;
    private const int FastEventCount = 8;
    private const int SlowLatencyMs = 25;
    private const int FastLatencyMs = 2;
    private const int ParallelConcurrency = 4;

    [Fact]
    public async Task Sequential_delivery_quantifies_cross_tenant_head_of_line_blocking()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BenchDatabase.CreateAsync(ct);
        var repository = new DeliveryEventRepository(db.Factory);
        var baseTime = new DateTimeOffset(2026, 7, 25, 6, 0, 0, TimeSpan.Zero);

        await SeedPendingMixAsync(db.ConnectionString, baseTime, ct);

        var sequential = await DrainAsync(
            repository,
            concurrency: 1,
            ct);

        await ResetPendingMixAsync(db.ConnectionString, baseTime, ct);

        var parallel = await DrainAsync(
            repository,
            concurrency: ParallelConcurrency,
            ct);

        var summary = BuildSummary(sequential, parallel);
        Console.WriteLine(summary);

        var outPath = Environment.GetEnvironmentVariable("AMANE_WEBHOOK_HOL_BENCH_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = Path.Combine(Path.GetTempPath(), "amane-webhook-hol-bench-last.txt");
        }

        var outDirectory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDirectory))
        {
            Directory.CreateDirectory(outDirectory);
        }

        await File.WriteAllTextAsync(outPath, summary, ct);
        Console.WriteLine($"summary_written={outPath}");

        // FIFO claim + sequential await: fast tenant cannot start until all earlier slow
        // events finish. Lower bound uses 70% of nominal slow latency to absorb OS jitter.
        var expectedMinFirstFastMs = SlowEventCount * SlowLatencyMs * 0.7;
        Assert.True(
            sequential.FirstFastStartMs >= expectedMinFirstFastMs,
            $"Sequential first-fast start ({sequential.FirstFastStartMs:F1} ms) should be "
            + $"blocked behind {SlowEventCount} slow events (min {expectedMinFirstFastMs:F1} ms).");

        Assert.True(
            sequential.FirstFastStartMs > parallel.FirstFastStartMs,
            $"Sequential HOL ({sequential.FirstFastStartMs:F1} ms) should exceed parallel "
            + $"concurrency={ParallelConcurrency} first-fast start ({parallel.FirstFastStartMs:F1} ms).");

        Assert.Equal(SlowEventCount + FastEventCount, sequential.DeliveredCount);
        Assert.Equal(SlowEventCount + FastEventCount, parallel.DeliveredCount);
    }

    private static string BuildSummary(DrainResult sequential, DrainResult parallel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Amane Mailer webhook sequential HOL measurement (#361) ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"captured_at_utc={DateTimeOffset.UtcNow:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"os={Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"framework={Environment.Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"slow_events={SlowEventCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"fast_events={FastEventCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"slow_latency_ms={SlowLatencyMs}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"fast_latency_ms={FastLatencyMs}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"seed_order=slow_then_fast_by_created_at");
        sb.AppendLine(CultureInfo.InvariantCulture, $"claim_order=ORDER BY created_at ASC (production)");
        sb.AppendLine("mode=sequential (mirrors WebhookDeliveryWorker)");
        AppendDrainLines(sb, "sequential", sequential);
        sb.AppendLine(CultureInfo.InvariantCulture, $"mode=parallel_simulation concurrency={ParallelConcurrency}");
        AppendDrainLines(sb, "parallel", parallel);
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"hol_ratio_first_fast_seq_over_par={sequential.FirstFastStartMs / Math.Max(1.0, parallel.FirstFastStartMs):F2}");
        sb.AppendLine(
            "notes=synthetic tenant ids and endpoint delays only; no recipient/subject/body; "
            + "no production HTTP; parallel path is comparison-only");
        return sb.ToString();
    }

    private static void AppendDrainLines(StringBuilder sb, string prefix, DrainResult result)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_delivered={result.DeliveredCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_total_ms={result.TotalMs:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_first_fast_start_ms={result.FirstFastStartMs:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_fast_complete_p50_ms={Percentile(result.FastCompleteMs, 0.50):F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_fast_complete_p95_ms={Percentile(result.FastCompleteMs, 0.95):F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_claim_ms_sum={result.ClaimMsSum:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_finalize_ms_sum={result.FinalizeMsSum:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{prefix}_max_inflight={result.MaxInflight}");
    }

    private static async Task<DrainResult> DrainAsync(
        DeliveryEventRepository repository,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var lease = LeaseDuration;
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var statsGate = new object();
        var inflight = 0;
        var maxInflight = 0;
        var claimMsSum = 0.0;
        var finalizeMsSum = 0.0;
        var delivered = 0;
        var fastCompleteMs = new List<double>(FastEventCount);
        double? firstFastStartMs = null;
        var drainWatch = Stopwatch.StartNew();

        async Task WorkerLoopAsync()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await gate.WaitAsync(cancellationToken);
                var entered = false;
                try
                {
                    var claimWatch = Stopwatch.StartNew();
                    var now = DateTimeOffset.UtcNow;
                    var row = await repository.TryClaimOneAsync(now, lease, cancellationToken);
                    claimWatch.Stop();

                    lock (statsGate)
                    {
                        claimMsSum += claimWatch.Elapsed.TotalMilliseconds;
                    }

                    if (row is null)
                    {
                        return;
                    }

                    entered = true;
                    lock (statsGate)
                    {
                        inflight++;
                        if (inflight > maxInflight)
                        {
                            maxInflight = inflight;
                        }

                        if (row.TenantId == FastTenantId)
                        {
                            var startMs = drainWatch.Elapsed.TotalMilliseconds;
                            if (firstFastStartMs is null || startMs < firstFastStartMs.Value)
                            {
                                firstFastStartMs = startMs;
                            }
                        }
                    }

                    var latencyMs = row.TenantId == SlowTenantId ? SlowLatencyMs : FastLatencyMs;
                    await Task.Delay(latencyMs, cancellationToken);

                    var finalizeWatch = Stopwatch.StartNew();
                    var completedAt = DateTimeOffset.UtcNow;
                    var ok = await repository.FinalizeAsync(
                        row.Id,
                        row.LockToken,
                        completedAt,
                        DeliveryEventFinalizeOutcome.Delivered,
                        nextAttemptAt: null,
                        lastErrorCode: null,
                        cancellationToken);
                    finalizeWatch.Stop();
                    Assert.True(ok);

                    lock (statsGate)
                    {
                        finalizeMsSum += finalizeWatch.Elapsed.TotalMilliseconds;
                        delivered++;
                        if (row.TenantId == FastTenantId)
                        {
                            fastCompleteMs.Add(drainWatch.Elapsed.TotalMilliseconds);
                        }
                    }
                }
                finally
                {
                    if (entered)
                    {
                        lock (statsGate)
                        {
                            inflight--;
                        }
                    }

                    gate.Release();
                }
            }
        }

        // Restart workers while claimable work remains. With concurrency>1 a worker may
        // observe an empty select while siblings still hold in-flight deliveries.
        while (true)
        {
            var workers = new Task[concurrency];
            for (var i = 0; i < concurrency; i++)
            {
                workers[i] = WorkerLoopAsync();
            }

            await Task.WhenAll(workers);
            if (!await repository.HasPendingWorkAsync(DateTimeOffset.UtcNow, cancellationToken))
            {
                break;
            }
        }

        drainWatch.Stop();

        double[] fastSorted;
        lock (statsGate)
        {
            fastSorted = fastCompleteMs.OrderBy(x => x).ToArray();
        }

        return new DrainResult(
            DeliveredCount: delivered,
            TotalMs: drainWatch.Elapsed.TotalMilliseconds,
            FirstFastStartMs: firstFastStartMs ?? -1,
            FastCompleteMs: fastSorted,
            ClaimMsSum: claimMsSum,
            FinalizeMsSum: finalizeMsSum,
            MaxInflight: maxInflight);
    }

    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return -1;
        }

        var index = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        index = Math.Clamp(index, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }

    private static async Task SeedPendingMixAsync(
        string connectionString,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var created = baseTime;
        for (var i = 0; i < SlowEventCount; i++)
        {
            created = created.AddMilliseconds(1);
            await InsertPendingAsync(connection, SlowTenantId, created, cancellationToken);
        }

        for (var i = 0; i < FastEventCount; i++)
        {
            created = created.AddMilliseconds(1);
            await InsertPendingAsync(connection, FastTenantId, created, cancellationToken);
        }
    }

    private static async Task ResetPendingMixAsync(
        string connectionString,
        DateTimeOffset baseTime,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM delivery_events;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await SeedPendingMixAsync(connectionString, baseTime, cancellationToken);
    }

    private static async Task InsertPendingAsync(
        SqliteConnection connection,
        Guid tenantId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.CreateVersion7(createdAt);
        var mailRequestId = Guid.CreateVersion7(createdAt);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_events (
                id, tenant_id, source_service, mail_request_id, event_type, payload_json,
                status, attempt_count, max_attempts, next_attempt_at,
                lock_token, lock_expires_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'webhook-hol-bench', @MailRequestId, 'delivered',
                @PayloadJson, @Status, 0, 3, NULL,
                NULL, NULL, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@Id", eventId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue(
            "@PayloadJson",
            """{"event_id":"00000000-0000-0000-0000-000000000361"}""");
        command.Parameters.AddWithValue("@Status", (int)DeliveryEventState.Pending);
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record DrainResult(
        int DeliveredCount,
        double TotalMs,
        double FirstFastStartMs,
        double[] FastCompleteMs,
        double ClaimMsSum,
        double FinalizeMsSum,
        int MaxInflight);

    private sealed class BenchDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private BenchDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<BenchDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-webhook-hol-bench",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new BenchDatabase(root, factory, connectionString);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
