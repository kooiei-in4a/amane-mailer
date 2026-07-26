using Amane.Mailer.Admin;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerCliSuppressionsRemoveTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000401");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000402");
    private const string Recipient = "user@example.com";

    [Fact]
    public async Task removes_existing_suppression_and_writes_audit_without_recipient()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await SeedSuppressionAsync(db.Factory, TenantA, "User@Example.COM", ct);

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbSuppressionsRemoveCommand(db.Factory, TimeProvider.System);

        var exitCode = await command.ExecuteAsync(
            ["db", "suppressions", "remove", "--tenant-id", TenantA.ToString("D"), "--recipient", Recipient],
            output,
            error,
            ct);

        Assert.Equal(DbSuppressionsRemoveCommand.SuccessExitCode, exitCode);
        Assert.Contains($"tenant {TenantA:D}", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Recipient, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantA, Recipient, ct));

        var audit = await new AdminAuditRepository(db.Factory).ListRecentAsync(10, ct);
        var removed = Assert.Single(
            audit,
            row => row.EventType == AdminAuditLog.EventTypes.MailSuppressionsRemoved);
        Assert.Equal(DbSuppressionsRemoveCommand.CliActor, removed.Actor);
        Assert.Equal(AdminAuditLog.Results.Success, removed.Result);
        Assert.Equal(AdminAuditLog.TargetTypes.MailSuppressions, removed.TargetType);
        Assert.Equal(TenantA.ToString("D"), removed.TargetId);
        Assert.DoesNotContain(Recipient, removed.TargetId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(removed.ErrorCode);
        Assert.Equal(TenantA.ToString("D"), await ReadAuditTenantIdAsync(db.Factory, removed.Id, ct));
    }

    [Fact]
    public async Task returns_not_found_for_missing_entry_without_silent_success()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbSuppressionsRemoveCommand(db.Factory, TimeProvider.System);

        var exitCode = await command.ExecuteAsync(
            ["db", "suppressions", "remove", "--tenant-id", TenantA.ToString("D"), "--recipient", Recipient],
            output,
            error,
            ct);

        Assert.Equal(DbSuppressionsRemoveCommand.NotFoundExitCode, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains($"tenant {TenantA:D}", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient, error.ToString(), StringComparison.OrdinalIgnoreCase);

        var audit = await new AdminAuditRepository(db.Factory).ListRecentAsync(10, ct);
        var removed = Assert.Single(
            audit,
            row => row.EventType == AdminAuditLog.EventTypes.MailSuppressionsRemoved);
        Assert.Equal(AdminAuditLog.Results.Failure, removed.Result);
        Assert.Equal(AdminAuditLog.ErrorCodes.NotFound, removed.ErrorCode);
    }

    [Fact]
    public async Task normalizes_recipient_case_and_whitespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await SeedSuppressionAsync(db.Factory, TenantA, "user@example.com", ct);

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbSuppressionsRemoveCommand(db.Factory, TimeProvider.System);

        var exitCode = await command.ExecuteAsync(
            ["db", "suppressions", "remove", "--tenant-id", TenantA.ToString("D"), "--recipient", "  USER@Example.COM  "],
            output,
            error,
            ct);

        Assert.Equal(DbSuppressionsRemoveCommand.SuccessExitCode, exitCode);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantA, Recipient, ct));
        Assert.DoesNotContain("USER@Example.COM", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("USER@Example.COM", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task does_not_remove_same_recipient_for_other_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await SeedSuppressionAsync(db.Factory, TenantA, Recipient, ct);
        await SeedSuppressionAsync(db.Factory, TenantB, Recipient, ct);

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new DbSuppressionsRemoveCommand(db.Factory, TimeProvider.System);

        var exitCode = await command.ExecuteAsync(
            ["db", "suppressions", "remove", "--tenant-id", TenantA.ToString("D"), "--recipient", Recipient],
            output,
            error,
            ct);

        Assert.Equal(DbSuppressionsRemoveCommand.SuccessExitCode, exitCode);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantA, Recipient, ct));
        Assert.True(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantB, Recipient, ct));
    }

    [Fact]
    public async Task usage_error_when_required_options_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var command = new DbSuppressionsRemoveCommand(
            new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = "Data Source=:memory:",
                    })
                    .Build()),
            TimeProvider.System);

        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await command.ExecuteAsync(
            ["db", "suppressions", "remove", "--tenant-id", TenantA.ToString("D")],
            output,
            error,
            ct);

        Assert.Equal(DbSuppressionsRemoveCommand.UsageErrorExitCode, exitCode);
        Assert.Contains("--tenant-id and --recipient are required.", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: dotnet Amane.Mailer.dll db suppressions remove", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task db_stats_includes_mail_suppressions_count()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await SeedSuppressionAsync(db.Factory, TenantA, Recipient, ct);
        await SeedSuppressionAsync(db.Factory, TenantB, "other@example.com", ct);

        var tenantOutput = new StringWriter();
        var allOutput = new StringWriter();
        var error = new StringWriter();
        var statsCommand = new DbStatsCommand(db.Factory);

        Assert.Equal(
            DbStatsCommand.SuccessExitCode,
            await statsCommand.ExecuteAsync(
                ["db", "stats", "--tenant-id", TenantA.ToString("D")],
                tenantOutput,
                error,
                ct));
        Assert.Equal(
            DbStatsCommand.SuccessExitCode,
            await statsCommand.ExecuteAsync(["db", "stats"], allOutput, error, ct));

        var tenantStats = ParseStats(tenantOutput.ToString());
        var allStats = ParseStats(allOutput.ToString());
        Assert.Equal("1", tenantStats["mail_suppressions_count"]);
        Assert.Equal("2", allStats["mail_suppressions_count"]);
    }

    private static async Task SeedSuppressionAsync(
        SqliteConnectionFactory factory,
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        Assert.True(await new MailSuppressionRepository(factory).TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RecipientEmail = recipientEmail,
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = DateTimeOffset.Parse("2026-07-27T00:00:00Z"),
            },
            cancellationToken));
    }

    private static async Task<string?> ReadAuditTenantIdAsync(
        SqliteConnectionFactory factory,
        long auditId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id FROM admin_audit_events WHERE id = @Id;";
        command.Parameters.AddWithValue("@Id", auditId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static IReadOnlyDictionary<string, string> ParseStats(string stats) =>
        stats
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private TestDatabase(string root, SqliteConnectionFactory factory)
        {
            _root = root;
            Factory = factory;
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-cli-suppressions-remove",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new TestDatabase(root, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}