using System.Security.Cryptography;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Qualification;

public sealed class G583MigrationSchemaContractFixtureTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedMigrationSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["014_mail_request_delivery_unknown_status.sql"] = "db4ffe02069d0b899958f191a4de5cbae4327ea1cee14892cda268f61edb31f6",
            ["015_attachment_spool_and_submission_evidence.sql"] = "f5cd7f7b885bab55fb77ff630c6ee42ce2094e22d5fe1615123d2cc4b2fdd7f8",
            ["016_recipient_persistence_and_plain_submission_evidence.sql"] = "c95e5b5c2d7b3ac52ab7ce6afc591f0a0e97aac59a151b9476bfca751dccc0c5",
            ["017_recipient_delivery_events.sql"] = "4e7f15fb61bc1bccd0386fecb3d267c23b0ce6044c87f7999c8fe1a74a1b2bdb",
            ["018_admin_user_capabilities.sql"] = "94af8770dec3a0e0ec925ce6a1946ad73f51f564e7137f2d82934b4fffb7f471",
        };

    [Fact]
    public async Task Qualification_fixture_G583_MIG_03_ci_auto_emits_value_free_schema_contract_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-g583-mig03-schema-contract",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var migrationDirectory = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
            AssertMigrationSourceIdentity(migrationDirectory);

            var databasePath = Path.Combine(root, "mailer.db");
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath};Pooling=False",
                    })
                    .Build());
            var runner = new SqlMigrationRunner(factory);
            var applied = await runner.ApplyPendingAsync(cancellationToken);
            Assert.Equal(18, applied.Count);
            Assert.Equal("018_admin_user_capabilities.sql", applied[^1]);
            Assert.True(await runner.IsCurrentSchemaReadyAsync(cancellationToken));

            await using var connection = await factory.OpenConnectionAsync(cancellationToken);
            await AssertSchemaAllowlistAsync(connection, cancellationToken);

            // The existing canary uses transient values internally but never writes them to the
            // structured result produced below. This fixture exposes only a PASS/FAIL conclusion.
            await new global::Amane.Mailer.Tests.MailRequestWorkerLogPiiCanaryTests()
                .Worker_terminal_failure_logs_do_not_contain_pii_or_secret_canaries();

            var observations = new Dictionary<string, object>
            {
                ["migration014To018SchemaResult"] = "pass",
                ["constraintsResult"] = "pass",
                ["indexesResult"] = "pass",
                ["piiValueCanaryResult"] = "pass",
                ["valueFreeEvidenceCanaryResult"] = "pass",
            };
            Assert.True(QualificationFixtureResultWriter.WriteIfRequested(
                typeof(G583MigrationSchemaContractFixtureTests),
                nameof(Qualification_fixture_G583_MIG_03_ci_auto_emits_value_free_schema_contract_result),
                "g583-mig03-ci-auto-schema-contract",
                "G583-MIG-03",
                "ci-auto",
                passed: true,
                observations));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertMigrationSourceIdentity(string migrationDirectory)
    {
        Assert.Equal(ExpectedMigrationSha256.Keys.OrderBy(name => name),
            Directory.GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null && string.CompareOrdinal(name, "014_") >= 0)
                .Cast<string>()
                .OrderBy(name => name));

        foreach (var (name, expectedHash) in ExpectedMigrationSha256)
        {
            var bytes = File.ReadAllBytes(Path.Combine(migrationDirectory, name));
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }
    }

    private static async Task AssertSchemaAllowlistAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = await ReadNamesAsync(connection, "table", cancellationToken);
        Assert.Subset(
            tables,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "mail_requests",
                "mail_attempts",
                "mail_request_attachments",
                "mail_attachment_submissions",
                "mailer_maintenance_leases",
                "mail_request_recipients",
                "mail_plain_submissions",
                "recipient_delivery_events",
                "admin_user_capabilities",
            });

        var indexes = await ReadNamesAsync(connection, "index", cancellationToken);
        var expectedIndexes = new HashSet<string>(StringComparer.Ordinal)
        {
            "idx_mail_requests_queued_due",
            "idx_mail_requests_processing_expired",
            "idx_mail_requests_status_updated",
            "idx_mail_requests_tenant_status_updated",
            "idx_mail_requests_source_service_status_updated",
            "idx_mail_requests_deadletter_completed",
            "idx_mail_attempts_request_id_attempt",
            "ix_mail_attempts_provider_message_id",
            "idx_mail_requests_delivery_unknown_completed",
            "idx_mail_attachment_submissions_state",
            "idx_mail_request_recipients_request",
            "idx_mail_plain_submissions_state",
            "ix_recipient_delivery_events_request_occurred",
            "ix_recipient_delivery_events_provider_message",
        };
        Assert.Subset(indexes, expectedIndexes);

        var mailRequests = await ReadTableSqlAsync(connection, "mail_requests", cancellationToken);
        Assert.Contains("CHECK (status IN (0, 1, 2, 3, 4, 5, 6))", mailRequests, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT uq_mail_requests_idempotency", mailRequests, StringComparison.Ordinal);
        Assert.Contains("attachment_count", mailRequests, StringComparison.Ordinal);
        Assert.Contains("delivery_unknown_at", mailRequests, StringComparison.Ordinal);

        var recipients = await ReadTableSqlAsync(connection, "mail_request_recipients", cancellationToken);
        Assert.Contains("recipient_role IN (0, 1, 2)", recipients, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (request_id, recipient_role, ordinal)", recipients, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT uq_mail_request_recipients_request_address_key", recipients, StringComparison.Ordinal);

        var plainSubmissions = await ReadTableSqlAsync(connection, "mail_plain_submissions", cancellationToken);
        Assert.Contains("evidence_state IN (0, 1, 2, 3, 4)", plainSubmissions, StringComparison.Ordinal);
        Assert.Contains("evidence_origin IN (0, 1)", plainSubmissions, StringComparison.Ordinal);

        var deliveryEvents = await ReadTableSqlAsync(connection, "recipient_delivery_events", cancellationToken);
        Assert.Contains("recipient_ordinal >= 0 AND recipient_ordinal <= 9", deliveryEvents, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT uq_recipient_delivery_events_provider_event", deliveryEvents, StringComparison.Ordinal);

        var capabilities = await ReadTableSqlAsync(connection, "admin_user_capabilities", cancellationToken);
        Assert.Contains("PRIMARY KEY (admin_user_id, capability)", capabilities, StringComparison.Ordinal);
        Assert.Contains("CHECK (length(capability) > 0)", capabilities, StringComparison.Ordinal);
    }

    private static async Task<HashSet<string>> ReadNamesAsync(
        SqliteConnection connection,
        string type,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = @type;";
        command.Parameters.AddWithValue("@type", type);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<string> ReadTableSqlAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table;";
        command.Parameters.AddWithValue("@table", table);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<string>(result);
    }
}
