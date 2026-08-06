using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class BounceAdminQueryTenantScopeTests
{
    private static readonly Guid VisibleTenantId = Guid.Parse("00000000-0000-0000-0000-000000000401");
    private static readonly Guid HiddenTenantId = Guid.Parse("00000000-0000-0000-0000-000000000402");
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Bounce_list_for_mail_request_respects_tenant_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BounceAdminTestDatabase.CreateAsync(ct);
        var repository = new BounceEventRepository(db.Factory);

        var sharedMailRequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await SeedBounceAsync(db.ConnectionString, VisibleTenantId, sharedMailRequestId, "evt-visible", ct);
        await SeedBounceAsync(db.ConnectionString, HiddenTenantId, sharedMailRequestId, "evt-hidden", ct);

        var scoped = new HashSet<Guid> { VisibleTenantId };
        var visible = await repository.ListForMailRequestAsync(
            VisibleTenantId, "bounce-admin-scope", sharedMailRequestId, scoped, ct);
        var hidden = await repository.ListForMailRequestAsync(
            HiddenTenantId, "bounce-admin-scope", sharedMailRequestId, scoped, ct);

        Assert.Equal("evt-visible", Assert.Single(visible).ProviderEventId);
        Assert.Empty(hidden);
    }

    [Fact]
    public async Task Suppression_list_hides_other_tenant_rows_for_scoped_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BounceAdminTestDatabase.CreateAsync(ct);
        var repository = new MailSuppressionRepository(db.Factory);

        await SeedSuppressionAsync(db.ConnectionString, VisibleTenantId, "visible@example.com", ct);
        await SeedSuppressionAsync(db.ConnectionString, HiddenTenantId, "hidden@example.com", ct);

        var page = await repository.ListForAdminAsync(
            new AdminSuppressionListQuery
            {
                AllowedTenantIds = new HashSet<Guid> { VisibleTenantId },
                PageSize = 50,
            },
            ct);

        var row = Assert.Single(page.Items);
        Assert.Equal(VisibleTenantId, row.TenantId);
        Assert.Equal("visible@example.com", row.RecipientEmail);
    }

    [Fact]
    public async Task Empty_scope_returns_no_suppressions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BounceAdminTestDatabase.CreateAsync(ct);
        var repository = new MailSuppressionRepository(db.Factory);

        await SeedSuppressionAsync(db.ConnectionString, VisibleTenantId, "visible@example.com", ct);

        var page = await repository.ListForAdminAsync(
            new AdminSuppressionListQuery
            {
                AllowedTenantIds = new HashSet<Guid>(),
                PageSize = 50,
            },
            ct);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Break_glass_null_scope_lists_suppressions_across_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await BounceAdminTestDatabase.CreateAsync(ct);
        var repository = new MailSuppressionRepository(db.Factory);

        await SeedSuppressionAsync(db.ConnectionString, VisibleTenantId, "visible@example.com", ct);
        await SeedSuppressionAsync(db.ConnectionString, HiddenTenantId, "hidden@example.com", ct);

        var page = await repository.ListForAdminAsync(
            new AdminSuppressionListQuery
            {
                AllowedTenantIds = null,
                PageSize = 50,
            },
            ct);

        Assert.Equal(2, page.Items.Count);
    }

    private static async Task SeedBounceAsync(
        string connectionString,
        Guid tenantId,
        Guid mailRequestId,
        string providerEventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recipient_delivery_events (
                id, tenant_id, source_service, mail_request_id,
                recipient_role, recipient_ordinal,
                provider, provider_event_id, provider_message_id,
                provider_status, applied_delivery_state, status_message, occurred_at, created_at)
            VALUES (
                @Id, @TenantId, 'bounce-admin-scope', @MailRequestId,
                0, 0,
                'acs', @ProviderEventId, @ProviderMessageId,
                'Bounced', 3, 'sanitized', @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@ProviderEventId", providerEventId);
        command.Parameters.AddWithValue("@ProviderMessageId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedSuppressionAsync(
        string connectionString,
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_suppressions (
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
            VALUES (
                @Id, @TenantId, @RecipientEmail, @Reason, NULL, @Now);
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", RecipientEmailNormalizer.Normalize(recipientEmail));
        command.Parameters.AddWithValue("@Reason", MailSuppressionReasons.HardBounce);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class BounceAdminTestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private BounceAdminTestDatabase(string root, SqliteConnectionFactory factory, string connectionString)
        {
            _root = root;
            Factory = factory;
            ConnectionString = connectionString;
        }

        public SqliteConnectionFactory Factory { get; }

        public string ConnectionString { get; }

        public static async Task<BounceAdminTestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-bounce-admin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";

            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = connectionString,
                    })
                    .Build());

            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new BounceAdminTestDatabase(root, factory, connectionString);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
}
