using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Contracts.MailRequests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminAuditRetentionTests : IClassFixture<AdminAuditRetentionFixture>, IAsyncLifetime
{
    private readonly AdminAuditRetentionFixture _fixture;

    public AdminAuditRetentionTests(AdminAuditRetentionFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() =>
        await _fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Load_prefers_MAILER_retention_days_when_both_env_vars_are_set()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_ADMIN_AUDIT_RETENTION_DAYS"] = "90",
                ["AMANE_ADMIN_AUDIT_RETENTION_DAYS"] = "365",
            })
            .Build();

        var options = MailerAdminAuditRetentionOptions.Load(configuration);

        Assert.Equal(90, options.RetentionDays);
    }

    [Fact]
    public void Validate_rejects_retention_under_30_days_outside_local_development()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_ADMIN_AUDIT_RETENTION_DAYS"] = "7",
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            })
            .Build();

        var options = MailerAdminAuditRetentionOptions.Load(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("30", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_allows_retention_under_30_days_in_local_development()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_ADMIN_AUDIT_RETENTION_DAYS"] = "7",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            })
            .Build();

        var options = MailerAdminAuditRetentionOptions.Load(configuration);
        options.Validate();
    }

    [Fact]
    public async Task DeleteOlderThanAsync_removes_only_expired_audit_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditRetentionTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        var expiredAt = DateTimeOffset.UtcNow.AddDays(-200);
        var recentAt = DateTimeOffset.UtcNow.AddDays(-1);
        await repository.WriteAsync(NewEvent("expired-actor", expiredAt), ct);
        await repository.WriteAsync(NewEvent("recent-actor", recentAt), ct);

        var deleted = await repository.DeleteOlderThanAsync(
            DateTimeOffset.UtcNow.AddDays(-90),
            batchSize: 100,
            ct);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await repository.CountAsync(ct));
        var remaining = Assert.Single(await repository.ListRecentAsync(10, ct));
        Assert.Equal("recent-actor", remaining.Actor);
    }

    [Fact]
    public async Task Retention_sweep_purges_expired_audit_events()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();

        await repository.WriteAsync(
            NewEvent("expired-audit", DateTimeOffset.UtcNow.AddDays(-120)),
            ct);
        await repository.WriteAsync(
            NewEvent("recent-audit", DateTimeOffset.UtcNow.AddDays(-1)),
            ct);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await repository.CountAsync(ct) == 1)
            {
                var remaining = Assert.Single(await repository.ListRecentAsync(10, ct));
                Assert.Equal("recent-audit", remaining.Actor);
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException("Expired admin audit event was not purged by retention service.");
    }

    [Fact]
    public async Task Retention_sweep_does_not_delete_mail_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = MailRequestTestData.CreateRequest();
        await SeedDeliveredRequestAsync(request, DateTimeOffset.UtcNow.AddDays(-200), ct);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var auditRepository = scope.ServiceProvider.GetRequiredService<AdminAuditRepository>();
        var mailRepository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();

        await auditRepository.WriteAsync(
            NewEvent("expired-audit", DateTimeOffset.UtcNow.AddDays(-120)),
            ct);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await auditRepository.CountAsync(ct) == 0)
            {
                var mailState = await mailRepository.FindDispatchStateByMailRequestIdAsync(request.MailRequestId, ct);
                Assert.NotNull(mailState);
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException("Admin audit retention sweep did not complete.");
    }

    [Fact]
    public async Task Db_admin_audit_purge_removes_only_rows_older_than_requested_days()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditRetentionTestDatabase.CreateAsync(ct);
        var repository = new AdminAuditRepository(db.Factory);

        await repository.WriteAsync(NewEvent("old-event", DateTimeOffset.UtcNow.AddDays(-200)), ct);
        await repository.WriteAsync(NewEvent("recent-event", DateTimeOffset.UtcNow.AddDays(-10)), ct);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = db.ConnectionString,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            })
            .Build();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DbAdminAuditPurgeCommand(db.Factory, TimeProvider.System);
        var exitCode = await command.ExecuteAsync(
            ["db", "admin-audit", "purge", "--older-than-days", "90"],
            configuration,
            output,
            error,
            ct);

        Assert.Equal(DbAdminAuditPurgeCommand.SuccessExitCode, exitCode);
        Assert.Equal(1, await repository.CountAsync(ct));
        var remaining = Assert.Single(await repository.ListRecentAsync(10, ct));
        Assert.Equal("recent-event", remaining.Actor);

        var purgeOutput = output.ToString();
        Assert.Contains("Purge removed 1 admin audit events older than 90 days.", purgeOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("old-event", purgeOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("recent-event", purgeOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_admin_audit_purge_rejects_short_retention_outside_local_development()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditRetentionTestDatabase.CreateAsync(ct);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = db.ConnectionString,
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            })
            .Build();

        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DbAdminAuditPurgeCommand(db.Factory, TimeProvider.System);
        var exitCode = await command.ExecuteAsync(
            ["db", "admin-audit", "purge", "--older-than-days", "7"],
            configuration,
            output,
            error,
            ct);

        Assert.Equal(DbAdminAuditPurgeCommand.UsageErrorExitCode, exitCode);
        Assert.Contains("30", error.ToString(), StringComparison.Ordinal);
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

    private async Task SeedDeliveredRequestAsync(
        MailRequestCreateRequest request,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(request);
        var now = DateTimeOffset.UtcNow;

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        await repository.InsertAcceptedAsync(
            new AcceptedMailRequestInsert
            {
                Id = Guid.CreateVersion7(now),
                TenantId = request.TenantId,
                SourceService = request.SourceService,
                MailRequestId = request.MailRequestId,
                Purpose = request.Purpose,
                PayloadJson = body,
                PayloadHash = request.PayloadHash,
                Subject = request.Subject,
                HtmlBody = request.HtmlBody,
                TextBody = request.TextBody,
                ReplyTo = request.ReplyTo,
                RecipientEmail = request.To[0].Email,
                RecipientDisplayName = request.To[0].DisplayName,
                MaxAttempts = 3,
                AcceptedAt = now,
            },
            cancellationToken);

        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = @Status,
                attempt_count = 1,
                completed_at = @CompletedAt,
                delivered_at = @CompletedAt,
                updated_at = @CompletedAt
            WHERE mail_request_id = @MailRequestId;
            """;
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(completedAt));
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class AuditRetentionTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private AuditRetentionTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<AuditRetentionTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-audit-retention", Guid.NewGuid().ToString("N"));
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
            return new AuditRetentionTestDatabase(root, factory, connectionString);
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

public sealed class AdminAuditRetentionFixture() : MailerWebApplicationFixtureBase(workerEnabled: true)
{
    public CapturingMailDeliveryProvider DeliveryProvider { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
            ["MAILER_ADMIN_AUDIT_RETENTION_DAYS"] = "90",
            ["MAILER_ADMIN_AUDIT_RETENTION_SWEEP_INTERVAL_SECONDS"] = "1",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };

    public new async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _ = Factory.CreateClient();
    }

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.RemoveAll<IMailDeliveryProvider>();
        services.AddSingleton<IMailDeliveryProvider>(DeliveryProvider);
    }
}
