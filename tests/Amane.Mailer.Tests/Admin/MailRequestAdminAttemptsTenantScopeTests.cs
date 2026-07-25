using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class MailRequestAdminAttemptsTenantScopeTests
{
    private static readonly Guid VisibleTenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid HiddenTenantId = Guid.Parse("00000000-0000-0000-0000-000000000202");
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 25, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scoped_admin_lists_attempts_only_for_visible_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AttemptsTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);

        var visibleId = await SeedMailRequestAsync(db.ConnectionString, VisibleTenantId, Guid.NewGuid(), ct);
        var hiddenId = await SeedMailRequestAsync(db.ConnectionString, HiddenTenantId, Guid.NewGuid(), ct);
        await SeedMailAttemptAsync(db.ConnectionString, visibleId, attemptNumber: 1, provider: "mailpit-visible", ct);
        await SeedMailAttemptAsync(db.ConnectionString, hiddenId, attemptNumber: 1, provider: "mailpit-hidden", ct);

        var scoped = new HashSet<Guid> { VisibleTenantId };
        var visibleAttempts = await repository.ListAttemptsForAdminAsync(visibleId, scoped, ct);
        var hiddenAttempts = await repository.ListAttemptsForAdminAsync(hiddenId, scoped, ct);

        Assert.Equal("mailpit-visible", Assert.Single(visibleAttempts).Provider);
        Assert.Empty(hiddenAttempts);
    }

    [Fact]
    public async Task Empty_scope_returns_no_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AttemptsTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);

        var requestId = await SeedMailRequestAsync(db.ConnectionString, VisibleTenantId, Guid.NewGuid(), ct);
        await SeedMailAttemptAsync(db.ConnectionString, requestId, attemptNumber: 1, provider: "mailpit", ct);

        var attempts = await repository.ListAttemptsForAdminAsync(requestId, new HashSet<Guid>(), ct);

        Assert.Empty(attempts);
    }

    [Fact]
    public async Task Break_glass_null_scope_lists_attempts_across_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AttemptsTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);

        var visibleId = await SeedMailRequestAsync(db.ConnectionString, VisibleTenantId, Guid.NewGuid(), ct);
        var hiddenId = await SeedMailRequestAsync(db.ConnectionString, HiddenTenantId, Guid.NewGuid(), ct);
        await SeedMailAttemptAsync(db.ConnectionString, visibleId, attemptNumber: 1, provider: "mailpit-visible", ct);
        await SeedMailAttemptAsync(db.ConnectionString, hiddenId, attemptNumber: 1, provider: "mailpit-hidden", ct);

        var visibleAttempts = await repository.ListAttemptsForAdminAsync(visibleId, allowedTenantIds: null, ct);
        var hiddenAttempts = await repository.ListAttemptsForAdminAsync(hiddenId, allowedTenantIds: null, ct);

        Assert.Equal("mailpit-visible", Assert.Single(visibleAttempts).Provider);
        Assert.Equal("mailpit-hidden", Assert.Single(hiddenAttempts).Provider);
    }

    [Fact]
    public async Task Shared_mail_request_id_across_tenants_does_not_leak_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AttemptsTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);

        var sharedMailRequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var visibleId = await SeedMailRequestAsync(db.ConnectionString, VisibleTenantId, sharedMailRequestId, ct);
        var hiddenId = await SeedMailRequestAsync(db.ConnectionString, HiddenTenantId, sharedMailRequestId, ct);
        await SeedMailAttemptAsync(db.ConnectionString, visibleId, attemptNumber: 1, provider: "mailpit-visible", ct);
        await SeedMailAttemptAsync(db.ConnectionString, hiddenId, attemptNumber: 1, provider: "mailpit-hidden", ct);

        var scoped = new HashSet<Guid> { VisibleTenantId };
        var visibleAttempts = await repository.ListAttemptsForAdminAsync(visibleId, scoped, ct);
        var hiddenAttempts = await repository.ListAttemptsForAdminAsync(hiddenId, scoped, ct);

        Assert.Equal("mailpit-visible", Assert.Single(visibleAttempts).Provider);
        Assert.Empty(hiddenAttempts);
    }

    [Fact]
    public async Task Direct_attempt_query_without_detail_lookup_still_enforces_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AttemptsTestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(db.Factory);

        var hiddenId = await SeedMailRequestAsync(db.ConnectionString, HiddenTenantId, Guid.NewGuid(), ct);
        await SeedMailAttemptAsync(db.ConnectionString, hiddenId, attemptNumber: 1, provider: "mailpit-hidden", ct);
        await SeedMailAttemptAsync(db.ConnectionString, hiddenId, attemptNumber: 2, provider: "mailpit-hidden-2", ct);

        // Negative path: call attempts API directly without GetDetailForAdminAsync first.
        var attempts = await repository.ListAttemptsForAdminAsync(
            hiddenId,
            new HashSet<Guid> { VisibleTenantId },
            ct);

        Assert.Empty(attempts);
        Assert.NotNull(await repository.GetDetailForAdminAsync(hiddenId, allowedTenantIds: null, ct));
    }

    private static async Task<Guid> SeedMailRequestAsync(
        string connectionString,
        Guid tenantId,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, 'admin-attempts-scope-test', @MailRequestId, 'AdminAttemptsScopeTest',
                '{}', @PayloadHash, 'scope subject', 'scope-recipient@example.com',
                @Status, 1, 3,
                @Now, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static async Task SeedMailAttemptAsync(
        string connectionString,
        Guid requestId,
        int attemptNumber,
        string provider,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message,
                retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @Status,
                NULL, 'provider_error', 'sanitized',
                0, @LockToken, @StartedAt, @CompletedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@AttemptNumber", attemptNumber);
        command.Parameters.AddWithValue("@Provider", provider);
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(attemptNumber)));
        command.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(FixedNow.AddMinutes(attemptNumber).AddSeconds(5)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class AttemptsTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private AttemptsTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<AttemptsTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-attempts", Guid.NewGuid().ToString("N"));
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
            return new AttemptsTestDatabase(root, factory, connectionString);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
