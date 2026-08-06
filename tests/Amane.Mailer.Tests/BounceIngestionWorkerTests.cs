using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

public sealed class AcsEventParserTests
{
    [Fact]
    public void ParseOne_keeps_bounced_delivery_report()
    {
        var json = """
            {
              "id": "eg-1",
              "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
              "eventTime": "2026-07-26T00:00:00Z",
              "data": {
                "messageId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "status": "Bounced",
                "recipient": "User@Example.COM",
                "deliveryStatusDetails": {
                  "statusMessage": "550 5.1.10 user unknown};'"
                }
              }
            }
            """;

        var result = AcsEventParser.ParseOne(json);
        Assert.Equal(AcsEventParseOutcome.DeliveryReport, result.Outcome);
        Assert.NotNull(result.Report);
        Assert.Equal("eg-1", result.Report.EventId);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", result.Report.MessageId);
        Assert.Equal("Bounced", result.Report.Status);
        Assert.Equal("User@Example.COM", result.Report.Recipient);
        Assert.Contains("550", result.Report.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T00:00:00Z"), result.Report.OccurredAt);
    }

    [Fact]
    public void ParseOne_keeps_suppressed_delivery_report()
    {
        var json = """
            {
              "id": "eg-suppressed",
              "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
              "eventTime": "2026-07-26T00:00:00Z",
              "data": {
                "messageId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "status": "Suppressed",
                "recipient": "User@Example.COM"
              }
            }
            """;

        var result = AcsEventParser.ParseOne(json);

        Assert.Equal(AcsEventParseOutcome.DeliveryReport, result.Outcome);
        Assert.NotNull(result.Report);
        Assert.Equal("Suppressed", result.Report.Status);
        Assert.Equal("User@Example.COM", result.Report.Recipient);
    }

    [Fact]
    public void ParseOne_keeps_delivered_and_ignores_non_delivery_report_types()
    {
        var delivered = """
            {
              "id": "eg-2",
              "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
              "eventTime": "2026-07-26T00:00:00Z",
              "data": { "messageId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "status": "Delivered", "recipient": "a@example.com" }
            }
            """;
        var deliveredResult = AcsEventParser.ParseOne(delivered);
        Assert.Equal(AcsEventParseOutcome.DeliveryReport, deliveredResult.Outcome);
        Assert.Equal("Delivered", deliveredResult.Report?.Status);

        var other = """
            {
              "id": "eg-3",
              "eventType": "Microsoft.Communication.EmailStatusUpdated",
              "data": { "messageId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "status": "Bounced" }
            }
            """;
        Assert.Equal(AcsEventParseOutcome.Ignored, AcsEventParser.ParseOne(other).Outcome);
    }

    [Fact]
    public void ParseOne_marks_malformed_json_unparseable()
    {
        Assert.Equal(AcsEventParseOutcome.Unparseable, AcsEventParser.ParseOne("{ not-json").Outcome);
    }

    [Fact]
    public void ParseOne_marks_missing_required_fields_unparseable()
    {
        var missingRequiredFields = new[]
        {
            """{"eventType":"Microsoft.Communication.EmailDeliveryReportReceived","data":{"messageId":"message","status":"Failed"}}""",
            """{"id":"event","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","data":{"status":"Failed"}}""",
            """{"id":"event","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","data":{"messageId":"message"}}""",
        };

        Assert.All(
            missingRequiredFields,
            json => Assert.Equal(AcsEventParseOutcome.Unparseable, AcsEventParser.ParseOne(json).Outcome));
    }

    [Fact]
    public void BounceClassifier_suppresses_bounced_and_suppressed_only()
    {
        Assert.True(BounceClassifier.IsHardBounce("Bounced"));
        Assert.False(BounceClassifier.IsHardBounce("Suppressed"));
        Assert.True(BounceClassifier.IsSuppressed("Suppressed"));
        Assert.True(BounceClassifier.ShouldSuppress("Bounced"));
        Assert.True(BounceClassifier.ShouldSuppress("Suppressed"));
        Assert.False(BounceClassifier.IsHardBounce("Suspended"));
        Assert.False(BounceClassifier.IsHardBounce("Failed"));
        Assert.False(BounceClassifier.ShouldSuppress("Failed"));
        Assert.False(BounceClassifier.ShouldSuppress("Quarantined"));
        Assert.True(BounceClassifier.ShouldRecordBounceEvent("Failed"));
        Assert.True(BounceClassifier.ShouldRecordBounceEvent("Delivered"));
    }

    [Theory]
    [InlineData("Delivered", MailRecipientDeliveryState.Delivered)]
    [InlineData("Bounced", MailRecipientDeliveryState.Bounced)]
    [InlineData("Suppressed", MailRecipientDeliveryState.Suppressed)]
    [InlineData("Failed", null)]
    [InlineData("Quarantined", null)]
    [InlineData("Complaint", null)]
    [InlineData("FutureProviderStatus", null)]
    public void BounceClassifier_maps_only_approved_current_states(
        string providerStatus,
        MailRecipientDeliveryState? expected)
    {
        Assert.Equal(expected, BounceClassifier.AppliedRecipientState(providerStatus));
        Assert.True(BounceClassifier.ShouldRecordBounceEvent(providerStatus));
    }
}

public sealed class BounceIngestionWorkerTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-26T12:00:00Z");
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000302");
    private static readonly Guid MailRequestId = Guid.Parse("00000000-0000-0000-0000-000000000303");
    private static readonly Guid RequestRowId = Guid.Parse("00000000-0000-0000-0000-000000000304");
    private const string ProviderMessageId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Recipient = "user@example.com";

