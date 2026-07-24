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

    [Fact]
    public async Task List_for_admin_filters_mail_request_events_by_tenant_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var visibleTenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var hiddenTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");
        var visibleMailRequestId = Guid.NewGuid();
        var hiddenMailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(db.ConnectionString, visibleMailRequestId, visibleTenantId, ct);
        await SeedMailRequestAsync(db.ConnectionString, hiddenMailRequestId, hiddenTenantId, ct);

        var occurredAt = new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(
            NewMailRequestAuditEvent(visibleMailRequestId, visibleTenantId, occurredAt, "scoped-visible"),
            ct);
        await repository.WriteAsync(
            NewMailRequestAuditEvent(hiddenMailRequestId, hiddenTenantId, occurredAt.AddMinutes(1), "scoped-hidden"),
            ct);
        await repository.WriteAsync(
            NewAuthAuditEvent(AdminAuditLog.EventTypes.Logout, "scoped-auth", occurredAt.AddMinutes(2)),
            ct);

        var scopedTenants = new HashSet<Guid> { visibleTenantId };
        var page = await repository.ListForAdminAsync(
            new AdminAuditListQuery { AllowedTenantIds = scopedTenants, PageSize = 50 },
            ct);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, row => row.Actor == "scoped-visible");
        Assert.Contains(page.Items, row => row.Actor == "scoped-auth");
        Assert.DoesNotContain(page.Items, row => row.Actor == "scoped-hidden");
    }

    [Fact]
    public async Task Scoped_admin_can_list_and_get_mail_request_audit_after_mail_request_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var visibleTenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        var hiddenTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");
        var visibleMailRequestId = Guid.NewGuid();
        var hiddenMailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(db.ConnectionString, visibleMailRequestId, visibleTenantId, ct);
        await SeedMailRequestAsync(db.ConnectionString, hiddenMailRequestId, hiddenTenantId, ct);

        var occurredAt = new DateTimeOffset(2026, 6, 27, 14, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(
            NewMailRequestAuditEvent(visibleMailRequestId, visibleTenantId, occurredAt, "retained-visible"),
            ct);
        await repository.WriteAsync(
            NewMailRequestAuditEvent(hiddenMailRequestId, hiddenTenantId, occurredAt.AddMinutes(1), "retained-hidden"),
            ct);

        await DeleteMailRequestAsync(db.ConnectionString, visibleMailRequestId, ct);
        await DeleteMailRequestAsync(db.ConnectionString, hiddenMailRequestId, ct);

        var scopedTenants = new HashSet<Guid> { visibleTenantId };
        var page = await repository.ListForAdminAsync(
            new AdminAuditListQuery { AllowedTenantIds = scopedTenants, PageSize = 50 },
            ct);

        var visible = Assert.Single(page.Items);
        Assert.Equal("retained-visible", visible.Actor);
        Assert.DoesNotContain(page.Items, row => row.Actor == "retained-hidden");

        var detail = await repository.GetForAdminAsync(visible.Id, scopedTenants, ct);
        Assert.NotNull(detail);
        Assert.Equal("retained-visible", detail.Actor);

        var allRows = await repository.ListRecentAsync(50, ct);
        var hidden = Assert.Single(allRows, row => row.Actor == "retained-hidden");
        Assert.Null(await repository.GetForAdminAsync(hidden.Id, scopedTenants, ct));
    }

    [Fact]
    public async Task Write_resolves_tenant_id_from_mail_request_when_omitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000303");
        var mailRequestId = Guid.NewGuid();
        await SeedMailRequestAsync(db.ConnectionString, mailRequestId, tenantId, ct);

        await repository.WriteAsync(
            NewMailRequestAuditEvent(
                mailRequestId,
                tenantId: null,
                new DateTimeOffset(2026, 6, 27, 15, 0, 0, TimeSpan.Zero),
                "resolved-from-row"),
            ct);

        await DeleteMailRequestAsync(db.ConnectionString, mailRequestId, ct);

        var page = await repository.ListForAdminAsync(
            new AdminAuditListQuery { AllowedTenantIds = new HashSet<Guid> { tenantId }, PageSize = 50 },
            ct);

        Assert.Equal("resolved-from-row", Assert.Single(page.Items).Actor);
        Assert.Equal(
            tenantId.ToString("D"),
            await ReadStoredTenantIdAsync(db.ConnectionString, "resolved-from-row", ct));
    }

    [Fact]
    public async Task Scoped_admin_excludes_mail_request_audit_with_null_tenant_id_while_break_glass_sees_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var scopedTenantId = Guid.Parse("00000000-0000-0000-0000-000000000404");
        var orphanMailRequestId = Guid.NewGuid();
        await InsertMailRequestAuditWithNullTenantIdAsync(
            db.ConnectionString,
            orphanMailRequestId,
            actor: "legacy-null-tenant",
            occurredAt: new DateTimeOffset(2026, 6, 27, 16, 0, 0, TimeSpan.Zero),
            ct);
        await repository.WriteAsync(
            NewAuthAuditEvent(
                AdminAuditLog.EventTypes.Logout,
                "scoped-auth-null-tenant",
                new DateTimeOffset(2026, 6, 27, 16, 1, 0, TimeSpan.Zero)),
            ct);

        var scopedPage = await repository.ListForAdminAsync(
            new AdminAuditListQuery
            {
                AllowedTenantIds = new HashSet<Guid> { scopedTenantId },
                PageSize = 50,
            },
            ct);
        Assert.DoesNotContain(scopedPage.Items, row => row.Actor == "legacy-null-tenant");
        Assert.Contains(scopedPage.Items, row => row.Actor == "scoped-auth-null-tenant");

        var breakGlassPage = await repository.ListForAdminAsync(
            new AdminAuditListQuery { AllowedTenantIds = null, PageSize = 50 },
            ct);
        Assert.Contains(breakGlassPage.Items, row => row.Actor == "legacy-null-tenant");
    }

    [Fact]
    public async Task List_for_admin_applies_event_type_and_actor_filters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var occurredAt = new DateTimeOffset(2026, 6, 27, 13, 0, 0, TimeSpan.Zero);
        await repository.WriteAsync(NewAuthAuditEvent(AdminAuditLog.EventTypes.Logout, "alpha", occurredAt), ct);
        await repository.WriteAsync(
            NewAuthAuditEvent(AdminAuditLog.EventTypes.SessionExpired, "alpha", occurredAt.AddMinutes(1)),
            ct);

        var page = await repository.ListForAdminAsync(
            new AdminAuditListQuery
            {
                EventType = AdminAuditLog.EventTypes.Logout,
                Actor = "alpha",
                PageSize = 50,
            },
            ct);

        var row = Assert.Single(page.Items);
        Assert.Equal(AdminAuditLog.EventTypes.Logout, row.EventType);
        Assert.Equal("alpha", row.Actor);
    }

    private static AdminAuditEvent NewMailRequestAuditEvent(
        Guid mailRequestId,
        Guid? tenantId,
        DateTimeOffset occurredAt,
        string actor) =>
        new()
        {
            EventType = AdminAuditLog.EventTypes.ManualCancelRequested,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.MailRequest,
            TargetId = mailRequestId.ToString("D"),
            TenantId = tenantId,
            Result = AdminAuditLog.Results.Success,
        };

    private static AdminAuditEvent NewAuthAuditEvent(
        string eventType,
        string actor,
        DateTimeOffset occurredAt) =>
        new()
        {
            EventType = eventType,
            Actor = actor,
            OccurredAt = occurredAt,
            TargetType = AdminAuditLog.TargetTypes.AdminSession,
            Result = AdminAuditLog.Results.Success,
        };

    private static async Task SeedMailRequestAsync(
        string connectionString,
        Guid id,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(2026, 6, 27, 0, 0, 0, TimeSpan.Zero);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, recipient_email,
                status, attempt_count, max_attempts, last_error_message,
                accepted_at, created_at, updated_at, completed_at, failed_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, 'AuditRepoTest',
                '{}', @PayloadHash, @Subject, NULL, @RecipientEmail,
                @Status, 1, 3, NULL,
                @AcceptedAt, @CreatedAt, @UpdatedAt, NULL, NULL);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", "audit-repo-test");
        command.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('b', 64));
        command.Parameters.AddWithValue("@Subject", "repo test subject");
        command.Parameters.AddWithValue("@RecipientEmail", "repo-test@example.com");
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@AcceptedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
        command.Parameters.AddWithValue("@UpdatedAt", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteMailRequestAsync(
        string connectionString,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mail_requests WHERE id = @Id;";
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMailRequestAuditWithNullTenantIdAsync(
        string connectionString,
        Guid mailRequestId,
        string actor,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_audit_events (
                event_type, actor, occurred_at,
                source_ip, user_agent_summary,
                target_type, target_id, tenant_id, field_name,
                result, error_code)
            VALUES (
                @EventType, @Actor, @OccurredAt,
                NULL, NULL,
                @TargetType, @TargetId, NULL, NULL,
                @Result, NULL);
            """;
        command.Parameters.AddWithValue("@EventType", AdminAuditLog.EventTypes.ManualCancelRequested);
        command.Parameters.AddWithValue("@Actor", actor);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(occurredAt));
        command.Parameters.AddWithValue("@TargetType", AdminAuditLog.TargetTypes.MailRequest);
        command.Parameters.AddWithValue("@TargetId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@Result", AdminAuditLog.Results.Success);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadStoredTenantIdAsync(
        string connectionString,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
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
        return result as string;
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
