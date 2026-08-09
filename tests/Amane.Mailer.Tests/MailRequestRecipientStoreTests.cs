using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// Repository-level coverage for <see cref="MailRequestRecipientStore.TryApplySuppressionPrecheckAsync"/>
/// (Issue #546 review finding F2): the attachment request suppression precheck must check every
/// canonical To/Cc/Bcc recipient, not only the legacy <c>mail_requests.recipient_email</c> shadow
/// (a single To address) that the attachment dispatch path used before this fix.
/// </summary>
public sealed class MailRequestRecipientStoreTests
{
    [Fact]
    public async Task Not_suppressed_returns_all_canonical_recipients_without_writing_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        var recipients = new[]
        {
            Recipient(MailRecipientRole.To, 0, "to@example.com", null),
            Recipient(MailRecipientRole.Cc, 0, "cc@example.com", null),
            Recipient(MailRecipientRole.Bcc, 0, "bcc@example.com", null),
        };
        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId, recipients), ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryApplyAttachmentSuppressionPrecheckAsync(
            requestId, tenantId, lockToken, attemptNumber: 1, ct);

        Assert.Equal(AttachmentSuppressionPrecheckOutcome.NotSuppressed, result.Outcome);
        Assert.NotNull(result.Recipients);
        Assert.Equal(3, result.Recipients!.Count);

        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Processing, status);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.All(recipientStates.Values, state => Assert.Equal(MailRecipientDeliveryState.NotSent, state));
    }

    /// <summary>
    /// Before the F2 fix, the attachment dispatch path checked only
    /// <c>row.RecipientEmail</c> (the legacy single-To shadow), so a suppressed Bcc-only
    /// recipient was never caught and the provider was invoked anyway. This proves the fix:
    /// a suppression hit on any canonical recipient -- even one the legacy shadow does not
    /// reflect -- converges the whole request atomically without ever reaching the provider.
    /// </summary>
    [Fact]
    public async Task Suppressed_bcc_recipient_converges_all_or_nothing_even_though_legacy_shadow_is_the_to_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        const string suppressedBcc = "bcc-suppressed@example.com";

        var recipients = new[]
        {
            Recipient(MailRecipientRole.To, 0, "to@example.com", null),
            Recipient(MailRecipientRole.Cc, 0, "cc@example.com", null),
            Recipient(MailRecipientRole.Bcc, 0, suppressedBcc, null),
        };
        await repository.InsertAcceptedAsync(CreateInsert(requestId, tenantId, recipients), ct);
        await SeedSuppressionAsync(database.Factory, tenantId, suppressedBcc, ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        var result = await repository.TryApplyAttachmentSuppressionPrecheckAsync(
            requestId, tenantId, lockToken, attemptNumber: 1, ct);

        Assert.Equal(AttachmentSuppressionPrecheckOutcome.Suppressed, result.Outcome);

        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRecipientDeliveryState.NotSent, recipientStates[(MailRecipientRole.To, 0)]);
        Assert.Equal(MailRecipientDeliveryState.NotSent, recipientStates[(MailRecipientRole.Cc, 0)]);
        Assert.Equal(MailRecipientDeliveryState.Suppressed, recipientStates[(MailRecipientRole.Bcc, 0)]);

        var (status, lastErrorMessage) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Failed, status);
        Assert.DoesNotContain(suppressedBcc, lastErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var attempt = await ReadSingleAttemptAsync(database.Factory, requestId, ct);
        Assert.Equal(MailDeliveryErrorCodes.RecipientSuppressed, attempt.ErrorCode);
        Assert.False(attempt.Retryable);
        Assert.DoesNotContain(suppressedBcc, attempt.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The attachment submission evidence lifecycle (ADR 0022 D-08) is out of Issue #546's
        // scope and must stay untouched: a suppression hit here is pre-Started, matching the
        // existing rowless semantics for attachment requests that never reach the provider.
        Assert.Equal(
            0L,
            await ReadScalarAsync(
                database.Factory,
                "SELECT COUNT(*) FROM mail_attachment_submissions WHERE request_id = @Id;",
                ct,
                ("@Id", requestId.ToString("D"))));
    }

    [Fact]
    public async Task Suppression_recipient_partial_update_rolls_back_when_affected_rows_do_not_match()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();
        const string suppressedBcc = "bcc-suppressed@example.com";

        await repository.InsertAcceptedAsync(
            CreateInsert(
                requestId,
                tenantId,
                [
                    Recipient(MailRecipientRole.Bcc, 0, suppressedBcc, null),
                    Recipient(MailRecipientRole.Bcc, 1, "bcc-other@example.com", null),
                ]),
            ct);
        await SeedSuppressionAsync(database.Factory, tenantId, suppressedBcc, ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(5), ct);

        // SQLite's RAISE(IGNORE) gives the UPDATE a safe, production-independent seam that
        // skips one canonical row. The bulk helper must see affected=1 versus expected=2 and
        // roll back the already-started recipient update and terminalization.
        await ExecuteAsync(
            database.Factory,
            $"""
            CREATE TRIGGER skip_one_suppression_recipient_update
            BEFORE UPDATE ON mail_request_recipients
            WHEN NEW.request_id = '{requestId:D}' AND NEW.ordinal = 1
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """,
            ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryApplyAttachmentSuppressionPrecheckAsync(
            requestId,
            tenantId,
            lockToken,
            attemptNumber: 1,
            ct));

        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Processing, status);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.All(recipientStates.Values, state => Assert.Equal(MailRecipientDeliveryState.NotSent, state));
        Assert.Equal(0L, await ReadScalarAsync(
            database.Factory,
            "SELECT COUNT(*) FROM mail_attempts WHERE request_id = @Id;",
            ct,
            ("@Id", requestId.ToString("D"))));
    }

    [Fact]
    public async Task Fence_failed_when_lease_has_already_expired_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var lockToken = Guid.NewGuid();

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, tenantId, [Recipient(MailRecipientRole.To, 0, "to@example.com", null)]),
            ct);
        await ClaimAsProcessingAsync(database.Factory, requestId, lockToken, DateTimeOffset.UtcNow.AddMinutes(-1), ct);

        var result = await repository.TryApplyAttachmentSuppressionPrecheckAsync(
            requestId, tenantId, lockToken, attemptNumber: 1, ct);

        Assert.Equal(AttachmentSuppressionPrecheckOutcome.FenceFailed, result.Outcome);
        var (status, _) = await ReadRequestStatusAsync(database.Factory, requestId, ct);
        Assert.Equal(MailRequestState.Processing, status);
        var recipientStates = await ReadRecipientStatesAsync(database.Factory, requestId, ct);
        Assert.All(recipientStates.Values, state => Assert.Equal(MailRecipientDeliveryState.NotSent, state));
    }

    private static CanonicalMailRecipient Recipient(
        MailRecipientRole role,
        int ordinal,
        string address,
        string? displayName) =>
        new()
        {
            Role = role,
            Ordinal = ordinal,
            Address = address,
            AddressKey = RecipientEmailNormalizer.Normalize(address),
            DisplayName = displayName,
        };

    private static AcceptedMailRequestInsert CreateInsert(
        Guid requestId,
        Guid tenantId,
        IReadOnlyList<CanonicalMailRecipient> recipients) =>
        new()
        {
            Id = requestId,
            TenantId = tenantId,
            SourceService = "recipient-store-test",
            MailRequestId = Guid.NewGuid(),
            Purpose = "test",
            PayloadJson = "{}",
            PayloadHash = new string('a', 64),
            Subject = "subject",
            RecipientEmail = recipients[0].Address,
            RecipientDisplayName = recipients[0].DisplayName,
            MaxAttempts = 3,
            AcceptedAt = DateTimeOffset.UtcNow,
            Recipients = recipients,
        };

    private static async Task SeedSuppressionAsync(
        SqliteConnectionFactory factory,
        Guid tenantId,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        var suppressions = new MailSuppressionRepository(factory);
        Assert.True(await suppressions.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RecipientEmail = recipientEmail,
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken));
    }

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClaimAsProcessingAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        Guid lockToken,
        DateTimeOffset lockExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = @ProcessingStatus,
                attempt_count = attempt_count + 1,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                updated_at = @Now
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@LockExpiresAt", SqliteTime.ToStorageUtc(lockExpiresAt));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<(MailRecipientRole Role, int Ordinal), MailRecipientDeliveryState>> ReadRecipientStatesAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<(MailRecipientRole, int), MailRecipientDeliveryState>();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_role, ordinal, delivery_state
            FROM mail_request_recipients
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states[((MailRecipientRole)reader.GetInt32(0), reader.GetInt32(1))] =
                (MailRecipientDeliveryState)reader.GetInt32(2);
        }

        return states;
    }

    private static async Task<(MailRequestState Status, string? LastErrorMessage)> ReadRequestStatusAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, last_error_message
            FROM mail_requests
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<RecipientStoreAttemptRow> ReadSingleAttemptAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, error_code, error_message, retryable
            FROM mail_attempts
            WHERE request_id = @RequestId
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var row = new RecipientStoreAttemptRow(
            (MailRequestState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3) == 1);
        Assert.False(await reader.ReadAsync(cancellationToken));
        return row;
    }

    private static async Task<long> ReadScalarAsync(
        SqliteConnectionFactory factory,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record RecipientStoreAttemptRow(
        MailRequestState Status,
        string? ErrorCode,
        string? ErrorMessage,
        bool Retryable);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string root, SqliteConnectionFactory factory)
        {
            Root = root;
            Factory = factory;
        }

        public string Root { get; }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-recipient-store",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
            return new TestDatabase(root, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
