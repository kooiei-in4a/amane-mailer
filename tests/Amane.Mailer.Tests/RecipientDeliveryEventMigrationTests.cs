using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class RecipientDeliveryEventMigrationTests
{
    [Fact]
    public async Task Migration_017_backfills_legacy_history_to_To_ordinal_zero_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(request, "event-a", "Bounced", database.Now, ct);
        await database.InsertLegacyEventAsync(request, "event-b", "Failed", database.Later, ct);

        Assert.False(await database.Runner.IsCurrentSchemaReadyAsync(ct));
        database.Copy017();

        var applied = await database.Runner.ApplyPendingAsync(ct);

        Assert.Equal(["017_recipient_delivery_events.sql"], applied);
        Assert.True(await database.Runner.IsCurrentSchemaReadyAsync(ct));
        Assert.Equal(2L, await database.ReadCountAsync("SELECT COUNT(*) FROM recipient_delivery_events;", ct));
        Assert.Equal(0L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM recipient_delivery_events WHERE recipient_role <> 0 OR recipient_ordinal <> 0;",
            ct));
        Assert.Equal(1L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_status = 'Bounced' AND applied_delivery_state = 3;",
            ct));
        Assert.Equal(1L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_status = 'Failed' AND applied_delivery_state IS NULL;",
            ct));
        Assert.Equal(0L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'bounce_events';",
            ct));
        Assert.Empty(await database.Runner.ApplyPendingAsync(ct));
    }

    [Fact]
    public async Task Migration_017_backfills_delivered_but_keeps_negative_state_sticky()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var delivered = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(delivered, "event-delivered", "Delivered", database.Now, ct);
        var negative = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(negative, "event-negative", "Bounced", database.Now, ct);
        await database.InsertLegacyEventAsync(negative, "event-late-delivered", "Delivered", database.Later, ct);
        database.Copy017();

        await database.Runner.ApplyPendingAsync(ct);

        Assert.Equal(1L, await database.ReadCountAsync(
            $"SELECT COUNT(*) FROM mail_request_recipients WHERE request_id = '{delivered.RequestId}' AND delivery_state = 2;",
            ct));
        Assert.Equal(1L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_event_id = 'event-delivered' AND applied_delivery_state = 2;",
            ct));
        Assert.Equal(1L, await database.ReadCountAsync(
            $"SELECT COUNT(*) FROM mail_request_recipients WHERE request_id = '{negative.RequestId}' AND delivery_state = 3;",
            ct));
        Assert.Equal(1L, await database.ReadCountAsync(
            "SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_event_id = 'event-late-delivered' AND applied_delivery_state IS NULL;",
            ct));
    }

    [Fact]
    public async Task Migration_017_rolls_back_when_request_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var missing = new RequestIdentity(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));
        await database.InsertLegacyEventAsync(missing, "event-missing-request", "Bounced", database.Now, ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Fact]
    public async Task Migration_017_partial_backfill_failure_rolls_back_every_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var valid = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(valid, "event-valid-before-failure", "Bounced", database.Now, ct);
        var missing = new RequestIdentity(
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));
        await database.InsertLegacyEventAsync(missing, "event-invalid-after-valid", "Bounced", database.Later, ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
        Assert.Equal(2L, await database.ReadCountAsync("SELECT COUNT(*) FROM bounce_events;", ct));
    }

    [Fact]
    public async Task Migration_017_rolls_back_when_migration_016_schema_is_incomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        await database.RebuildPlainEvidenceWithoutProviderMessageIdAsync(ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Fact]
    public async Task Schema_readiness_requires_issue_d_indexes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        database.Copy017();
        await database.Runner.ApplyPendingAsync(ct);
        Assert.True(await database.Runner.IsCurrentSchemaReadyAsync(ct));

        await database.ExecuteAsync(
            "DROP INDEX ix_recipient_delivery_events_provider_message;",
            [],
            ct);

        Assert.False(await database.Runner.IsCurrentSchemaReadyAsync(ct));
    }

    [Fact]
    public async Task Migration_017_rolls_back_when_request_candidate_is_not_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(request, "event-ambiguous-request", "Bounced", database.Now, ct);
        await database.DuplicateRequestByRebuildingWithoutUniqueConstraintAsync(request.RequestId, ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Fact]
    public async Task Migration_017_rolls_back_when_canonical_recipient_is_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: false, ct);
        await database.InsertLegacyEventAsync(request, "event-no-recipient", "Bounced", database.Now, ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Fact]
    public async Task Migration_017_rolls_back_when_canonical_recipient_is_not_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertRecipientAsync(request.RequestId, role: 1, ordinal: 0, "cc@example.com", ct);
        await database.InsertLegacyEventAsync(request, "event-many-recipients", "Bounced", database.Now, ct);
        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Migration_017_rolls_back_when_processing_precondition_fails(bool processingRequest)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: true, ct);
        if (processingRequest)
        {
            await database.ExecuteAsync(
                "UPDATE mail_requests SET status = 1 WHERE id = @Id;",
                [new("@Id", request.RequestId)],
                ct);
        }
        else
        {
            await database.InsertProcessingInboxAsync(ct);
        }

        database.Copy017();

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Runner.ApplyPendingAsync(ct));

        await database.Assert017RolledBackAsync(ct);
    }

    [Fact]
    public async Task Migration_017_cancellation_rolls_back_schema_data_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre017Async(ct);
        var request = await database.InsertRequestAsync(includeRecipient: true, ct);
        await database.InsertLegacyEventAsync(request, "event-cancel", "Bounced", database.Now, ct);
        database.Copy017();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        database.Runner.BeforeMigrationCommitForTests = token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                database.Runner.ApplyPendingAsync(cts.Token));
        }
        finally
        {
            database.Runner.BeforeMigrationCommitForTests = null;
        }

        await database.Assert017RolledBackAsync(ct);
    }

    private sealed record RequestIdentity(string RequestId, string TenantId, string MailRequestId);

    private sealed class MigrationDatabase : IAsyncDisposable
    {
        private readonly string root;

        private MigrationDatabase(
            string root,
            string migrationDirectory,
            SqliteConnectionFactory factory,
            SqlMigrationRunner runner)
        {
            this.root = root;
            MigrationDirectory = migrationDirectory;
            Factory = factory;
            Runner = runner;
            Now = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 8, 6, 1, 0, 0, TimeSpan.Zero));
            Later = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 8, 6, 2, 0, 0, TimeSpan.Zero));
        }

        public string MigrationDirectory { get; }

        public SqliteConnectionFactory Factory { get; }

        public SqlMigrationRunner Runner { get; }

        public string Now { get; }

        public string Later { get; }

        public static async Task<MigrationDatabase> CreatePre017Async(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-recipient-event-migration",
                Guid.NewGuid().ToString("N"));
            var migrationDirectory = Path.Combine(root, "migrations");
            Directory.CreateDirectory(migrationDirectory);
            var current = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
            foreach (var source in Directory.GetFiles(current, "*.sql", SearchOption.TopDirectoryOnly)
                         .Where(path => string.CompareOrdinal(
                             Path.GetFileName(path),
                             "016_recipient_persistence_and_plain_submission_evidence.sql") <= 0))
            {
                File.Copy(source, Path.Combine(migrationDirectory, Path.GetFileName(source)));
            }

            var databasePath = Path.Combine(root, "mailer.db");
            var factory = new SqliteConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath};Pooling=False",
                    })
                    .Build());
            var runner = new SqlMigrationRunner(factory, migrationDirectory);
            await runner.ApplyPendingAsync(cancellationToken);
            return new MigrationDatabase(root, migrationDirectory, factory, runner);
        }

        public void Copy017()
        {
            var source = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "Migrations",
                "017_recipient_delivery_events.sql");
            File.Copy(source, Path.Combine(MigrationDirectory, Path.GetFileName(source)));
        }

        public async Task<RequestIdentity> InsertRequestAsync(
            bool includeRecipient,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("D");
            var tenantId = Guid.NewGuid().ToString("D");
            var mailRequestId = Guid.NewGuid().ToString("D");
            await ExecuteAsync(
                """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts, attachment_count,
                    accepted_at, created_at, updated_at)
                VALUES (
                    @Id, @TenantId, 'migration-017-test', @MailRequestId, 'test',
                    '{}', @PayloadHash, 'subject', 'to@example.com',
                    0, 0, 3, 0,
                    @Now, @Now, @Now);
                """,
                [
                    new("@Id", requestId),
                    new("@TenantId", tenantId),
                    new("@MailRequestId", mailRequestId),
                    new("@PayloadHash", new string('a', 64)),
                    new("@Now", Now),
                ],
                cancellationToken);

            if (includeRecipient)
            {
                await InsertRecipientAsync(requestId, role: 0, ordinal: 0, "to@example.com", cancellationToken);
            }

            return new RequestIdentity(requestId, tenantId, mailRequestId);
        }

        public Task InsertRecipientAsync(
            string requestId,
            int role,
            int ordinal,
            string address,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                """
                INSERT INTO mail_request_recipients (
                    request_id, recipient_role, ordinal, address, address_key,
                    display_name, delivery_state, created_at, updated_at)
                VALUES (
                    @RequestId, @Role, @Ordinal, @Address, @Address,
                    NULL, 1, @Now, @Now);
                """,
                [
                    new("@RequestId", requestId),
                    new("@Role", role),
                    new("@Ordinal", ordinal),
                    new("@Address", address),
                    new("@Now", Now),
                ],
                cancellationToken);

        public Task InsertLegacyEventAsync(
            RequestIdentity request,
            string eventId,
            string status,
            string occurredAt,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                """
                INSERT INTO bounce_events (
                    id, tenant_id, source_service, mail_request_id,
                    provider, provider_event_id, provider_message_id,
                    delivery_status, status_message, occurred_at, created_at)
                VALUES (
                    @Id, @TenantId, 'migration-017-test', @MailRequestId,
                    'acs', @EventId, 'message-017',
                    @Status, 'sanitized', @OccurredAt, @Now);
                """,
                [
                    new("@Id", Guid.NewGuid().ToString("D")),
                    new("@TenantId", request.TenantId),
                    new("@MailRequestId", request.MailRequestId),
                    new("@EventId", eventId),
                    new("@Status", status),
                    new("@OccurredAt", occurredAt),
                    new("@Now", Now),
                ],
                cancellationToken);

        public Task InsertProcessingInboxAsync(CancellationToken cancellationToken) =>
            ExecuteAsync(
                """
                INSERT INTO provider_event_inbox (
                    id, provider, event_id, status, attempt_count, max_attempts,
                    lock_token, lock_expires_at, created_at, updated_at)
                VALUES (
                    @Id, 'acs', @EventId, 1, 1, 3,
                    @LockToken, @ExpiresAt, @Now, @Now);
                """,
                [
                    new("@Id", Guid.NewGuid().ToString("D")),
                    new("@EventId", Guid.NewGuid().ToString("D")),
                    new("@LockToken", Guid.NewGuid().ToString("D")),
                    new("@ExpiresAt", Later),
                    new("@Now", Now),
                ],
                cancellationToken);

        public async Task DuplicateRequestByRebuildingWithoutUniqueConstraintAsync(
            string requestId,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                CREATE TABLE mail_requests_without_constraints AS SELECT * FROM mail_requests;
                DROP TABLE mail_requests;
                ALTER TABLE mail_requests_without_constraints RENAME TO mail_requests;
                INSERT INTO mail_requests SELECT * FROM mail_requests WHERE id = @RequestId;
                """;
            command.Parameters.AddWithValue("@RequestId", requestId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task RebuildPlainEvidenceWithoutProviderMessageIdAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                CREATE TABLE mail_plain_submissions_incomplete AS
                    SELECT request_id, evidence_state, evidence_origin, provider, claim_token,
                           started_at, resolved_at, created_at, updated_at
                    FROM mail_plain_submissions;
                DROP TABLE mail_plain_submissions;
                ALTER TABLE mail_plain_submissions_incomplete RENAME TO mail_plain_submissions;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ExecuteAsync(
            string sql,
            IReadOnlyList<SqliteParameter> parameters,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<long> ReadCountAsync(string sql, CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task Assert017RolledBackAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(1L, await ReadCountAsync(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'bounce_events';",
                cancellationToken));
            Assert.Equal(0L, await ReadCountAsync(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recipient_delivery_events';",
                cancellationToken));
            Assert.Equal(0L, await ReadCountAsync(
                "SELECT COUNT(*) FROM schema_migrations WHERE version = '017_recipient_delivery_events.sql';",
                cancellationToken));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
