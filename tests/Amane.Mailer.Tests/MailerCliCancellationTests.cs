using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerCliCancellationTests
{
    [Fact]
    public void Attach_subscribes_and_Dispose_unsubscribes_CancelKeyPress_handler()
    {
        using var cancellation = MailerCliCancellation.Attach();
        Assert.True(cancellation.IsHandlerSubscribedForTests);

        cancellation.Dispose();
        Assert.False(cancellation.IsHandlerSubscribedForTests);

        // Second dispose is a no-op.
        cancellation.Dispose();
        Assert.False(cancellation.IsHandlerSubscribedForTests);
    }

    [Fact]
    public void RequestCancel_cancels_token_without_replacing_exit_code_constant()
    {
        using var cancellation = MailerCliCancellation.CreateUnattachedForTests();
        Assert.False(cancellation.Token.IsCancellationRequested);

        cancellation.RequestCancelForTests();

        Assert.True(cancellation.Token.IsCancellationRequested);
        Assert.Equal(130, MailerCliCancellation.ExitCode);
        Assert.NotEqual(DbMigrateCommand.SuccessExitCode, MailerCliCancellation.ExitCode);
        Assert.NotEqual(DbBackupCommand.UsageErrorExitCode, MailerCliCancellation.ExitCode);
        Assert.NotEqual(DbStatsCommand.UnavailableExitCode, MailerCliCancellation.ExitCode);
    }

    [Fact]
    public async Task MapCancellationAsync_completed_command_returns_original_exit_code()
    {
        var ct = TestContext.Current.CancellationToken;
        using var error = new StringWriter();

        var exitCode = await MailerCliHost.MapCancellationAsync(
            _ => Task.FromResult(DbMigrateCommand.SuccessExitCode),
            error,
            ct);

        Assert.Equal(DbMigrateCommand.SuccessExitCode, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task MapCancellationAsync_maps_cancelled_token_to_exit_130_without_error_noise()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var error = new StringWriter();

        var exitCode = await MailerCliHost.MapCancellationAsync(
            async token =>
            {
                token.ThrowIfCancellationRequested();
                return DbMigrateCommand.SuccessExitCode;
            },
            error,
            cts.Token);

        Assert.Equal(MailerCliCancellation.ExitCode, exitCode);
        Assert.Equal("Cancelled." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task MapCancellationAsync_does_not_replace_unrelated_exceptions()
    {
        using var cts = new CancellationTokenSource();
        using var error = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MailerCliHost.MapCancellationAsync(
                _ => throw new InvalidOperationException("not-cancellation"),
                error,
                cts.Token));

        Assert.Equal("not-cancellation", exception.Message);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task MapCancellationAsync_does_not_map_operation_canceled_for_other_token()
    {
        using var commandCts = new CancellationTokenSource();
        using var otherCts = new CancellationTokenSource();
        otherCts.Cancel();
        using var error = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MailerCliHost.MapCancellationAsync(
                _ => throw new OperationCanceledException(otherCts.Token),
                error,
                commandCts.Token));

        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Db_backup_cancel_before_replace_preserves_destination_cleans_temp_and_exits_130()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-cancel-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var backupPath = Path.Combine(root, "backups", "mailer.db");

        try
        {
            var configuration = CreateConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);
            await SeedMarkerAsync(databasePath, "good-backup", ct);
            await factory.BackupToAsync(backupPath, ct);
            await SeedMarkerAsync(databasePath, "should-not-land", ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            factory.BeforeAtomicReplaceForTests = token =>
            {
                Assert.NotEmpty(ListTempBackupArtifacts(backupPath));
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            using var output = new StringWriter();
            using var error = new StringWriter();
            try
            {
                var exitCode = await MailerCliHost.MapCancellationAsync(
                    token => new DbBackupCommand(factory).ExecuteAsync(
                        ["db", "backup", backupPath],
                        output,
                        error,
                        token),
                    error,
                    cts.Token);

                Assert.Equal(MailerCliCancellation.ExitCode, exitCode);
                Assert.Contains("Cancelled.", error.ToString(), StringComparison.Ordinal);
                Assert.Equal(string.Empty, output.ToString());
                Assert.Equal("good-backup", await ReadMarkerAsync(backupPath, ct));
                Assert.Empty(ListTempBackupArtifacts(backupPath));
            }
            finally
            {
                factory.BeforeAtomicReplaceForTests = null;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_cancel_before_commit_rolls_back_and_exits_130()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-cancel-migrate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");
        Directory.CreateDirectory(migrationDirectory);

        try
        {
            foreach (var file in Directory.GetFiles(GetCurrentMigrationDirectory(), "*.sql"))
            {
                File.Copy(file, Path.Combine(migrationDirectory, Path.GetFileName(file)));
            }

            var configuration = CreateConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory, migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            const string pendingVersion = "999_cli_cancel_probe.sql";
            await File.WriteAllTextAsync(
                Path.Combine(migrationDirectory, pendingVersion),
                """
                CREATE TABLE cli_cancel_probe (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    marker TEXT NOT NULL
                );
                INSERT INTO cli_cancel_probe (id, marker) VALUES (1, 'should-roll-back');
                """,
                ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runner.BeforeMigrationCommitForTests = token =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };

            using var output = new StringWriter();
            using var error = new StringWriter();
            try
            {
                var exitCode = await MailerCliHost.MapCancellationAsync(
                    token => new DbMigrateCommand(runner).ExecuteAsync(
                        ["db", "migrate"],
                        output,
                        error,
                        token),
                    error,
                    cts.Token);

                Assert.Equal(MailerCliCancellation.ExitCode, exitCode);
                Assert.Contains("Cancelled.", error.ToString(), StringComparison.Ordinal);
                Assert.False(await TableExistsAsync(databasePath, "cli_cancel_probe", ct));
                Assert.False(await SchemaMigrationExistsAsync(databasePath, pendingVersion, ct));
            }
            finally
            {
                runner.BeforeMigrationCommitForTests = null;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_admin_audit_purge_cancel_after_batch_exits_130()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-cancel-purge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["MAILER_ADMIN_AUDIT_RETENTION_BATCH_SIZE"] = "1",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);

            var repository = new AdminAuditRepository(factory);
            await repository.WriteAsync(NewAuditEvent("old-1", DateTimeOffset.UtcNow.AddDays(-200)), ct);
            await repository.WriteAsync(NewAuditEvent("old-2", DateTimeOffset.UtcNow.AddDays(-180)), ct);
            Assert.Equal(2, await repository.CountAsync(ct));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var command = new DbAdminAuditPurgeCommand(factory, TimeProvider.System)
            {
                AfterBatchDeletedForTests = (_, token) =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
            };

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await MailerCliHost.MapCancellationAsync(
                token => command.ExecuteAsync(
                    ["db", "admin-audit", "purge", "--older-than-days", "90"],
                    configuration,
                    output,
                    error,
                    token),
                error,
                cts.Token);

            Assert.Equal(MailerCliCancellation.ExitCode, exitCode);
            Assert.Contains("Cancelled.", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal(1, await repository.CountAsync(ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Healthcheck_cancelled_token_is_not_mapped_to_unhealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-cli-cancel-health", Guid.NewGuid().ToString("N"));
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

            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration)).ApplyPendingAsync(ct);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var error = new StringWriter();

            var exitCode = await MailerCliHost.MapCancellationAsync(
                token => MailerCliHost.RunHealthCheckAsync(configuration, token),
                error,
                cts.Token);

            Assert.Equal(MailerCliCancellation.ExitCode, exitCode);
            Assert.NotEqual(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IConfiguration CreateConfiguration(string databasePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();

    private static AdminAuditEvent NewAuditEvent(string actor, DateTimeOffset occurredAt) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.LoginSucceeded,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.AdminSession,
            Result = AdminAuditLog.Results.Success,
        };

    private static IReadOnlyList<string> ListTempBackupArtifacts(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        Assert.False(string.IsNullOrEmpty(directory));
        var prefix = "." + Path.GetFileName(destinationPath) + ".tmp-";
        return Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
    }

    private static async Task SeedMarkerAsync(string databasePath, string marker, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS backup_atomicity_marker (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    marker TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO backup_atomicity_marker (id, marker) VALUES (1, @Marker)
            ON CONFLICT(id) DO UPDATE SET marker = excluded.marker;
            """;
        upsert.Parameters.AddWithValue("@Marker", marker);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadMarkerAsync(string databasePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT marker FROM backup_atomicity_marker WHERE id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<string>(result);
    }

    private static async Task<bool> TableExistsAsync(
        string databasePath,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = @Name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> SchemaMigrationExistsAsync(
        string databasePath,
        string version,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM schema_migrations
            WHERE version = @Version
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Version", version);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string GetCurrentMigrationDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
}
