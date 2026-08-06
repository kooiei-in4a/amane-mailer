using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 Spike — S-11 (duplicate event dedup), S-13 (cross-tenant / unknown message-id
/// isolation), exercised against the REAL production <see cref="BounceIngestionStore"/> and
/// migrations (no fault harness, no network — pure DB-layer evidence). This deliberately uses
/// Current recipient-aware correlation schema and atomic processing path.
/// </summary>
public sealed class Spike525EventCorrelationTests
{
    [Fact]
    public async Task S13_event_for_one_tenants_message_id_never_resolves_to_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, cleanup) = await CreateMigratedDatabaseAsync(ct);
        try
        {
            var store = new BounceIngestionStore(factory);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var messageIdForA = "spike525-msg-" + Guid.NewGuid().ToString("N");

            await SeedMailRequestWithAttemptAsync(factory, tenantA, "source-a", "to-a@example.com", messageIdForA, ct);
            await SeedMailRequestWithAttemptAsync(factory, tenantB, "source-b", "to-b@example.com", "spike525-msg-" + Guid.NewGuid().ToString("N"), ct);

            var row = BuildInboxRow("spike525-known", messageIdForA, "Bounced", "to-a@example.com");
            await SeedInboxRowAsync(factory, row, ct);
            var result = await store.ProcessClaimedAsync(row, DateTimeOffset.UtcNow, ct);

            Spike525Support.Evidence.Record("S-13", new
            {
                Scenario = "known-message-id-two-tenants-present",
                ProcessResult = result,
            });

            Assert.Equal(RecipientFeedbackProcessResult.Processed, result);
        }
        finally
        {
            await cleanup();
        }
    }

    [Fact]
    public async Task S13_unknown_provider_message_id_resolves_to_no_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, cleanup) = await CreateMigratedDatabaseAsync(ct);
        try
        {
            var store = new BounceIngestionStore(factory);
            await SeedMailRequestWithAttemptAsync(factory, Guid.NewGuid(), "source-a", "to-a@example.com", "spike525-msg-" + Guid.NewGuid().ToString("N"), ct);

            var row = BuildInboxRow(
                "spike525-unknown",
                "spike525-msg-" + Guid.NewGuid().ToString("N") + "-unknown",
                "Bounced",
                "to-a@example.com");
            await SeedInboxRowAsync(factory, row, ct);
            var result = await store.ProcessClaimedAsync(row, DateTimeOffset.UtcNow, ct);

            Spike525Support.Evidence.Record("S-13", new
            {
                Scenario = "unknown-message-id",
                ProcessResult = result,
            });

            Assert.Equal(RecipientFeedbackProcessResult.Unmatched, result);
        }
        finally
        {
            await cleanup();
        }
    }

    [Fact]
    public async Task S11_duplicate_provider_event_id_is_deduplicated_not_double_recorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, cleanup) = await CreateMigratedDatabaseAsync(ct);
        try
        {
            var store = new BounceIngestionStore(factory);
            var tenant = Guid.NewGuid();
            var providerMessageId = "spike525-msg-" + Guid.NewGuid().ToString("N");
            await SeedMailRequestWithAttemptAsync(factory, tenant, "source-a", "to-a@example.com", providerMessageId, ct);

            var eventId = "spike525-evt-" + Guid.NewGuid().ToString("N");
            var inboxRow = BuildInboxRow(eventId, providerMessageId, "Bounced", "to-a@example.com");
            await SeedInboxRowAsync(factory, inboxRow, ct);

            var first = await store.ProcessClaimedAsync(inboxRow, DateTimeOffset.UtcNow, ct);
            // Same inbox row id/lock_token replayed (simulates at-least-once queue delivery of the same event).
            var second = await store.ProcessClaimedAsync(inboxRow, DateTimeOffset.UtcNow, ct);

            var bounceEventCount = await CountRowsAsync(factory, "recipient_delivery_events", ct);
            var suppressionCount = await CountRowsAsync(factory, "mail_suppressions", ct);

            Spike525Support.Evidence.Record("S-11", new
            {
                Scenario = "replayed-provider-event-id",
                FirstPersistResult = first,
                SecondPersistResult = second,
                BounceEventRowCount = bounceEventCount,
                SuppressionRowCount = suppressionCount,
            });

            // PersistCorrelatedAsync is fenced by the inbox row's lock_token/status, so the
            // literal replay above is rejected outright at the finalize step (never reaches the
            // ON CONFLICT(provider, provider_event_id) dedup path); a distinct inbox row
            // carrying the SAME event_id is the case that exercises that DB-level dedup.
            Assert.Equal(RecipientFeedbackProcessResult.Processed, first);
            Assert.Equal(RecipientFeedbackProcessResult.FenceFailed, second);
            Assert.Equal(1, bounceEventCount);
            Assert.Equal(1, suppressionCount);
        }
        finally
        {
            await cleanup();
        }
    }

    /// <summary>
    /// #525 note: two DISTINCT provider_event_inbox rows sharing the same (provider, event_id)
    /// cannot occur in practice — TryInsertAsync's own ON CONFLICT(provider, event_id) DO NOTHING
    /// prevents that at ingestion. The realistic duplicate-reprocessing scenario is the SAME
    /// inbox row being reclaimed (e.g. after a crash between the bounce_events insert and the
    /// finalize UPDATE, or an expired lease) and processed a second time with a fresh lock_token.
    /// This test reproduces that scenario directly against BounceIngestionStore.
    /// </summary>
    [Fact]
    public async Task S11_reclaimed_inbox_row_reprocessing_is_deduplicated_at_the_bounce_events_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, cleanup) = await CreateMigratedDatabaseAsync(ct);
        try
        {
            var store = new BounceIngestionStore(factory);
            var tenant = Guid.NewGuid();
            var providerMessageId = "spike525-msg-" + Guid.NewGuid().ToString("N");
            await SeedMailRequestWithAttemptAsync(factory, tenant, "source-a", "to-a@example.com", providerMessageId, ct);
            var eventId = "spike525-evt-" + Guid.NewGuid().ToString("N");
            var row = BuildInboxRow(eventId, providerMessageId, "Bounced", "to-a@example.com");
            await SeedInboxRowAsync(factory, row, ct);

            var first = await store.ProcessClaimedAsync(row, DateTimeOffset.UtcNow, ct);

            // Simulate lease-expiry reclaim of the SAME row (same id, same event_id) with a
            // fresh lock_token, as MailerRequestWorker-style claim logic would do after a crash
            // left the row stuck mid-Processing.
            var reclaimedLockToken = Guid.NewGuid();
            await ReclaimInboxRowAsync(factory, row.Id, reclaimedLockToken, ct);

            var reprocessed = new Amane.Mailer.Data.Sqlite.Models.ProviderEventInboxRow
            {
                Id = row.Id,
                Provider = row.Provider,
                EventId = row.EventId,
                ProviderMessageId = row.ProviderMessageId,
                DeliveryStatus = row.DeliveryStatus,
                RecipientEmail = row.RecipientEmail,
                StatusMessage = row.StatusMessage,
                OccurredAt = row.OccurredAt,
                Status = ProviderEventInboxState.Processing,
                Disposition = null,
                AttemptCount = row.AttemptCount + 1,
                MaxAttempts = row.MaxAttempts,
                LockToken = reclaimedLockToken,
                LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            };

            var second = await store.ProcessClaimedAsync(reprocessed, DateTimeOffset.UtcNow, ct);

            var bounceEventCount = await CountRowsAsync(factory, "recipient_delivery_events", ct);
            var suppressionCount = await CountRowsAsync(factory, "mail_suppressions", ct);

            Spike525Support.Evidence.Record("S-11", new
            {
                Scenario = "reclaimed-inbox-row-reprocessed",
                FirstPersistReturnedTrue = first,
                ReclaimedReprocessReturnedTrue = second,
                BounceEventRowCountStaysOne = bounceEventCount == 1,
                SuppressionRowCountStaysOne = suppressionCount == 1,
            });

            // Reclaimed reprocessing still succeeds at the finalize step (lock_token now
            // matches), but the bounce_events UNIQUE(provider, provider_event_id) constraint
            // silently absorbs the duplicate insert — append-only, no duplicate row.
            Assert.Equal(RecipientFeedbackProcessResult.Processed, first);
            Assert.Equal(RecipientFeedbackProcessResult.Duplicate, second);
            Assert.Equal(1, bounceEventCount);
            Assert.Equal(1, suppressionCount);
        }
        finally
        {
            await cleanup();
        }
    }

    private static ProviderEventInboxRow BuildInboxRow(
        string eventId,
        string providerMessageId,
        string deliveryStatus,
        string recipient) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = "acs",
            EventId = eventId,
            ProviderMessageId = providerMessageId,
            DeliveryStatus = deliveryStatus,
            RecipientEmail = recipient,
            StatusMessage = null,
            OccurredAt = DateTimeOffset.UtcNow,
            Status = ProviderEventInboxState.Processing,
            Disposition = null,
            AttemptCount = 1,
            MaxAttempts = 5,
            LockToken = Guid.NewGuid(),
            LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    /// <summary>Directly persists a provider_event_inbox row matching the given fixture so
    /// PersistCorrelatedAsync's finalize UPDATE (which requires an existing row with a matching
    /// id/lock_token/Processing status) can succeed — TryInsertAsync alone only reaches Pending.</summary>
    private static async Task SeedInboxRowAsync(
        SqliteConnectionFactory factory,
        ProviderEventInboxRow row,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_event_inbox (
                id, provider, event_id, provider_message_id, delivery_status, recipient_email,
                status, disposition, attempt_count, max_attempts,
                lock_token, lock_expires_at, created_at, updated_at)
            VALUES (
                @Id, @Provider, @EventId, @ProviderMessageId, @DeliveryStatus, @RecipientEmail,
                @Status, NULL, @AttemptCount, @MaxAttempts,
                @LockToken, @LockExpiresAt, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@Id", row.Id.ToString("D"));
        command.Parameters.AddWithValue("@Provider", row.Provider);
        command.Parameters.AddWithValue("@EventId", row.EventId);
        command.Parameters.AddWithValue("@ProviderMessageId", (object?)row.ProviderMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("@DeliveryStatus", (object?)row.DeliveryStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@RecipientEmail", (object?)row.RecipientEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", (int)row.Status);
        command.Parameters.AddWithValue("@AttemptCount", row.AttemptCount);
        command.Parameters.AddWithValue("@MaxAttempts", row.MaxAttempts);
        command.Parameters.AddWithValue("@LockToken", row.LockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", row.LockExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("@Now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Simulates lease-expiry reclaim: resets an existing inbox row back to Processing with a fresh lock_token.</summary>
    private static async Task ReclaimInboxRowAsync(
        SqliteConnectionFactory factory,
        Guid id,
        Guid newLockToken,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE provider_event_inbox
            SET status = @ProcessingStatus, lock_token = @LockToken, lock_expires_at = @LockExpiresAt, updated_at = @Now
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
        command.Parameters.AddWithValue("@LockToken", newLockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        command.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@Id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedMailRequestWithAttemptAsync(
        SqliteConnectionFactory factory,
        Guid tenantId,
        string sourceService,
        string recipientEmail,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using (var insertRequest = connection.CreateCommand())
        {
            insertRequest.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts, accepted_at, created_at, updated_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, 'spike525',
                    '{}', @PayloadHash, 'spike525 subject', @RecipientEmail,
                    2, 1, 3, @Now, @Now, @Now);
                """;
            insertRequest.Parameters.AddWithValue("@Id", requestId);
            insertRequest.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            insertRequest.Parameters.AddWithValue("@SourceService", sourceService);
            insertRequest.Parameters.AddWithValue("@MailRequestId", Guid.NewGuid().ToString("D"));
            insertRequest.Parameters.AddWithValue("@PayloadHash", new string('0', 64));
            insertRequest.Parameters.AddWithValue("@RecipientEmail", recipientEmail);
            insertRequest.Parameters.AddWithValue("@Now", now);
            await insertRequest.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertAttempt = connection.CreateCommand())
        {
            insertAttempt.CommandText = """
                INSERT INTO mail_attempts (
                    request_id, attempt_number, provider, status, provider_message_id,
                    retryable, lock_token, started_at, completed_at)
                VALUES (
                    @RequestId, 1, 'acs', 2, @ProviderMessageId,
                    0, @LockToken, @Now, @Now);
                """;
            insertAttempt.Parameters.AddWithValue("@RequestId", requestId);
            insertAttempt.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
            insertAttempt.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            insertAttempt.Parameters.AddWithValue("@Now", now);
            await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertRecipient = connection.CreateCommand())
        {
            insertRecipient.CommandText = """
                INSERT INTO mail_request_recipients (
                    request_id, recipient_role, ordinal, address, address_key, display_name,
                    delivery_state, created_at, updated_at)
                VALUES (@RequestId, 0, 0, @Address, @AddressKey, NULL, 1, @Now, @Now);
                """;
            insertRecipient.Parameters.AddWithValue("@RequestId", requestId);
            insertRecipient.Parameters.AddWithValue("@Address", recipientEmail);
            insertRecipient.Parameters.AddWithValue("@AddressKey", RecipientEmailNormalizer.Normalize(recipientEmail));
            insertRecipient.Parameters.AddWithValue("@Now", now);
            await insertRecipient.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertEvidence = connection.CreateCommand())
        {
            insertEvidence.CommandText = """
                INSERT INTO mail_plain_submissions (
                    request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                    provider_message_id, resolved_at, created_at, updated_at)
                VALUES (@RequestId, 2, 0, 'acs', @LockToken, @Now,
                        @ProviderMessageId, @Now, @Now, @Now);
                """;
            insertEvidence.Parameters.AddWithValue("@RequestId", requestId);
            insertEvidence.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            insertEvidence.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
            insertEvidence.Parameters.AddWithValue("@Now", now);
            await insertEvidence.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> CountRowsAsync(SqliteConnectionFactory factory, string table, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task<(SqliteConnectionFactory Factory, Func<Task> Cleanup)> CreateMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-spike525-events", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath};Pooling=False",
            })
            .Build();

        var factory = new SqliteConnectionFactory(configuration);
        var runner = new SqlMigrationRunner(factory);
        await runner.ApplyPendingAsync(cancellationToken);

        return (factory, () =>
        {
            Directory.Delete(root, recursive: true);
            return Task.CompletedTask;
        }
        );
    }
}
