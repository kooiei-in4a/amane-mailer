using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerCliOperationsTests
{
    [Fact]
    public async Task healthcheck_returns_unhealthy_without_migrated_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-health-missing-schema", Guid.NewGuid().ToString("N"));
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

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_healthy_after_migration_when_worker_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-health-migrated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["Mailer:Worker:Enabled"] = "false",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory);
            await runner.ApplyPendingAsync(ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(DbMigrateCommand.SuccessExitCode, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task T14_db_checkpoint_exits_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-checkpoint", Guid.NewGuid().ToString("N"));
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
            await runner.ApplyPendingAsync(ct);

            var exitCode = await MailerCliHost.RunDbCheckpointAsync(
                configuration,
                Console.Out,
                Console.Error,
                ct);

            Assert.Equal(DbCheckpointCommand.SuccessExitCode, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_stats_reports_sqlite_mail_request_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-stats", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var otherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

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
            await runner.ApplyPendingAsync(ct);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Queued,
                    updatedAt: now.AddMinutes(-45),
                    nextAttemptAt: null,
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Queued,
                    updatedAt: now.AddMinutes(-45),
                    nextAttemptAt: now.AddMinutes(10),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Processing,
                    updatedAt: now.AddMinutes(-40),
                    lockExpiresAt: now.AddMinutes(-5),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Delivered,
                    updatedAt: now.AddMinutes(-5),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Failed,
                    updatedAt: now.AddMinutes(-30),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.Failed,
                    updatedAt: now.AddMinutes(-120),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    tenantId,
                    MailRequestState.DeadLettered,
                    updatedAt: now.AddMinutes(-10),
                    cancellationToken: ct);
                await SeedMailRequestAsync(
                    connection,
                    otherTenantId,
                    MailRequestState.Queued,
                    updatedAt: now.AddMinutes(-60),
                    nextAttemptAt: null,
                    cancellationToken: ct);
            }

            var output = new StringWriter();
            var error = new StringWriter();
            var command = new DbStatsCommand(factory, () => now);
            var exitCode = await command.ExecuteAsync(
                [
                    "db",
                    "stats",
                    "--tenant-id",
                    tenantId.ToString("D"),
                    "--queued-stale-minutes",
                    "30",
                    "--failure-window-minutes",
                    "60",
                    "--stale-processing-minutes",
                    "30",
                ],
                output,
                error,
                ct);

            Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            var stats = ParseStats(output.ToString());
            Assert.Equal(tenantId.ToString("D"), stats["tenant_id"]);
            Assert.Equal("2", stats["status_queued"]);
            Assert.Equal("1", stats["status_processing"]);
            Assert.Equal("1", stats["status_delivered"]);
            Assert.Equal("2", stats["status_failed"]);
            Assert.Equal("1", stats["status_dead_lettered"]);
            Assert.Equal("1", stats["ready_backlog_count"]);
            Assert.Equal("2700", stats["oldest_queued_age_seconds"]);
            Assert.Equal("1", stats["queued_stale_count"]);
            Assert.Equal("1", stats["stale_processing_count"]);
            Assert.Equal("1", stats["expired_processing_count"]);
            Assert.Equal("1", stats["recent_failed_count"]);
            Assert.Equal("1", stats["recent_dead_lettered_count"]);
            Assert.Equal("2", stats["failed_total"]);
            Assert.Equal("1", stats["dead_lettered_total"]);
            Assert.Equal("3", stats["terminal_total"]);
            Assert.Equal("-1", stats["worker_heartbeat_age_seconds"]);
            Assert.Equal("-1", stats["sweep_heartbeat_age_seconds"]);

            var allOutput = new StringWriter();
            var allError = new StringWriter();
            exitCode = await command.ExecuteAsync(["db", "stats"], allOutput, allError, ct);

            Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);
            Assert.Equal(string.Empty, allError.ToString());

            var allStats = ParseStats(allOutput.ToString());
            Assert.Equal("all", allStats["tenant_id"]);
            Assert.Equal("3", allStats["status_queued"]);
            Assert.Equal("1", allStats["status_processing"]);
            Assert.Equal("1", allStats["status_delivered"]);
            Assert.Equal("2", allStats["status_failed"]);
            Assert.Equal("1", allStats["status_dead_lettered"]);
            Assert.Equal("2", allStats["ready_backlog_count"]);
            Assert.Equal("3600", allStats["oldest_queued_age_seconds"]);
            Assert.Equal("2", allStats["queued_stale_count"]);
            Assert.Equal("1", allStats["stale_processing_count"]);
            Assert.Equal("1", allStats["expired_processing_count"]);
            Assert.Equal("1", allStats["recent_failed_count"]);
            Assert.Equal("1", allStats["recent_dead_lettered_count"]);
            Assert.Equal("2", allStats["failed_total"]);
            Assert.Equal("1", allStats["dead_lettered_total"]);
            Assert.Equal("3", allStats["terminal_total"]);
            Assert.Equal("-1", allStats["worker_heartbeat_age_seconds"]);
            Assert.Equal("-1", allStats["sweep_heartbeat_age_seconds"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_stats_returns_unavailable_without_migrated_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-stats-missing-schema", Guid.NewGuid().ToString("N"));
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

            var output = new StringWriter();
            var error = new StringWriter();
            var command = new DbStatsCommand(new SqliteConnectionFactory(configuration));
            var exitCode = await command.ExecuteAsync(["db", "stats"], output, error, ct);

            Assert.Equal(DbStatsCommand.UnavailableExitCode, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("schema is not migrated", error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_request_state_reports_single_request_without_pii()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-request-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var mailRequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var queuedMailRequestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

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
            await runner.ApplyPendingAsync(ct);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                var requestId = await SeedMailRequestForStateAsync(
                    connection,
                    tenantId,
                    "example-service",
                    mailRequestId,
                    "mail-05a-acs-drill",
                    MailRequestState.Delivered,
                    attemptCount: 1,
                    updatedAt: now,
                    recipientEmail: "approved-recipient@example.com",
                    cancellationToken: ct);

                await SeedMailAttemptAsync(
                    connection,
                    requestId,
                    MailRequestState.Delivered,
                    "acs",
                    providerMessageId: "provider-message-id-must-not-print",
                    errorCode: null,
                    completedAt: now,
                    cancellationToken: ct);

                await SeedMailRequestForStateAsync(
                    connection,
                    tenantId,
                    "example-service",
                    queuedMailRequestId,
                    "mail-05a-smoke",
                    MailRequestState.Queued,
                    attemptCount: 0,
                    updatedAt: now,
                    recipientEmail: "queued-recipient@example.com",
                    cancellationToken: ct);
            }

            var output = new StringWriter();
            var error = new StringWriter();
            var command = new DbRequestStateCommand(factory);
            var exitCode = await command.ExecuteAsync(
                [
                    "db",
                    "request-state",
                    "--tenant-id",
                    tenantId.ToString("D"),
                    "--source-service",
                    "example-service",
                    "--mail-request-id",
                    mailRequestId.ToString("D"),
                ],
                output,
                error,
                ct);

            Assert.Equal(DbRequestStateCommand.SuccessExitCode, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            var state = ParseStats(output.ToString());
            Assert.Equal("true", state["found"]);
            Assert.Equal("mail-05a-acs-drill", state["purpose"]);
            Assert.Equal("delivered", state["status"]);
            Assert.Equal("2", state["status_code"]);
            Assert.Equal("1", state["attempt_count"]);
            Assert.Equal("1", state["attempt_rows"]);
            Assert.Equal("acs", state["last_provider"]);
            Assert.Equal("delivered", state["last_attempt_status"]);
            Assert.Equal("2", state["last_attempt_status_code"]);
            Assert.Equal("true", state["provider_message_id_present"]);
            Assert.Equal(string.Empty, state["last_error_code"]);
            Assert.DoesNotContain("approved-recipient@example.com", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("provider-message-id-must-not-print", output.ToString(), StringComparison.Ordinal);

            var queuedOutput = new StringWriter();
            var queuedExitCode = await command.ExecuteAsync(
                [
                    "db",
                    "request-state",
                    "--tenant-id",
                    tenantId.ToString("D"),
                    "--source-service",
                    "example-service",
                    "--mail-request-id",
                    queuedMailRequestId.ToString("D"),
                ],
                queuedOutput,
                Console.Error,
                ct);

            Assert.Equal(DbRequestStateCommand.SuccessExitCode, queuedExitCode);
            var queuedState = ParseStats(queuedOutput.ToString());
            Assert.Equal("queued", queuedState["status"]);
            Assert.Equal("0", queuedState["attempt_count"]);
            Assert.Equal("0", queuedState["attempt_rows"]);
            Assert.Equal(string.Empty, queuedState["last_attempt_status"]);
            Assert.Equal(string.Empty, queuedState["last_attempt_status_code"]);
            Assert.Equal("false", queuedState["provider_message_id_present"]);
            Assert.DoesNotContain("queued-recipient@example.com", queuedOutput.ToString(), StringComparison.Ordinal);

            var missingOutput = new StringWriter();
            var missingExitCode = await command.ExecuteAsync(
                [
                    "db",
                    "request-state",
                    "--tenant-id",
                    tenantId.ToString("D"),
                    "--source-service",
                    "example-service",
                    "--mail-request-id",
                    Guid.Parse("22222222-2222-2222-2222-222222222222").ToString("D"),
                ],
                missingOutput,
                Console.Error,
                ct);

            Assert.Equal(DbRequestStateCommand.SuccessExitCode, missingExitCode);
            Assert.Equal("false", ParseStats(missingOutput.ToString())["found"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_request_state_rejects_missing_required_options()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbRequestStateCommand(new SqliteConnectionFactory(configuration));
        var exitCode = await command.ExecuteAsync(
            ["db", "request-state", "--tenant-id", "00000000-0000-0000-0000-000000000101"],
            output,
            error,
            ct);

        Assert.Equal(DbRequestStateCommand.UsageErrorExitCode, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--tenant-id, --source-service, and --mail-request-id are required.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: dotnet Amane.Mailer.dll db request-state", error.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        error = new StringWriter();
        exitCode = await command.ExecuteAsync(
            ["db", "request-state", "--unknown", "value"],
            output,
            error,
            ct);

        Assert.Equal(DbRequestStateCommand.UsageErrorExitCode, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Unknown option: --unknown.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: dotnet Amane.Mailer.dll db request-state", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task db_stats_rejects_invalid_tenant_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbStatsCommand(new SqliteConnectionFactory(configuration));
        var exitCode = await command.ExecuteAsync(
            ["db", "stats", "--tenant-id", "not-a-uuid"],
            output,
            error,
            ct);

        Assert.Equal(DbStatsCommand.UsageErrorExitCode, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--tenant-id must be a UUID.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: dotnet Amane.Mailer.dll db stats", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task T15_db_backup_exits_zero_and_writes_copy()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backup", "mailer-backup.db");

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
            await runner.ApplyPendingAsync(ct);

            var exitCode = await MailerCliHost.RunDbBackupAsync(
                configuration,
                backupPath,
                Console.Out,
                Console.Error,
                ct);

            Assert.Equal(DbBackupCommand.SuccessExitCode, exitCode);
            Assert.True(File.Exists(backupPath));

            await using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
            await backupConnection.OpenAsync(ct);
            await using var command = backupConnection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'mail_requests';";
            var tableName = await command.ExecuteScalarAsync(ct);
            Assert.Equal("mail_requests", tableName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_backup_rejects_relative_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
            })
            .Build();

        var exitCode = await MailerCliHost.RunDbBackupAsync(
            configuration,
            "relative/backup.db",
            Console.Out,
            Console.Error,
            ct);

        Assert.Equal(DbBackupCommand.UsageErrorExitCode, exitCode);
    }

    [Fact]
    public async Task db_backup_rejects_active_database_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-backup-same-path", Guid.NewGuid().ToString("N"));
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
            await runner.ApplyPendingAsync(ct);

            var exitCode = await MailerCliHost.RunDbBackupAsync(
                configuration,
                databasePath,
                Console.Out,
                Console.Error,
                ct);

            Assert.Equal(DbBackupCommand.UsageErrorExitCode, exitCode);
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedMailRequestAsync(
        SqliteConnection connection,
        Guid tenantId,
        MailRequestState status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? lockExpiresAt = null)
    {
        var id = Guid.NewGuid();
        var lockToken = lockExpiresAt is null ? (object)DBNull.Value : Guid.NewGuid().ToString("D");
        var completedAt = status is MailRequestState.Delivered or MailRequestState.Failed or MailRequestState.DeadLettered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;
        var deliveredAt = status == MailRequestState.Delivered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;
        var failedAt = status is MailRequestState.Failed or MailRequestState.DeadLettered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, next_attempt_at,
                lock_token, lock_expires_at, delivered_at, failed_at,
                accepted_at, created_at, updated_at, completed_at)
            VALUES (
                @Id, @TenantId, 'stats-test', @MailRequestId, 'StatsTest',
                '{}', @PayloadHash, 'Stats test', 'recipient@example.com',
                @Status, 0, 3, @NextAttemptAt,
                @LockToken, @LockExpiresAt, @DeliveredAt, @FailedAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue(
            "@NextAttemptAt",
            nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
        command.Parameters.AddWithValue("@LockToken", lockToken);
        command.Parameters.AddWithValue(
            "@LockExpiresAt",
            lockExpiresAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(lockExpiresAt.Value));
        command.Parameters.AddWithValue("@DeliveredAt", deliveredAt);
        command.Parameters.AddWithValue("@FailedAt", failedAt);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CompletedAt", completedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> SeedMailRequestForStateAsync(
        SqliteConnection connection,
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        string purpose,
        MailRequestState status,
        int attemptCount,
        DateTimeOffset updatedAt,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("D");
        var completedAt = status is MailRequestState.Delivered or MailRequestState.Failed or MailRequestState.DeadLettered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;
        var deliveredAt = status == MailRequestState.Delivered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;
        var failedAt = status is MailRequestState.Failed or MailRequestState.DeadLettered
            ? SqliteTime.ToStorageUtc(updatedAt)
            : (object)DBNull.Value;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, next_attempt_at,
                lock_token, lock_expires_at, delivered_at, failed_at,
                accepted_at, created_at, updated_at, completed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                '{}', @PayloadHash, 'State test', @RecipientEmail,
                @Status, @AttemptCount, 3, NULL,
                NULL, NULL, @DeliveredAt, @FailedAt,
                @AcceptedAt, @CreatedAt, @UpdatedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@Purpose", purpose);
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@AttemptCount", attemptCount);
        command.Parameters.AddWithValue("@DeliveredAt", deliveredAt);
        command.Parameters.AddWithValue("@FailedAt", failedAt);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(updatedAt));
        command.Parameters.AddWithValue("@CompletedAt", completedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static async Task SeedMailAttemptAsync(
        SqliteConnection connection,
        string requestId,
        MailRequestState status,
        string provider,
        string? providerMessageId,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status, provider_message_id,
                error_code, error_message, retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, 1, @Provider, @Status, @ProviderMessageId,
                @ErrorCode, NULL, 0, @LockToken, @StartedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId);
        command.Parameters.AddWithValue("@Provider", provider);
        command.Parameters.AddWithValue("@Status", (int)status);
        command.Parameters.AddWithValue("@ProviderMessageId", providerMessageId is null ? DBNull.Value : providerMessageId);
        command.Parameters.AddWithValue("@ErrorCode", errorCode is null ? DBNull.Value : errorCode);
        command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(completedAt.AddSeconds(-1)));
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> ParseStats(string stats) =>
        stats
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', count: 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
}
