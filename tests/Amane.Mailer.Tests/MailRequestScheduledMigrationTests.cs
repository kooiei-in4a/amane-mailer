using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailRequestScheduledMigrationTests
{
    [Fact]
    public async Task Db_migrate_009_adds_scheduled_at_null_for_existing_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-009", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyMigrationsThrough(migrationDirectory, "008_delivery_events.sql");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration), migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            var requestId = Guid.NewGuid();
            await SeedMailRequestAsync(databasePath, requestId, ct);

            File.Copy(
                Path.Combine(GetCurrentMigrationDirectory(), "009_mail_request_scheduled_at.sql"),
                Path.Combine(migrationDirectory, "009_mail_request_scheduled_at.sql"));

            var applied = await runner.ApplyPendingAsync(ct);
            Assert.Contains("009_mail_request_scheduled_at.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT scheduled_at FROM mail_requests WHERE id = @Id;";
                verify.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                var value = await verify.ExecuteScalarAsync(ct);
                Assert.True(value is null or DBNull);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO mail_requests (
                        id, tenant_id, source_service, mail_request_id, purpose,
                        payload_json, payload_hash, subject, recipient_email,
                        status, attempt_count, max_attempts, scheduled_at,
                        accepted_at, created_at, updated_at)
                    VALUES (
                        @Id, @TenantId, 'migration-009-test', @MailRequestId, 'test',
                        '{}', @PayloadHash, 'subject', 'user@example.com',
                        0, 0, 3, @ScheduledAt,
                        @Now, @Now, @Now);
                    """;
                var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);
                var scheduledId = Guid.NewGuid();
                insert.Parameters.AddWithValue("@Id", scheduledId.ToString("D"));
                insert.Parameters.AddWithValue("@TenantId", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("@PayloadHash", new string('d', 64));
                insert.Parameters.AddWithValue("@ScheduledAt", now);
                insert.Parameters.AddWithValue("@Now", now);
                await insert.ExecuteNonQueryAsync(ct);
            }
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
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var request = connection.CreateCommand();
        request.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'migration-009-test', @MailRequestId, 'test',
                '{}', @PayloadHash, 'subject', 'user@example.com',
                0, 0, 3,
                @Now, @Now, @Now);
            """;
        request.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        request.Parameters.AddWithValue("@TenantId", Guid.NewGuid().ToString("D"));
        request.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        request.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
        request.Parameters.AddWithValue("@Now", now);
        await request.ExecuteNonQueryAsync(cancellationToken);
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
}
