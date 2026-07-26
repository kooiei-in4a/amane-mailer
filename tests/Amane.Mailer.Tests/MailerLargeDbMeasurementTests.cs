using System.Diagnostics;
using System.Globalization;
using System.Text;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Opt-in large-DB timing harness for metrics / retention / Admin queries (#288).
/// Skipped unless <c>AMANE_LARGE_DB_BENCH=1</c> so default CI stays fast.
/// Optional row count: <c>AMANE_LARGE_DB_BENCH_ROWS</c> (default 50000).
/// </summary>
public sealed class MailerLargeDbMeasurementTests
{
    private const string EnableEnvName = "AMANE_LARGE_DB_BENCH";
    private const string RowsEnvName = "AMANE_LARGE_DB_BENCH_ROWS";
    private const int DefaultRowCount = 50_000;

    [Fact]
    public async Task Large_db_metrics_retention_and_admin_queries_report_timings()
    {
        if (!IsBenchEnabled())
        {
            Assert.Skip(
                $"Set {EnableEnvName}=1 to run the large-DB measurement harness (#288). " +
                $"Optional {RowsEnvName} overrides row count (default {DefaultRowCount}).");
        }

        var ct = TestContext.Current.CancellationToken;
        var rowCount = ResolveRowCount();
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-large-db-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory);
            _ = await runner.ApplyPendingAsync(ct);

            var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
            var seedElapsed = await SeedLargeAsync(databasePath, rowCount, now, ct);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                await using var analyze = connection.CreateCommand();
                analyze.CommandText = "ANALYZE;";
                await analyze.ExecuteNonQueryAsync(ct);
            }

            var statsReader = new MailerDbStatsReader(factory);
            var repository = MailRequestRepository.CreateStandalone(factory);

            var statsSw = Stopwatch.StartNew();
            var stats = await statsReader.LoadStatsAsync(MailerDbStatsQuery.ForAllTenants(), now, ct);
            statsSw.Stop();
            Assert.NotNull(stats);
            Assert.Equal(rowCount, stats.QueuedCount
                + stats.ProcessingCount
                + stats.DeliveredCount
                + stats.FailedCount
                + stats.DeadLetteredCount
                + await CountCancelledAsync(databasePath, ct));

            var adminListSw = Stopwatch.StartNew();
            var adminPage = await repository.ListForAdminAsync(
                new AdminMailRequestListQuery
                {
                    Status = (int)MailRequestState.Queued,
                    PageSize = 50,
                },
                ct);
            adminListSw.Stop();
            Assert.Equal(50, adminPage.Items.Count);

            var deadLetterCountSw = Stopwatch.StartNew();
            var deadLetterCount = await repository.CountDeadLettersForAdminAsync(
                allowedTenantIds: null,
                cancellationToken: ct);
            deadLetterCountSw.Stop();
            Assert.True(deadLetterCount > 0);

            var retentionCutoff = now.AddDays(-90);
            var retentionSw = Stopwatch.StartNew();
            var deleted = await repository.DeleteExpiredCompletedAsync(retentionCutoff, batchSize: 100, ct);
            retentionSw.Stop();
            Assert.Equal(100, deleted);

            var retentionPlan = await ExplainRetentionSelectAsync(databasePath, retentionCutoff, ct);
            var metricsPlan = await ExplainMetricsAggregateAsync(databasePath, ct);

            var dbBytes = new FileInfo(databasePath).Length;
            var summary = new StringBuilder();
            summary.AppendLine("=== Amane Mailer large-DB measurement (#288) ===");
            summary.AppendLine(CultureInfo.InvariantCulture, $"captured_at_utc={DateTimeOffset.UtcNow:O}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"os={Environment.OSVersion}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"framework={Environment.Version}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"row_count={rowCount}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"db_bytes={dbBytes}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"seed_ms={seedElapsed.TotalMilliseconds:F1}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"LoadStatsAsync_ms={statsSw.Elapsed.TotalMilliseconds:F1}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"ListForAdminAsync_queued_ms={adminListSw.Elapsed.TotalMilliseconds:F1}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"CountDeadLettersForAdminAsync_ms={deadLetterCountSw.Elapsed.TotalMilliseconds:F1}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"DeleteExpiredCompletedAsync_batch100_ms={retentionSw.Elapsed.TotalMilliseconds:F1}");
            summary.AppendLine(CultureInfo.InvariantCulture, $"DeleteExpiredCompletedAsync_deleted={deleted}");
            summary.AppendLine("explain_metrics_aggregate:");
            foreach (var line in metricsPlan)
                summary.AppendLine(CultureInfo.InvariantCulture, $"  - {line}");
            summary.AppendLine("explain_retention_select:");
            foreach (var line in retentionPlan)
                summary.AppendLine(CultureInfo.InvariantCulture, $"  - {line}");
            summary.AppendLine("notes=no recipient/subject/body values logged; synthetic seed only");

            var summaryText = summary.ToString();

            // Visible with diagnostic verbosity; also persist for ops-doc capture.
            Console.WriteLine(summaryText);
            var outPath = Environment.GetEnvironmentVariable("AMANE_LARGE_DB_BENCH_OUT");
            if (string.IsNullOrWhiteSpace(outPath))
            {
                outPath = Path.Combine(Path.GetTempPath(), "amane-large-db-bench-last.txt");
            }

            var outDirectory = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDirectory))
                Directory.CreateDirectory(outDirectory);
            await File.WriteAllTextAsync(outPath, summaryText, ct);
            Console.WriteLine($"summary_written={outPath}");

            // Sanity: Admin list with indexes should stay well under aggregate scan cost
            // on this seeded shape. Soft ceiling keeps the harness from silently regressing
            // into multi-second list latency without failing flaky CI machines.
            Assert.True(
                adminListSw.Elapsed < TimeSpan.FromSeconds(2),
                $"Admin queued list exceeded soft ceiling: {adminListSw.Elapsed.TotalMilliseconds:F1} ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsBenchEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableEnvName);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveRowCount()
    {
        var raw = Environment.GetEnvironmentVariable(RowsEnvName);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultRowCount;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows)
            || rows < 1_000
            || rows > 500_000)
        {
            throw new InvalidOperationException(
                $"{RowsEnvName} must be an integer between 1000 and 500000.");
        }

        return rows;
    }

    private static async Task<TimeSpan> SeedLargeAsync(
        string databasePath,
        int rowCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at, completed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'LargeDbBench',
                '{}', @PayloadHash, @Subject, @RecipientEmail,
                @Status, 0, 3,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
            """;

        var id = command.Parameters.Add("@Id", SqliteType.Text);
        var tenantId = command.Parameters.Add("@TenantId", SqliteType.Text);
        var sourceService = command.Parameters.Add("@SourceService", SqliteType.Text);
        var mailRequestId = command.Parameters.Add("@MailRequestId", SqliteType.Text);
        var payloadHash = command.Parameters.Add("@PayloadHash", SqliteType.Text);
        var subject = command.Parameters.Add("@Subject", SqliteType.Text);
        var recipientEmail = command.Parameters.Add("@RecipientEmail", SqliteType.Text);
        var status = command.Parameters.Add("@Status", SqliteType.Integer);
        var acceptedAt = command.Parameters.Add("@AcceptedAt", SqliteType.Text);
        var createdAt = command.Parameters.Add("@CreatedAt", SqliteType.Text);
        var updatedAt = command.Parameters.Add("@UpdatedAt", SqliteType.Text);
        var completedAt = command.Parameters.Add("@CompletedAt", SqliteType.Text);

        tenantId.Value = "00000000-0000-0000-0000-000000000101";
        sourceService.Value = "example-service";
        payloadHash.Value = new string('0', 64);

        // Mix: ~40% terminal expired (retention candidates), ~20% terminal recent,
        // ~25% queued, ~10% processing, ~5% dead-lettered recent for Admin count.
        for (var i = 0; i < rowCount; i++)
        {
            var bucket = i % 100;
            MailRequestState state;
            DateTimeOffset updated;
            string? completed;

            if (bucket < 40)
            {
                state = (MailRequestState)(2 + (i % 4)); // Delivered..Cancelled
                if (state > MailRequestState.Cancelled)
                    state = MailRequestState.Delivered;
                updated = now.AddMinutes(-i);
                completed = SqliteTime.ToStorageUtc(now.AddDays(-120).AddSeconds(-i));
            }
            else if (bucket < 60)
            {
                state = i % 2 == 0 ? MailRequestState.Delivered : MailRequestState.Failed;
                updated = now.AddMinutes(-i);
                completed = SqliteTime.ToStorageUtc(now.AddDays(-1).AddSeconds(-i));
            }
            else if (bucket < 85)
            {
                state = MailRequestState.Queued;
                updated = now.AddMinutes(-i);
                completed = null;
            }
            else if (bucket < 95)
            {
                state = MailRequestState.Processing;
                updated = now.AddMinutes(-i);
                completed = null;
            }
            else
            {
                state = MailRequestState.DeadLettered;
                updated = now.AddMinutes(-i);
                completed = SqliteTime.ToStorageUtc(now.AddDays(-2).AddSeconds(-i));
            }

            id.Value = Guid.NewGuid().ToString("D");
            mailRequestId.Value = Guid.NewGuid().ToString("D");
            subject.Value = $"bench-{i}";
            recipientEmail.Value = $"bench-{i}@example.com";
            status.Value = (int)state;
            var stamp = SqliteTime.ToStorageUtc(updated);
            acceptedAt.Value = stamp;
            createdAt.Value = stamp;
            updatedAt.Value = stamp;
            completedAt.Value = completed is null ? DBNull.Value : completed;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        sw.Stop();
        return sw.Elapsed;
    }

    private static async Task<long> CountCancelledAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_requests WHERE status = @Status;";
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Cancelled);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ExplainRetentionSelectAsync(
        string databasePath,
        DateTimeOffset completedBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT id, tenant_id, source_service, mail_request_id
            FROM mail_requests
            WHERE status IN (@DeliveredStatus, @FailedStatus, @DeadLetteredStatus, @CancelledStatus)
              AND completed_at IS NOT NULL
              AND completed_at < @CompletedBefore
            ORDER BY completed_at ASC, id ASC
            LIMIT @BatchSize;
            """;
        command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@CancelledStatus", (int)MailRequestState.Cancelled);
        command.Parameters.AddWithValue("@CompletedBefore", SqliteTime.ToStorageUtc(completedBefore));
        command.Parameters.AddWithValue("@BatchSize", 100);

        return await ReadPlanAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ExplainMetricsAggregateAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            WITH filtered AS (
                SELECT status, next_attempt_at, scheduled_at, lock_expires_at, updated_at
                FROM mail_requests
                WHERE 1 = 1
            )
            SELECT COUNT(*) FROM filtered;
            """;

        return await ReadPlanAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadPlanAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            details.Add(reader.GetString(3));
        return details;
    }
}
