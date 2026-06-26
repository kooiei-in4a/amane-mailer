using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminAuditRepositoryTests
{
    [Fact]
    public async Task Write_then_list_round_trips_all_audit_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var occurredAt = new DateTimeOffset(2026, 6, 27, 8, 30, 15, TimeSpan.Zero);
        var targetId = Guid.NewGuid().ToString("D");
        await repository.WriteAsync(
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.MailRequestBodyViewed,
                Actor = "admin-user",
                OccurredAt = occurredAt,
                SourceIp = "203.0.113.10",
                UserAgentSummary = "Mozilla/5.0 example",
                TargetType = AdminAuditLog.TargetTypes.MailRequest,
                TargetId = targetId,
                FieldName = "html_body",
                Result = AdminAuditLog.Results.Success,
                ErrorCode = null,
            },
            ct);

        var rows = await repository.ListRecentAsync(50, ct);

        var row = Assert.Single(rows);
        Assert.Equal(AdminAuditLog.EventTypes.MailRequestBodyViewed, row.EventType);
        Assert.Equal("admin-user", row.Actor);
        Assert.Equal(occurredAt, row.OccurredAt);
        Assert.Equal("203.0.113.10", row.SourceIp);
        Assert.Equal("Mozilla/5.0 example", row.UserAgentSummary);
        Assert.Equal(AdminAuditLog.TargetTypes.MailRequest, row.TargetType);
        Assert.Equal(targetId, row.TargetId);
        Assert.Equal("html_body", row.FieldName);
        Assert.Equal(AdminAuditLog.Results.Success, row.Result);
        Assert.Null(row.ErrorCode);
    }

    [Fact]
    public async Task Write_persists_null_optional_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        await repository.WriteAsync(
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.LoginFailed,
                Actor = "intruder",
                OccurredAt = new DateTimeOffset(2026, 6, 27, 9, 0, 0, TimeSpan.Zero),
                Result = AdminAuditLog.Results.Failure,
            },
            ct);

        var row = Assert.Single(await repository.ListRecentAsync(50, ct));
        Assert.Null(row.SourceIp);
        Assert.Null(row.UserAgentSummary);
        Assert.Null(row.TargetId);
        Assert.Null(row.FieldName);
        Assert.Null(row.ErrorCode);
    }

    [Fact]
    public async Task List_recent_orders_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var baseTime = new DateTimeOffset(2026, 6, 27, 10, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(NewEvent("older", baseTime), ct);
        await repository.WriteAsync(NewEvent("newer", baseTime.AddMinutes(5)), ct);

        var rows = await repository.ListRecentAsync(50, ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal("newer", rows[0].Actor);
        Assert.Equal("older", rows[1].Actor);
    }

    [Fact]
    public async Task Write_throws_when_audit_table_is_missing()
    {
        // This is the precondition that lets body-view persistence fail closed:
        // a failed write must surface, not be swallowed.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        await db.DropAuditTableAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.WriteAsync(
                NewEvent("admin", new DateTimeOffset(2026, 6, 27, 11, 0, 0, TimeSpan.Zero)),
                ct));
    }

    private static AdminAuditEvent NewEvent(string actor, DateTimeOffset occurredAt) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.LoginSucceeded,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.AdminSession,
            Result = AdminAuditLog.Results.Success,
        };

    private sealed class AuditTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private AuditTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<AuditTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-audit-repo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new AuditTestDatabase(root, factory, connectionString);
        }

        public async Task DropAuditTableAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE admin_audit_events;";
            await command.ExecuteNonQueryAsync(cancellationToken);
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