    [Fact]
    public async Task Correlated_hard_bounce_records_event_and_suppression()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(NewInboxInsert("event-ok", ProviderMessageId, "Bounced", Recipient), ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        var worker = CreateWorker(db.Factory, metrics);
        await worker.ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(1, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.Equal(0, metrics.CaptureSnapshot().BounceUnmatchedTotal);
        Assert.True(await new BounceEventRepository(db.Factory).ExistsAsync(
            await FindBounceEventIdAsync(db.Factory, "event-ok", ct), ct));
        Assert.True(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
        Assert.False(await inbox.HasPendingWorkAsync(FixedNow.AddMinutes(1), ct));
    }

    [Fact]
    public async Task Correlated_suppressed_status_records_event_and_suppression()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(NewInboxInsert("event-suppressed", ProviderMessageId, "Suppressed", Recipient), ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        var worker = CreateWorker(db.Factory, metrics);
        await worker.ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(1, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.Equal(0, metrics.CaptureSnapshot().BounceUnmatchedTotal);
        Assert.True(await new BounceEventRepository(db.Factory).ExistsAsync(
            await FindBounceEventIdAsync(db.Factory, "event-suppressed", ct), ct));
        Assert.True(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
        Assert.False(await inbox.HasPendingWorkAsync(FixedNow.AddMinutes(1), ct));
    }

    [Fact]
    public async Task Unmatched_message_id_is_discarded_with_metric()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert("event-unmatched", "ffffffff-ffff-ffff-ffff-ffffffffffff", "Bounced", Recipient),
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(0, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.Equal(1, metrics.CaptureSnapshot().BounceUnmatchedTotal);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
    }

    [Fact]
    public async Task Recipient_mismatch_is_discarded_without_suppression()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert("event-mismatch", ProviderMessageId, "Bounced", "other@example.com"),
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(0, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.Equal(1, metrics.CaptureSnapshot().BounceRecipientMismatchTotal);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, "other@example.com", ct));
    }

    [Fact]
    public async Task Correlation_is_exact_match_casing_difference_is_unmatched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert("event-case", ProviderMessageId.ToUpperInvariant(), "Bounced", Recipient),
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(1, metrics.CaptureSnapshot().BounceUnmatchedTotal);
        Assert.Equal(0, metrics.CaptureSnapshot().BounceEventsTotal);
    }

    [Fact]
    public async Task Persist_sanitizes_raw_status_message_before_write()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        const string raw =
            "550 5.1.10 RESOLVER.ADR.RecipientNotFound; recipient pii-canary-302@example.com "
            + "Bearer secret-token-do-not-store};'";
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.CreateVersion7(FixedNow),
                Provider = "acs",
                EventId = "event-sanitize",
                ProviderMessageId = ProviderMessageId,
                DeliveryStatus = "Bounced",
                RecipientEmail = Recipient,
                StatusMessage = raw,
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);
        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await new BounceIngestionStore(db.Factory).ProcessClaimedAsync(claimed, FixedNow, ct));

        await using var connection = await db.Factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status_message
            FROM recipient_delivery_events
            WHERE provider_event_id = @EventId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventId", "event-sanitize");
        var stored = (string?)await command.ExecuteScalarAsync(ct);
        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.DoesNotContain("pii-canary-302@example.com", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token-do-not-store", stored, StringComparison.Ordinal);
        Assert.Equal(ProviderErrorSanitizer.Sanitize(raw), stored);
    }

    [Fact]
    public async Task Worker_carries_inbox_occurred_at_and_status_message_to_bounce_events()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var eventTime = DateTimeOffset.Parse("2026-07-26T10:30:00Z");
        const string raw =
            "550 5.1.10 RESOLVER.ADR.RecipientNotFound; recipient pii-canary-460@example.com "
            + "Bearer secret-token-do-not-store};'";
        var sanitized = ProviderErrorSanitizer.Sanitize(raw);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.CreateVersion7(FixedNow),
                Provider = "acs",
                EventId = "event-carry",
                ProviderMessageId = ProviderMessageId,
                DeliveryStatus = "Bounced",
                RecipientEmail = Recipient,
                StatusMessage = sanitized,
                OccurredAt = eventTime,
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);
        Assert.Equal(eventTime, claimed.OccurredAt);
        Assert.Equal(sanitized, claimed.StatusMessage);

        await CreateWorker(db.Factory, new MailerRuntimeMetrics()).ProcessClaimedEventForTestsAsync(claimed, ct);

        await using var connection = await db.Factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status_message, occurred_at
            FROM recipient_delivery_events
            WHERE provider_event_id = @EventId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventId", "event-carry");
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        var storedMessage = reader.GetString(0);
        var storedOccurred = SqliteTime.FromStorage(reader.GetString(1));
        Assert.Equal(sanitized, storedMessage);
        Assert.Equal(eventTime, storedOccurred);
        Assert.DoesNotContain("pii-canary-460@example.com", storedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token-do-not-store", storedMessage, StringComparison.Ordinal);
        Assert.Equal(ProviderErrorSanitizer.Sanitize(sanitized), storedMessage);
    }

    [Fact]
    public async Task Worker_falls_back_to_processing_time_when_inbox_occurred_at_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert("event-fallback", ProviderMessageId, "Bounced", Recipient),
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);
        Assert.Null(claimed.OccurredAt);

        await CreateWorker(db.Factory, new MailerRuntimeMetrics()).ProcessClaimedEventForTestsAsync(claimed, ct);

        await using var connection = await db.Factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_at
            FROM recipient_delivery_events
            WHERE provider_event_id = @EventId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventId", "event-fallback");
        var stored = (string?)await command.ExecuteScalarAsync(ct);
        Assert.Equal(SqliteTime.ToStorageUtc(FixedNow), stored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Persist_fencing_failure_rolls_back_bounce_and_suppression(bool expireLease)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var eventTime = DateTimeOffset.Parse("2026-07-26T09:00:00Z");
        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.CreateVersion7(FixedNow),
                Provider = "acs",
                EventId = "event-fence",
                ProviderMessageId = ProviderMessageId,
                DeliveryStatus = "Bounced",
                RecipientEmail = Recipient,
                StatusMessage = "sanitized-safe",
                OccurredAt = eventTime,
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        // Invalidate one half of the lease fence before the atomic processing transaction.
        await using (var connection = await db.Factory.OpenConnectionAsync(ct))
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.CommandText = expireLease
                ? "UPDATE provider_event_inbox SET lock_expires_at = @Value WHERE id = @Id;"
                : "UPDATE provider_event_inbox SET lock_token = @Value WHERE id = @Id;";
            invalidate.Parameters.AddWithValue(
                "@Value",
                expireLease
                    ? SqliteTime.ToStorageUtc(FixedNow.AddMinutes(-1))
                    : Guid.NewGuid().ToString("D"));
            invalidate.Parameters.AddWithValue("@Id", claimed.Id.ToString("D"));
            Assert.Equal(1, await invalidate.ExecuteNonQueryAsync(ct));
        }

        Assert.Equal(
            RecipientFeedbackProcessResult.FenceFailed,
            await new BounceIngestionStore(db.Factory).ProcessClaimedAsync(claimed, FixedNow, ct));

        await using var verify = await db.Factory.OpenConnectionAsync(ct);
        await using var bounceCount = verify.CreateCommand();
        bounceCount.CommandText = "SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_event_id = 'event-fence';";
        Assert.Equal(0L, Convert.ToInt64(await bounceCount.ExecuteScalarAsync(ct)));
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Quarantined")]
    public async Task Unconfirmed_status_records_bounce_without_suppression(string deliveryStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert($"event-soft-{deliveryStatus.ToLowerInvariant()}", ProviderMessageId, deliveryStatus, Recipient),
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(claimed, ct);

        Assert.Equal(1, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
    }

    [Fact]
    public async Task Duplicate_provider_event_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        var metrics = new MailerRuntimeMetrics();
        Assert.True(await inbox.TryInsertAsync(NewInboxInsert("event-dup", ProviderMessageId, "Bounced", Recipient), ct));
        var first = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(first);

        // Model a previously committed history row with an inbox row that must converge on replay.
        await using (var connection = await db.Factory.OpenConnectionAsync(ct))
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO recipient_delivery_events (
                    id, tenant_id, source_service, mail_request_id,
                    recipient_role, recipient_ordinal,
                    provider, provider_event_id, provider_message_id, provider_status,
                    applied_delivery_state, status_message, occurred_at, created_at)
                VALUES (
                    @Id, @TenantId, 'orders', @MailRequestId,
                    0, 0,
                    'acs', 'event-dup', @ProviderMessageId, 'Bounced',
                    NULL, NULL, @Now, @Now);
                """;
            seed.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
            seed.Parameters.AddWithValue("@TenantId", TenantId.ToString("D"));
            seed.Parameters.AddWithValue("@MailRequestId", MailRequestId.ToString("D"));
            seed.Parameters.AddWithValue("@ProviderMessageId", ProviderMessageId);
            seed.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(FixedNow));
            Assert.Equal(1, await seed.ExecuteNonQueryAsync(ct));
        }

        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(first, ct);

        Assert.Equal(0, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.False(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
        await using var verify = await db.Factory.OpenConnectionAsync(ct);
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM recipient_delivery_events WHERE provider_event_id = 'event-dup'),
                (SELECT delivery_state FROM mail_request_recipients WHERE request_id = @RequestId),
                (SELECT status FROM provider_event_inbox WHERE id = @InboxId);
            """;
        command.Parameters.AddWithValue("@RequestId", RequestRowId.ToString("D"));
        command.Parameters.AddWithValue("@InboxId", first.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal((int)MailRecipientDeliveryState.Pending, reader.GetInt32(1));
        Assert.Equal((int)ProviderEventInboxState.Processed, reader.GetInt32(2));
    }

    [Fact]
    public async Task Shared_operation_id_across_attempts_correlates_to_same_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);
        await SeedExtraAttemptAsync(db.Factory, ProviderMessageId, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(NewInboxInsert("event-retry", ProviderMessageId, "Bounced", Recipient), ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        var metrics = new MailerRuntimeMetrics();
        await CreateWorker(db.Factory, metrics).ProcessClaimedEventForTestsAsync(claimed, ct);
        Assert.Equal(1, metrics.CaptureSnapshot().BounceEventsTotal);
        Assert.True(await new MailSuppressionRepository(db.Factory).ExistsAsync(TenantId, Recipient, ct));
    }

    [Fact]
    public async Task Unparseable_inbox_row_is_discarded_not_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000901"),
                Provider = "acs",
                EventId = "event-unparseable",
                ProviderMessageId = null,
                DeliveryStatus = null,
                RecipientEmail = null,
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));
        var claimed = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);

        await CreateWorker(db.Factory, new MailerRuntimeMetrics()).ProcessClaimedEventForTestsAsync(claimed, ct);
        Assert.False(await inbox.HasPendingWorkAsync(FixedNow.AddMinutes(1), ct));
    }

    [Fact]
    public async Task Process_exception_on_one_event_does_not_stop_next_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        await SeedDeliveredRequestAsync(db.Factory, ProviderMessageId, Recipient, ct);

        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000911"),
                Provider = "acs",
                EventId = "event-bad",
                ProviderMessageId = null,
                DeliveryStatus = null,
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));
        Assert.True(await inbox.TryInsertAsync(
            NewInboxInsert(
                "event-good",
                ProviderMessageId,
                "Bounced",
                Recipient,
                id: Guid.Parse("00000000-0000-0000-0000-000000000912")),
            ct));

        var metrics = new MailerRuntimeMetrics();
        var worker = CreateWorker(db.Factory, metrics);
        worker.WorkStoreOverride = new ThrowingFinalizeOnceStore(inbox);

        var first = await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(first);
        await worker.ProcessClaimedEventForTestsAsync(first, ct);

        worker.WorkStoreOverride = null;
        var second = await inbox.TryClaimOneAsync(FixedNow.AddSeconds(1), TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(second);
        await worker.ProcessClaimedEventForTestsAsync(second, ct);
        Assert.Equal(1, metrics.CaptureSnapshot().BounceEventsTotal);
    }

    [Fact]
    public async Task Metrics_formatter_has_no_tenant_or_recipient_labels_for_bounce()
    {
        var runtime = new MailerRuntimeMetrics();
        runtime.RecordBounceEvent();
        runtime.RecordBounceUnmatched();
        runtime.RecordBounceRecipientMismatch();
        var body = PrometheusMetricsFormatter.Format(
            new MailerDbStatsResult(
                AsOfUtc: FixedNow,
                QueuedCount: 0,
                ProcessingCount: 0,
                DeliveredCount: 0,
                FailedCount: 0,
                DeadLetteredCount: 0,
                ReadyBacklogCount: 0,
                OldestQueuedAgeSeconds: 0,
                QueuedStaleCount: 0,
                StaleProcessingCount: 0,
                ExpiredProcessingCount: 0,
                RecentFailedCount: 0,
                RecentDeadLetteredCount: 0,
                WorkerHeartbeatAgeSeconds: -1,
                SweepHeartbeatAgeSeconds: -1),
            runtime.CaptureSnapshot(),
            providerEventsPending: 2,
            providerEventsDeadLettered: 1);

        Assert.Contains("mail_bounce_events_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_bounce_unmatched_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_bounce_recipient_mismatch_total 1", body, StringComparison.Ordinal);
        Assert.Contains("mail_suppressed_sends_total 0", body, StringComparison.Ordinal);
        Assert.Contains("mail_provider_queue_poll_failed_total 0", body, StringComparison.Ordinal);
        Assert.Contains("mail_provider_queue_payload_invalid_total 0", body, StringComparison.Ordinal);
        Assert.Contains("mail_provider_queue_poisoned_total 0", body, StringComparison.Ordinal);
        Assert.Contains("mail_provider_events_pending 2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant_id=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", body, StringComparison.Ordinal);
    }

    private static BounceIngestionWorker CreateWorker(SqliteConnectionFactory factory, MailerRuntimeMetrics metrics)
    {
        var options = new MailerBounceIngestionOptions { Enabled = true };
        return new BounceIngestionWorker(
            new ProviderEventInboxRepository(factory),
            new BounceIngestionStore(factory),
            options,
            new BounceIngestionQueue(),
            metrics,
            new FixedUtcTimeProvider(FixedNow),
            NullLogger<BounceIngestionWorker>.Instance);
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static ProviderEventInboxInsert NewInboxInsert(
        string eventId,
        string providerMessageId,
        string status,
        string recipient,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.CreateVersion7(FixedNow),
            Provider = "acs",
            EventId = eventId,
            ProviderMessageId = providerMessageId,
            DeliveryStatus = status,
            RecipientEmail = recipient,
            MaxAttempts = 3,
            CreatedAt = FixedNow,
        };

    private static async Task SeedDeliveredRequestAsync(
        SqliteConnectionFactory factory,
        string providerMessageId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(FixedNow);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var request = connection.CreateCommand();
        request.CommandText = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, recipient_email,
                status, attempt_count, max_attempts, accepted_at, created_at, updated_at, completed_at, delivered_at)
            VALUES (
                @Id, @TenantId, 'orders', @MailRequestId, 'notify',
                '{}', '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef', 's', @Recipient,
                2, 1, 3, @Now, @Now, @Now, @Now, @Now);
            """;
        request.Parameters.AddWithValue("@Id", RequestRowId.ToString("D"));
        request.Parameters.AddWithValue("@TenantId", TenantId.ToString("D"));
        request.Parameters.AddWithValue("@MailRequestId", MailRequestId.ToString("D"));
        request.Parameters.AddWithValue("@Recipient", recipientEmail);
        request.Parameters.AddWithValue("@Now", now);
        await request.ExecuteNonQueryAsync(cancellationToken);

        await using var attempt = connection.CreateCommand();
        attempt.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status, provider_message_id,
                error_code, error_message, retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, 1, 'acs', 2, @ProviderMessageId,
                NULL, NULL, 0, @LockToken, @Now, @Now);
            """;
        attempt.Parameters.AddWithValue("@RequestId", RequestRowId.ToString("D"));
        attempt.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
        attempt.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        attempt.Parameters.AddWithValue("@Now", now);
        await attempt.ExecuteNonQueryAsync(cancellationToken);

        await using var recipient = connection.CreateCommand();
        recipient.CommandText = """
            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key, display_name,
                delivery_state, created_at, updated_at)
            VALUES (@RequestId, 0, 0, @Address, @AddressKey, NULL, 1, @Now, @Now);
            """;
        recipient.Parameters.AddWithValue("@RequestId", RequestRowId.ToString("D"));
        recipient.Parameters.AddWithValue("@Address", recipientEmail);
        recipient.Parameters.AddWithValue("@AddressKey", RecipientEmailNormalizer.Normalize(recipientEmail));
        recipient.Parameters.AddWithValue("@Now", now);
        await recipient.ExecuteNonQueryAsync(cancellationToken);

        await using var evidence = connection.CreateCommand();
        evidence.CommandText = """
            INSERT INTO mail_plain_submissions (
                request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                provider_message_id, resolved_at, created_at, updated_at)
            VALUES (@RequestId, 2, 0, 'acs', @LockToken, @Now,
                    @ProviderMessageId, @Now, @Now, @Now);
            """;
        evidence.Parameters.AddWithValue("@RequestId", RequestRowId.ToString("D"));
        evidence.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        evidence.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
        evidence.Parameters.AddWithValue("@Now", now);
        await evidence.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedExtraAttemptAsync(
        SqliteConnectionFactory factory,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        var now = SqliteTime.ToStorageUtc(FixedNow);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var attempt = connection.CreateCommand();
        attempt.CommandText = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status, provider_message_id,
                error_code, error_message, retryable, lock_token, started_at, completed_at)
            VALUES (
                @RequestId, 2, 'acs', 2, @ProviderMessageId,
                NULL, NULL, 0, @LockToken, @Now, @Now);
            """;
        attempt.Parameters.AddWithValue("@RequestId", RequestRowId.ToString("D"));
        attempt.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
        attempt.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
        attempt.Parameters.AddWithValue("@Now", now);
        await attempt.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> FindBounceEventIdAsync(
        SqliteConnectionFactory factory,
        string providerEventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM recipient_delivery_events WHERE provider_event_id = @EventId LIMIT 1;
            """;
        command.Parameters.AddWithValue("@EventId", providerEventId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Guid.Parse((string)value!);
    }

    private static async Task<MigratedDb> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-bounce-worker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath};Pooling=False",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
        return new MigratedDb(root, factory);
    }

    private sealed class MigratedDb(string root, SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public SqliteConnectionFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await Task.Run(() => Directory.Delete(root, recursive: true));
        }
    }

    private sealed class ThrowingFinalizeOnceStore(ProviderEventInboxRepository inner) : IBounceIngestionWorkStore
    {
        private int _finalizeCalls;

        public Task<ProviderEventInboxRow?> TryClaimOneAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            inner.TryClaimOneAsync(now, leaseDuration, cancellationToken);

        public Task<bool> FinalizeAsync(
            Guid id,
            Guid lockToken,
            DateTimeOffset now,
            ProviderEventInboxFinalizeOutcome outcome,
            DateTimeOffset? nextAttemptAt,
            string? lastErrorCode,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _finalizeCalls) == 1)
            {
                throw new InvalidOperationException("simulated finalize failure");
            }

            return inner.FinalizeAsync(id, lockToken, now, outcome, nextAttemptAt, lastErrorCode, cancellationToken);
        }
    }
}
