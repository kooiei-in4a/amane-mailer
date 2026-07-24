using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminAuditMigrationTests
{
    [Fact]
    public async Task Db_migrate_creates_admin_audit_events_table_and_index()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-audit", Guid.NewGuid().ToString("N"));
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

            Assert.Contains("004_admin_audit_events.sql", applied);
            Assert.Contains("010_admin_audit_events_tenant_id.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await TableExistsAsync(connection, "admin_audit_events", ct));

            var columns = await GetColumnNamesAsync(connection, "admin_audit_events", ct);
            string[] expectedColumns =
            [
                "id", "event_type", "actor", "occurred_at",
                "source_ip", "user_agent_summary",
                "target_type", "target_id", "tenant_id", "field_name",
                "result", "error_code",
            ];
            foreach (var column in expectedColumns)
            {
                Assert.Contains(column, columns);
            }

            // PII must not be a column of the audit table (ADR 0013 D-08).
            string[] forbiddenColumns =
            [
                "html_body", "text_body", "body", "recipient_email", "recipient",
                "subject", "metadata_json", "metadata", "payload_json",
            ];
            foreach (var column in forbiddenColumns)
            {
                Assert.DoesNotContain(column, columns);
            }

            var indexes = await GetIndexNamesAsync(connection, ct);
            Assert.Contains("idx_admin_audit_events_occurred_at", indexes);
            Assert.Contains("idx_admin_audit_events_tenant_id", indexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_is_idempotent_for_admin_audit_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-audit", Guid.NewGuid().ToString("N"));
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

            var firstRun = await runner.ApplyPendingAsync(ct);
            var secondRun = await runner.ApplyPendingAsync(ct);

            Assert.Contains("004_admin_audit_events.sql", firstRun);
            Assert.DoesNotContain("004_admin_audit_events.sql", secondRun);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_010_backfills_tenant_id_from_live_mail_requests_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-audit-010", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyMigrationsThrough(migrationDirectory, "009_mail_request_scheduled_at.sql");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration), migrationDirectory);
            var appliedBefore = await runner.ApplyPendingAsync(ct);
            Assert.Contains("009_mail_request_scheduled_at.sql", appliedBefore);
            Assert.DoesNotContain("010_admin_audit_events_tenant_id.sql", appliedBefore);

            var liveTenantId = Guid.Parse("00000000-0000-0000-0000-000000000401");
            var liveMailRequestId = Guid.NewGuid();
            var purgedMailRequestId = Guid.NewGuid();
            await SeedMailRequestAsync(databasePath, liveMailRequestId, liveTenantId, ct);
            await SeedPreTenantIdAuditEventAsync(
                databasePath,
                actor: "backfill-live",
                targetId: liveMailRequestId,
                ct);
            await SeedPreTenantIdAuditEventAsync(
                databasePath,
                actor: "backfill-purged",
                targetId: purgedMailRequestId,
                ct);

            File.Copy(
                Path.Combine(GetCurrentMigrationDirectory(), "010_admin_audit_events_tenant_id.sql"),
                Path.Combine(migrationDirectory, "010_admin_audit_events_tenant_id.sql"));

            var applied = await runner.ApplyPendingAsync(ct);
            Assert.Contains("010_admin_audit_events_tenant_id.sql", applied);

            Assert.Equal(
                liveTenantId.ToString("D"),
                await ReadAuditTenantIdAsync(databasePath, "backfill-live", ct));
            Assert.Null(await ReadAuditTenantIdAsync(databasePath, "backfill-purged", ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedMailRequestAsync(
        string databasePath,
        Guid requestId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'migration-010-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                0, 0, 3,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('e', 64));
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedPreTenantIdAuditEventAsync(
        string databasePath,
        string actor,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Pre-010 schema: no tenant_id column yet.
        command.CommandText = """
            INSERT INTO admin_audit_events (
                event_type, actor, occurred_at,
                source_ip, user_agent_summary,
                target_type, target_id, field_name,
                result, error_code)
            VALUES (
                @EventType, @Actor, @OccurredAt,
                NULL, NULL,
                @TargetType, @TargetId, NULL,
                @Result, NULL);
            """;
        command.Parameters.AddWithValue("@EventType", "mail_request.manual_cancel_requested");
        command.Parameters.AddWithValue("@Actor", actor);
        command.Parameters.AddWithValue("@OccurredAt", now);
        command.Parameters.AddWithValue("@TargetType", "mail_request");
        command.Parameters.AddWithValue("@TargetId", targetId.ToString("D"));
        command.Parameters.AddWithValue("@Result", "success");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadAuditTenantIdAsync(
        string databasePath,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tenant_id
            FROM admin_audit_events
            WHERE actor = @Actor
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Actor", actor);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : (string)result;
    }

    private static void CopyMigrationsThrough(string destination, string lastVersion)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(GetCurrentMigrationDirectory(), "*.sql", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file)!;
            File.Copy(file, Path.Combine(destination, fileName));
            if (string.Equals(fileName, lastVersion, StringComparison.Ordinal))
                break;
        }
    }

    private static string GetCurrentMigrationDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @Name LIMIT 1;";
        command.Parameters.AddWithValue("@Name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<IReadOnlySet<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
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
}
