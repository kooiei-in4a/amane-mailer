using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailRequestCancelledStatusMigrationTests
{
    [Fact]
    public async Task Db_migrate_007_preserves_mail_attempts_and_allows_cancelled_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-007", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyMigrationsThrough(migrationDirectory, "006_admin_users_and_tenant_scopes.sql");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration), migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            var requestId = Guid.NewGuid();
            const int attemptId = 42;
            await SeedMailRequestWithAttemptAsync(databasePath, requestId, attemptId, ct);

            File.Copy(
                Path.Combine(GetCurrentMigrationDirectory(), "007_mail_request_cancelled_status.sql"),
                Path.Combine(migrationDirectory, "007_mail_request_cancelled_status.sql"));

            var applied = await runner.ApplyPendingAsync(ct);
            Assert.Contains("007_mail_request_cancelled_status.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.Equal(1, await CountAttemptsAsync(connection, requestId, ct));
            Assert.Equal(attemptId, await ReadAttemptIdAsync(connection, requestId, ct));

            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE mail_requests
                    SET status = 5
                    WHERE id = @Id;
                    """;
                update.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                await update.ExecuteNonQueryAsync(ct);
            }

            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT status FROM mail_requests WHERE id = @Id;";
                verify.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                var status = await verify.ExecuteScalarAsync(ct);
                Assert.Equal(5L, status);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedMailRequestWithAttemptAsync(
        string databasePath,
        Guid requestId,
        int attemptId,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(SqliteTime.UtcNow);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using (var request = connection.CreateCommand())
        {
            request.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts,
                    accepted_at, created_at, updated_at)
                VALUES (
                    @Id, @TenantId, 'migration-007-test', @MailRequestId, 'test',
                    '{}', @PayloadHash, 'subject', 'user@example.com',
                    4, 3, 3,
                    @Now, @Now, @Now);
                """;
            request.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            request.Parameters.AddWithValue("@TenantId", Guid.NewGuid().ToString("D"));
            request.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
            request.Parameters.AddWithValue("@PayloadHash", new string('c', 64));
            request.Parameters.AddWithValue("@Now", now);
            await request.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var attempt = connection.CreateCommand())
        {
            attempt.CommandText = """
                INSERT INTO mail_attempts (
                    id, request_id, attempt_number, provider, status,
                    error_code, error_message, retryable, lock_token,
                    started_at, completed_at)
                VALUES (
                    @AttemptId, @RequestId, 1, 'mailpit', 4,
                    'TEST_ERROR', 'provider failed', 1, @LockToken,
                    @Now, @Now);
                """;
            attempt.Parameters.AddWithValue("@AttemptId", attemptId);
            attempt.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            attempt.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            attempt.Parameters.AddWithValue("@Now", now);
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> CountAttemptsAsync(
        SqliteConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_attempts WHERE request_id = @RequestId;";
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadAttemptIdAsync(
        SqliteConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM mail_attempts WHERE request_id = @RequestId LIMIT 1;";
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
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
