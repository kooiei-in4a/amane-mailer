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

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await TableExistsAsync(connection, "admin_audit_events", ct));

            var columns = await GetColumnNamesAsync(connection, "admin_audit_events", ct);
            string[] expectedColumns =
            [
                "id", "event_type", "actor", "occurred_at",
                "source_ip", "user_agent_summary",
                "target_type", "target_id", "field_name",
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
