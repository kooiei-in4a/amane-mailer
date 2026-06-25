using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminIndexMigrationTests
{
    [Fact]
    public async Task Db_migrate_applies_admin_indexes_and_plans_use_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-indexes", Guid.NewGuid().ToString("N"));
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
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.Contains("003_admin_indexes.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            var indexes = await GetIndexNamesAsync(connection, ct);
            Assert.Contains("idx_mail_requests_status_updated", indexes);
            Assert.Contains("idx_mail_requests_tenant_status_updated", indexes);
            Assert.Contains("idx_mail_requests_source_service_status_updated", indexes);
            Assert.Contains("idx_mail_requests_deadletter_completed", indexes);
            Assert.Contains("idx_mail_attempts_request_id_attempt", indexes);
            Assert.DoesNotContain("ix_mail_attempts_request_id", indexes);

            await SeedPlannerDataAsync(connection, ct);

            await AssertPlanUsesIndexAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_requests
                WHERE status = 0
                ORDER BY updated_at DESC
                LIMIT 50;
                """,
                "idx_mail_requests_status_updated",
                ct);

            await AssertPlanUsesIndexAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_requests
                WHERE tenant_id = '00000000-0000-0000-0000-000000000101'
                  AND status = 0
                ORDER BY updated_at DESC
                LIMIT 50;
                """,
                "idx_mail_requests_tenant_status_updated",
                ct);

            await AssertPlanUsesIndexAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_requests
                WHERE source_service = 'example-service'
                  AND status = 0
                ORDER BY updated_at DESC
                LIMIT 50;
                """,
                "idx_mail_requests_source_service_status_updated",
                ct);

            await AssertPlanUsesIndexAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_requests
                WHERE status = 4
                ORDER BY completed_at DESC
                LIMIT 50;
                """,
                "idx_mail_requests_deadletter_completed",
                ct);

            await AssertPlanUsesIndexAsync(
                connection,
                """
                EXPLAIN QUERY PLAN
                SELECT id
                FROM mail_attempts
                WHERE request_id = '00000000-0000-0000-0000-000000000201'
                ORDER BY attempt_number ASC
                LIMIT 50;
                """,
                "idx_mail_attempts_request_id_attempt",
                ct);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<IReadOnlySet<string>> GetIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static async Task SeedPlannerDataAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        string? requestForAttempts = null;

        for (var i = 0; i < 80; i++)
        {
            var requestId = Guid.NewGuid().ToString("D");
            requestForAttempts ??= requestId;
            var status = i < 20 ? MailRequestState.DeadLettered : MailRequestState.Queued;
            var updatedAt = now.AddMinutes(-i);
            var completedAt = status == MailRequestState.DeadLettered
                ? SqliteTime.ToStorageUtc(updatedAt)
                : null;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts,
                    accepted_at, created_at, updated_at, completed_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, 'AdminIndexPlan',
                    '{}', @PayloadHash, @Subject, @RecipientEmail,
                    @Status, 0, 3,
                    @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
                """;
            command.Parameters.AddWithValue("@Id", requestId);
            command.Parameters.AddWithValue("@TenantId", "00000000-0000-0000-0000-000000000101");
            command.Parameters.AddWithValue("@SourceService", i % 2 == 0 ? "example-service" : "other");
            command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
            command.Parameters.AddWithValue("@Subject", $"Admin index plan {i}");
            command.Parameters.AddWithValue("@RecipientEmail", $"recipient-{i}@example.com");
            command.Parameters.AddWithValue("@Status", (int)status);
            command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
            command.Parameters.AddWithValue("@CompletedAt", completedAt is null ? DBNull.Value : completedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 1; i <= 5; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_attempts (
                    request_id, attempt_number, provider, status,
                    retryable, lock_token, started_at, completed_at)
                VALUES (
                    @RequestId, @AttemptNumber, 'mailpit', 3,
                    0, @LockToken, @StartedAt, @CompletedAt);
                """;
            command.Parameters.AddWithValue("@RequestId", requestForAttempts);
            command.Parameters.AddWithValue("@AttemptNumber", i);
            command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-i)));
            command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(now.AddMinutes(-i).AddSeconds(10)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var analyze = connection.CreateCommand();
        analyze.CommandText = "ANALYZE;";
        await analyze.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertPlanUsesIndexAsync(
        SqliteConnection connection,
        string sql,
        string indexName,
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

        Assert.Contains(
            details,
            detail => detail.Contains(indexName, StringComparison.Ordinal));
    }
}
