using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class RecipientFeedbackCorrelationTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-08-06T12:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_and_attachment_evidence_use_the_same_feedback_path(bool attachment)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            attachment,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "target@example.com", MailRecipientDeliveryState.Pending)],
            ct);

        var beforeEvidence = await db.ReadEvidenceSnapshotAsync(seeded.RequestId, attachment, ct);
        var beforeAttempts = await db.CountAsync("mail_attempts", ct);
        var result = await db.ProcessAsync(seeded, "event-delivered", "Delivered", "target@example.com", FixedNow, ct);

        Assert.Equal(RecipientFeedbackProcessResult.Processed, result);
        Assert.Equal(MailRecipientDeliveryState.Delivered, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));
        Assert.Equal(beforeEvidence, await db.ReadEvidenceSnapshotAsync(seeded.RequestId, attachment, ct));
        Assert.Equal(beforeAttempts, await db.CountAsync("mail_attempts", ct));
        Assert.Equal(MailRequestState.Delivered, await db.ReadRequestStateAsync(seeded.RequestId, ct));
    }

    [Fact]
    public async Task Correlation_preserves_role_and_ordinal_for_To_Cc_and_Bcc()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            attachment: false,
            MailRequestState.Delivered,
            [
                new RecipientSeed(MailRecipientRole.To, 0, "to-0@example.com", MailRecipientDeliveryState.Pending),
                new RecipientSeed(MailRecipientRole.To, 1, "to-1@example.com", MailRecipientDeliveryState.Pending),
                new RecipientSeed(MailRecipientRole.Cc, 0, "cc-0@example.com", MailRecipientDeliveryState.Pending),
                new RecipientSeed(MailRecipientRole.Bcc, 0, "bcc-pii-canary@example.com", MailRecipientDeliveryState.Pending),
            ],
            ct);

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(
                seeded,
                "event-bcc",
                "Bounced",
                "  BCC-PII-CANARY@EXAMPLE.COM  ",
                FixedNow,
                ct));

        var history = Assert.Single(await db.ReadHistoryAsync(ct));
        Assert.Equal((int)MailRecipientRole.Bcc, history.Role);
        Assert.Equal(0, history.Ordinal);
        Assert.Equal(MailRecipientDeliveryState.Bounced, history.AppliedState);
        Assert.Equal(MailRecipientDeliveryState.Bounced, await db.ReadStateAsync(seeded.RequestId, 2, 0, ct));
        Assert.Equal(MailRecipientDeliveryState.Pending, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));
        Assert.Equal(MailRecipientDeliveryState.Pending, await db.ReadStateAsync(seeded.RequestId, 0, 1, ct));
        Assert.Equal(MailRecipientDeliveryState.Pending, await db.ReadStateAsync(seeded.RequestId, 1, 0, ct));
        Assert.True(await db.SuppressionExistsAsync(seeded.TenantId, "bcc-pii-canary@example.com", ct));
    }

    [Theory]
    [InlineData(MailRecipientDeliveryState.Pending, "Delivered", MailRecipientDeliveryState.Delivered)]
    [InlineData(MailRecipientDeliveryState.Delivered, "Bounced", MailRecipientDeliveryState.Bounced)]
    [InlineData(MailRecipientDeliveryState.Delivered, "Suppressed", MailRecipientDeliveryState.Suppressed)]
    [InlineData(MailRecipientDeliveryState.Bounced, "Delivered", MailRecipientDeliveryState.Bounced)]
    [InlineData(MailRecipientDeliveryState.Suppressed, "Delivered", MailRecipientDeliveryState.Suppressed)]
    [InlineData(MailRecipientDeliveryState.Failed, "Delivered", MailRecipientDeliveryState.Delivered)]
    [InlineData(MailRecipientDeliveryState.Unknown, "Delivered", MailRecipientDeliveryState.Delivered)]
    public async Task Recipient_state_transition_follows_negative_sticky_matrix(
        MailRecipientDeliveryState initial,
        string providerStatus,
        MailRecipientDeliveryState expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "state@example.com", initial)],
            ct);

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-state", providerStatus, "state@example.com", FixedNow, ct));
        Assert.Equal(expected, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));

        var history = Assert.Single(await db.ReadHistoryAsync(ct));
        var changed = expected != initial;
        Assert.Equal(changed ? expected : null, history.AppliedState);
    }

    [Fact]
    public async Task Negative_ordering_is_occurrence_time_then_ordinal_event_id_not_arrival_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "order@example.com", MailRecipientDeliveryState.Delivered)],
            ct);

        var later = FixedNow.AddMinutes(2);
        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-m", "Bounced", "order@example.com", later, ct));
        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-old", "Suppressed", "order@example.com", FixedNow, ct));
        Assert.Equal(MailRecipientDeliveryState.Bounced, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-a", "Suppressed", "order@example.com", later, ct));
        Assert.Equal(MailRecipientDeliveryState.Bounced, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-z", "Suppressed", "order@example.com", later, ct));
        Assert.Equal(MailRecipientDeliveryState.Suppressed, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));

        var history = await db.ReadHistoryAsync(ct);
        Assert.Equal(4, history.Count);
        Assert.Single(history, row => row.AppliedState == MailRecipientDeliveryState.Bounced);
        Assert.Single(history, row => row.AppliedState == MailRecipientDeliveryState.Suppressed);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Quarantined")]
    [InlineData("Complaint")]
    [InlineData("FutureStatus")]
    public async Task History_only_status_does_not_change_current_state_or_suppress(string providerStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.Cc, 0, "history@example.com", MailRecipientDeliveryState.Pending)],
            ct);

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-history", providerStatus, "history@example.com", FixedNow, ct));
        Assert.Equal(MailRecipientDeliveryState.Pending, await db.ReadStateAsync(seeded.RequestId, 1, 0, ct));
        Assert.Null(Assert.Single(await db.ReadHistoryAsync(ct)).AppliedState);
        Assert.False(await db.SuppressionExistsAsync(seeded.TenantId, "history@example.com", ct));
    }

    [Theory]
    [InlineData(MailRequestState.Delivered, "Bounced")]
    [InlineData(MailRequestState.DeliveryUnknown, "Delivered")]
    [InlineData(MailRequestState.Cancelled, "Suppressed")]
    [InlineData(MailRequestState.Failed, "Delivered")]
    public async Task Late_feedback_never_changes_request_aggregate(
        MailRequestState requestState,
        string providerStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            requestState,
            [new RecipientSeed(MailRecipientRole.To, 0, "late@example.com", MailRecipientDeliveryState.Pending)],
            ct);

        Assert.Equal(
            RecipientFeedbackProcessResult.Processed,
            await db.ProcessAsync(seeded, "event-late", providerStatus, "late@example.com", FixedNow, ct));
        Assert.Equal(requestState, await db.ReadRequestStateAsync(seeded.RequestId, ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_message_collision_across_tenant_or_source_fails_closed(bool sameTenant)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var firstTenant = Guid.NewGuid();
        var first = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "collision@example.com", MailRecipientDeliveryState.Pending)],
            ct,
            providerMessageId: "collision-message",
            tenantId: firstTenant);
        _ = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "collision@example.com", MailRecipientDeliveryState.Pending)],
            ct,
            providerMessageId: "collision-message",
            sourceService: sameTenant ? "other-source" : first.SourceService,
            tenantId: sameTenant ? firstTenant : Guid.NewGuid());

        var claimed = await db.ClaimAsync(first, "event-collision", "Bounced", "collision@example.com", FixedNow, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BounceIngestionStore(db.Factory).ProcessClaimedAsync(claimed, FixedNow, ct));
        Assert.Equal(0, await db.CountAsync("recipient_delivery_events", ct));
        Assert.Equal(ProviderEventInboxState.Processing, await db.ReadInboxStateAsync(claimed.Id, ct));
    }

    [Fact]
    public async Task Exact_provider_and_recipient_are_required()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "exact@example.com", MailRecipientDeliveryState.Pending)],
            ct);

        Assert.Equal(
            RecipientFeedbackProcessResult.Unmatched,
            await db.ProcessAsync(seeded, "event-provider", "Bounced", "exact@example.com", FixedNow, ct, "mailpit"));
        Assert.Equal(
            RecipientFeedbackProcessResult.RecipientMismatch,
            await db.ProcessAsync(seeded, "event-recipient", "Bounced", "other@example.com", FixedNow, ct));
        Assert.Equal(0, await db.CountAsync("recipient_delivery_events", ct));
        Assert.False(await db.SuppressionExistsAsync(seeded.TenantId, "exact@example.com", ct));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("recipient")]
    [InlineData("recipient_zero")]
    [InlineData("suppression")]
    [InlineData("finalize")]
    public async Task Write_failure_rolls_back_every_feedback_side_effect(string failureStage)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var seeded = await db.SeedRequestAsync(
            false,
            MailRequestState.Delivered,
            [new RecipientSeed(MailRecipientRole.To, 0, "rollback@example.com", MailRecipientDeliveryState.Pending)],
            ct);
        var claimed = await db.ClaimAsync(seeded, "event-rollback", "Bounced", "rollback@example.com", FixedNow, ct);
        await db.InstallFailureTriggerAsync(failureStage, ct);

        if (failureStage == "recipient_zero")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new BounceIngestionStore(db.Factory).ProcessClaimedAsync(claimed, FixedNow, ct));
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => new BounceIngestionStore(db.Factory).ProcessClaimedAsync(claimed, FixedNow, ct));
        }

        Assert.Equal(0, await db.CountAsync("recipient_delivery_events", ct));
        Assert.Equal(MailRecipientDeliveryState.Pending, await db.ReadStateAsync(seeded.RequestId, 0, 0, ct));
        Assert.False(await db.SuppressionExistsAsync(seeded.TenantId, "rollback@example.com", ct));
        Assert.Equal(ProviderEventInboxState.Processing, await db.ReadInboxStateAsync(claimed.Id, ct));
    }

    private sealed record RecipientSeed(
        MailRecipientRole Role,
        int Ordinal,
        string Address,
        MailRecipientDeliveryState State);

    private sealed record SeededRequest(
        Guid RequestId,
        Guid TenantId,
        string SourceService,
        Guid MailRequestId,
        string Provider,
        string ProviderMessageId);

    private sealed record HistoryRow(
        int Role,
        int Ordinal,
        MailRecipientDeliveryState? AppliedState);

    private sealed class TestDatabase(string root, SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public SqliteConnectionFactory Factory { get; } = factory;

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-recipient-feedback", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={Path.Combine(root, "mailer.db")};Pooling=False",
                })
                .Build();
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new TestDatabase(root, factory);
        }

        public async Task<SeededRequest> SeedRequestAsync(
            bool attachment,
            MailRequestState requestState,
            IReadOnlyList<RecipientSeed> recipients,
            CancellationToken cancellationToken,
            string? providerMessageId = null,
            string sourceService = "feedback-tests",
            Guid? tenantId = null)
        {
            var requestId = Guid.NewGuid();
            tenantId ??= Guid.NewGuid();
            var mailRequestId = Guid.NewGuid();
            const string provider = "acs";
            providerMessageId ??= "message-" + Guid.NewGuid().ToString("N");
            var now = SqliteTime.ToStorageUtc(FixedNow);

            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using (var request = connection.CreateCommand())
            {
                request.CommandText = """
                    INSERT INTO mail_requests (
                        id, tenant_id, source_service, mail_request_id, purpose,
                        payload_json, payload_hash, subject, recipient_email,
                        status, attempt_count, max_attempts, attachment_count,
                        accepted_at, created_at, updated_at, completed_at)
                    VALUES (
                        @Id, @TenantId, @SourceService, @MailRequestId, 'feedback-test',
                        '{}', @PayloadHash, 'subject', 'legacy-shadow@example.invalid',
                        @Status, 1, 3, @AttachmentCount,
                        @Now, @Now, @Now, @Now);
                    """;
                request.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                request.Parameters.AddWithValue("@TenantId", tenantId.Value.ToString("D"));
                request.Parameters.AddWithValue("@SourceService", sourceService);
                request.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
                request.Parameters.AddWithValue("@PayloadHash", new string('a', 64));
                request.Parameters.AddWithValue("@Status", (int)requestState);
                request.Parameters.AddWithValue("@AttachmentCount", attachment ? 1 : 0);
                request.Parameters.AddWithValue("@Now", now);
                await request.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var row in recipients)
            {
                await using var recipient = connection.CreateCommand();
                recipient.CommandText = """
                    INSERT INTO mail_request_recipients (
                        request_id, recipient_role, ordinal, address, address_key, display_name,
                        delivery_state, created_at, updated_at)
                    VALUES (
                        @RequestId, @Role, @Ordinal, @Address, @AddressKey, 'ignored display name',
                        @State, @Now, @Now);
                    """;
                recipient.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
                recipient.Parameters.AddWithValue("@Role", (int)row.Role);
                recipient.Parameters.AddWithValue("@Ordinal", row.Ordinal);
                recipient.Parameters.AddWithValue("@Address", row.Address);
                recipient.Parameters.AddWithValue("@AddressKey", RecipientEmailNormalizer.Normalize(row.Address));
                recipient.Parameters.AddWithValue("@State", (int)row.State);
                recipient.Parameters.AddWithValue("@Now", now);
                await recipient.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var evidence = connection.CreateCommand();
            evidence.CommandText = attachment
                ? """
                    INSERT INTO mail_attachment_submissions (
                        request_id, submission_state, provider, submission_started_at, lock_token,
                        provider_message_id, completed_at, created_at, updated_at)
                    VALUES (@RequestId, 1, @Provider, @Now, @LockToken,
                            @ProviderMessageId, @Now, @Now, @Now);
                    """
                : """
                    INSERT INTO mail_plain_submissions (
                        request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                        provider_message_id, resolved_at, created_at, updated_at)
                    VALUES (@RequestId, 2, 0, @Provider, @LockToken, @Now,
                            @ProviderMessageId, @Now, @Now, @Now);
                    """;
            evidence.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            evidence.Parameters.AddWithValue("@Provider", provider);
            evidence.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            evidence.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);
            evidence.Parameters.AddWithValue("@Now", now);
            await evidence.ExecuteNonQueryAsync(cancellationToken);

            return new SeededRequest(requestId, tenantId.Value, sourceService, mailRequestId, provider, providerMessageId);
        }

        public async Task<RecipientFeedbackProcessResult> ProcessAsync(
            SeededRequest request,
            string eventId,
            string status,
            string recipient,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken,
            string? provider = null)
        {
            var claimed = await ClaimAsync(
                request,
                eventId,
                status,
                recipient,
                occurredAt,
                cancellationToken,
                provider);
            return await new BounceIngestionStore(Factory).ProcessClaimedAsync(claimed, FixedNow, cancellationToken);
        }

        public async Task<ProviderEventInboxRow> ClaimAsync(
            SeededRequest request,
            string eventId,
            string status,
            string recipient,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken,
            string? provider = null)
        {
            var inbox = new ProviderEventInboxRepository(Factory);
            Assert.True(await inbox.TryInsertAsync(
                new ProviderEventInboxInsert
                {
                    Id = Guid.CreateVersion7(FixedNow),
                    Provider = provider ?? request.Provider,
                    EventId = eventId,
                    ProviderMessageId = request.ProviderMessageId,
                    DeliveryStatus = status,
                    RecipientEmail = recipient,
                    StatusMessage = "sanitized detail",
                    OccurredAt = occurredAt,
                    MaxAttempts = 3,
                    CreatedAt = FixedNow,
                },
                cancellationToken));
            return Assert.IsType<ProviderEventInboxRow>(
                await inbox.TryClaimOneAsync(FixedNow, TimeSpan.FromMinutes(1), cancellationToken));
        }

        public async Task InstallFailureTriggerAsync(string stage, CancellationToken cancellationToken)
        {
            var sql = stage switch
            {
                "event" => """
                    CREATE TRIGGER fail_feedback_event BEFORE INSERT ON recipient_delivery_events
                    BEGIN SELECT RAISE(ABORT, 'event failure'); END;
                    """,
                "recipient" => """
                    CREATE TRIGGER fail_feedback_recipient BEFORE UPDATE ON mail_request_recipients
                    BEGIN SELECT RAISE(ABORT, 'recipient failure'); END;
                    """,
                "recipient_zero" => """
                    CREATE TRIGGER ignore_feedback_recipient BEFORE UPDATE ON mail_request_recipients
                    BEGIN SELECT RAISE(IGNORE); END;
                    """,
                "suppression" => """
                    CREATE TRIGGER fail_feedback_suppression BEFORE INSERT ON mail_suppressions
                    BEGIN SELECT RAISE(ABORT, 'suppression failure'); END;
                    """,
                "finalize" => """
                    CREATE TRIGGER fail_feedback_finalize BEFORE UPDATE ON provider_event_inbox
                    WHEN NEW.status = 2
                    BEGIN SELECT RAISE(ABORT, 'finalize failure'); END;
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            };
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<HistoryRow>> ReadHistoryAsync(CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT recipient_role, recipient_ordinal, applied_delivery_state
                FROM recipient_delivery_events
                ORDER BY occurred_at, provider_event_id;
                """;
            var rows = new List<HistoryRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new HistoryRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : (MailRecipientDeliveryState)reader.GetInt32(2)));
            }

            return rows;
        }

        public async Task<MailRecipientDeliveryState> ReadStateAsync(
            Guid requestId,
            int role,
            int ordinal,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT delivery_state FROM mail_request_recipients
                WHERE request_id = @RequestId AND recipient_role = @Role AND ordinal = @Ordinal;
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            command.Parameters.AddWithValue("@Role", role);
            command.Parameters.AddWithValue("@Ordinal", ordinal);
            return (MailRecipientDeliveryState)Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async Task<MailRequestState> ReadRequestStateAsync(Guid requestId, CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM mail_requests WHERE id = @Id;";
            command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            return (MailRequestState)Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async Task<ProviderEventInboxState> ReadInboxStateAsync(Guid id, CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM provider_event_inbox WHERE id = @Id;";
            command.Parameters.AddWithValue("@Id", id.ToString("D"));
            return (ProviderEventInboxState)Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async Task<string> ReadEvidenceSnapshotAsync(
            Guid requestId,
            bool attachment,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = attachment
                ? """
                    SELECT submission_state || '|' || provider || '|' || provider_message_id || '|' || lock_token
                    FROM mail_attachment_submissions WHERE request_id = @RequestId;
                    """
                : """
                    SELECT evidence_state || '|' || evidence_origin || '|' || provider || '|' || provider_message_id || '|' || claim_token
                    FROM mail_plain_submissions WHERE request_id = @RequestId;
                    """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<bool> SuppressionExistsAsync(
            Guid tenantId,
            string address,
            CancellationToken cancellationToken) =>
            await new MailSuppressionRepository(Factory).ExistsAsync(tenantId, address, cancellationToken);

        public async Task<int> CountAsync(string table, CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Run(() => Directory.Delete(root, recursive: true));
        }
    }
}
