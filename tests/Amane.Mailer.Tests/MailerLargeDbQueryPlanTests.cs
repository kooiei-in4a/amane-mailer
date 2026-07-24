using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Always-on EXPLAIN QUERY PLAN coverage for metrics aggregate and retention
/// SELECT shapes (#288). Large timing seeds live in
/// <see cref="MailerLargeDbMeasurementTests"/> and stay opt-in.
/// </summary>
public sealed class MailerLargeDbQueryPlanTests
{
    [Fact]
    public async Task Metrics_aggregate_and_retention_select_plans_are_documented()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-large-db-plans", Guid.NewGuid().ToString("N"));
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

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            await SeedPlannerDataAsync(connection, ct);

            var metricsPlan = await ExplainAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                WITH filtered AS (
                    SELECT status, next_attempt_at, scheduled_at, lock_expires_at, updated_at
                    FROM mail_requests
                    WHERE 1 = 1
                )
                SELECT
                    COUNT(CASE WHEN status = 0 THEN 1 END),
                    COUNT(CASE WHEN status = 1 THEN 1 END),
                    COUNT(CASE WHEN status = 2 THEN 1 END),
                    COUNT(CASE WHEN status = 3 THEN 1 END),
                    COUNT(CASE WHEN status = 4 THEN 1 END)
                FROM filtered;
                """,
                ct);

            // Full-table multi-status aggregate: planner scans mail_requests.
            // Partial/worker indexes cannot cover every COUNT(CASE ...) branch.
            Assert.Contains(
                metricsPlan,
                detail => detail.Contains("SCAN", StringComparison.OrdinalIgnoreCase)
                    && detail.Contains("mail_requests", StringComparison.Ordinal));

            var retentionPlan = await ExplainAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id, tenant_id, source_service, mail_request_id
                FROM mail_requests
                WHERE status IN (2, 3, 4, 5)
                  AND completed_at IS NOT NULL
                  AND completed_at < '2026-01-01T00:00:00.0000000Z'
                ORDER BY completed_at ASC, id ASC
                LIMIT 100;
                """,
                ct);

            // Multi-status retention select: no completed_at-ordered index covering
            // Delivered/Failed/DeadLettered/Cancelled. Planner may SEARCH via
            // idx_mail_requests_status_updated then TEMP B-TREE for ORDER BY, or SCAN
            // on small tables. Dead-letter-only index must not be treated as sufficient.
            Assert.DoesNotContain(
                retentionPlan,
                detail => detail.Contains("idx_mail_requests_deadletter_completed", StringComparison.Ordinal));
            Assert.Contains(
                retentionPlan,
                detail => detail.Contains("SCAN", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("SEARCH", StringComparison.OrdinalIgnoreCase));

            var deadLetterCountPlan = await ExplainAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT COUNT(*)
                FROM mail_requests
                WHERE status = 4;
                """,
                ct);

            Assert.Contains(
                deadLetterCountPlan,
                detail =>
                    detail.Contains("idx_mail_requests_deadletter_completed", StringComparison.Ordinal)
                    || detail.Contains("idx_mail_requests_status_updated", StringComparison.Ordinal));

            var adminStatusListPlan = await ExplainAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_requests
                WHERE status = 0
                ORDER BY updated_at DESC, id DESC
                LIMIT 51;
                """,
                ct);

            Assert.Contains(
                adminStatusListPlan,
                detail => detail.Contains("idx_mail_requests_status_updated", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedPlannerDataAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 80; i++)
        {
            var status = i switch
            {
                < 15 => MailRequestState.Delivered,
                < 30 => MailRequestState.Failed,
                < 45 => MailRequestState.DeadLettered,
                < 55 => MailRequestState.Cancelled,
                < 70 => MailRequestState.Queued,
                _ => MailRequestState.Processing,
            };
            var updatedAt = now.AddMinutes(-i);
            var completedAt = status is MailRequestState.Delivered
                or MailRequestState.Failed
                or MailRequestState.DeadLettered
                or MailRequestState.Cancelled
                ? SqliteTime.ToStorageUtc(updatedAt.AddDays(-100))
                : null;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts,
                    accepted_at, created_at, updated_at, completed_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, 'LargeDbPlan',
                    '{}', @PayloadHash, @Subject, @RecipientEmail,
                    @Status, 0, 3,
                    @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
                """;
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@TenantId", "00000000-0000-0000-0000-000000000101");
            command.Parameters.AddWithValue("@SourceService", "example-service");
            command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
            command.Parameters.AddWithValue("@Subject", $"Large DB plan {i}");
            command.Parameters.AddWithValue("@RecipientEmail", $"recipient-{i}@example.com");
            command.Parameters.AddWithValue("@Status", (int)status);
            command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@CompletedAt", completedAt is null ? DBNull.Value : completedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var analyze = connection.CreateCommand();
        analyze.CommandText = "ANALYZE;";
        await analyze.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var details = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            details.Add(reader.GetString(3));
        }

        Assert.NotEmpty(details);
        return details;
    }
}
