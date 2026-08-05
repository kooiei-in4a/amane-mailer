using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class RecipientPersistenceMigrationTests
{
    [Fact]
    public async Task Migration_016_backfills_classifications_and_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);

        var noEvidence = await database.InsertRequestAsync(
            status: MailRequestState.Queued,
            attemptCount: 0,
            attachmentCount: 0,
            cancellationToken: cancellationToken);
        var accepted = await database.InsertRequestAsync(
            status: MailRequestState.Delivered,
            attemptCount: 1,
            attachmentCount: 0,
            deliveredAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(accepted, MailRequestState.Delivered, null, cancellationToken);
        await database.InsertBounceAsync(accepted, cancellationToken);

        var definitelyRejected = await database.InsertRequestAsync(
            status: MailRequestState.Failed,
            attemptCount: 1,
            attachmentCount: 0,
            failedAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            definitelyRejected,
            MailRequestState.Failed,
            MailDeliveryErrorCodes.AcsSendFailed,
            cancellationToken);

        var unknown = await database.InsertRequestAsync(
            status: MailRequestState.Failed,
            attemptCount: 1,
            attachmentCount: 0,
            failedAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            unknown,
            MailRequestState.Failed,
            MailDeliveryErrorCodes.ProviderTimeout,
            cancellationToken);

        var acceptedWithMismatchedAttemptTimestamp = await database.InsertRequestAsync(
            status: MailRequestState.Delivered,
            attemptCount: 1,
            attachmentCount: 0,
            deliveredAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            acceptedWithMismatchedAttemptTimestamp,
            MailRequestState.Delivered,
            null,
            cancellationToken,
            completedAt: SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var acceptedWithSupersededAttempt = await database.InsertRequestAsync(
            status: MailRequestState.Delivered,
            attemptCount: 1,
            attachmentCount: 0,
            deliveredAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            acceptedWithSupersededAttempt,
            MailRequestState.Delivered,
            MailRequestRepository.SupersededByManualRetryErrorCode,
            cancellationToken);

        var attachmentSucceeded = await database.InsertRequestAsync(
            status: MailRequestState.Delivered,
            attemptCount: 1,
            attachmentCount: 1,
            deliveredAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            attachmentSucceeded,
            MailRequestState.Delivered,
            null,
            cancellationToken);
        await database.InsertAttachmentSubmissionAsync(
            attachmentSucceeded,
            AttachmentSubmissionState.Succeeded,
            cancellationToken);

        var attachmentDefinitelyFailed = await database.InsertRequestAsync(
            status: MailRequestState.Failed,
            attemptCount: 1,
            attachmentCount: 1,
            failedAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            attachmentDefinitelyFailed,
            MailRequestState.Failed,
            MailDeliveryErrorCodes.AcsSendFailed,
            cancellationToken);
        await database.InsertAttachmentSubmissionAsync(
            attachmentDefinitelyFailed,
            AttachmentSubmissionState.DefinitiveFailed,
            cancellationToken);

        var attachmentUnknown = await database.InsertRequestAsync(
            status: MailRequestState.DeliveryUnknown,
            attemptCount: 1,
            attachmentCount: 1,
            deliveryUnknownAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttemptAsync(
            attachmentUnknown,
            MailRequestState.Failed,
            MailDeliveryErrorCodes.DeliveryUnknown,
            cancellationToken);
        await database.InsertAttachmentSubmissionAsync(
            attachmentUnknown,
            AttachmentSubmissionState.Unknown,
            cancellationToken);

        var attachmentWithoutEvidence = await database.InsertRequestAsync(
            status: MailRequestState.Queued,
            attemptCount: 0,
            attachmentCount: 1,
            cancellationToken: cancellationToken);

        Assert.False(await database.Runner.IsCurrentSchemaReadyAsync(cancellationToken));
        await database.Copy016Async();

        var applied = await database.Runner.ApplyPendingAsync(cancellationToken);
        Assert.Contains("016_recipient_persistence_and_plain_submission_evidence.sql", applied);
        Assert.True(await database.Runner.IsCurrentSchemaReadyAsync(cancellationToken));

        AssertRecipient(await database.ReadRecipientAsync(noEvidence, cancellationToken), 0);
        AssertRecipient(await database.ReadRecipientAsync(accepted, cancellationToken), 3);
        AssertRecipient(await database.ReadRecipientAsync(definitelyRejected, cancellationToken), 5);
        AssertRecipient(await database.ReadRecipientAsync(unknown, cancellationToken), 6);
        AssertRecipient(await database.ReadRecipientAsync(attachmentSucceeded, cancellationToken), 1);
        AssertRecipient(await database.ReadRecipientAsync(attachmentDefinitelyFailed, cancellationToken), 5);
        AssertRecipient(await database.ReadRecipientAsync(attachmentUnknown, cancellationToken), 6);
        AssertRecipient(await database.ReadRecipientAsync(attachmentWithoutEvidence, cancellationToken), 0);
        Assert.Equal(
            MailRequestState.DeliveryUnknown,
            await database.ReadRequestStatusAsync(unknown, cancellationToken));
        Assert.Equal(
            MailRequestState.DeliveryUnknown,
            await database.ReadRequestStatusAsync(acceptedWithMismatchedAttemptTimestamp, cancellationToken));
        Assert.Equal(
            MailRequestState.DeliveryUnknown,
            await database.ReadRequestStatusAsync(acceptedWithSupersededAttempt, cancellationToken));

        Assert.Null(await database.ReadPlainEvidenceAsync(noEvidence, cancellationToken));
        Assert.Equal(
            ((int)MailPlainSubmissionEvidenceState.Accepted, (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill),
            await database.ReadPlainEvidenceAsync(accepted, cancellationToken));
        Assert.Equal(
            ((int)MailPlainSubmissionEvidenceState.DefinitelyRejected, (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill),
            await database.ReadPlainEvidenceAsync(definitelyRejected, cancellationToken));
        Assert.Equal(
            ((int)MailPlainSubmissionEvidenceState.Unknown, (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill),
            await database.ReadPlainEvidenceAsync(unknown, cancellationToken));
        Assert.Equal(
            ((int)MailPlainSubmissionEvidenceState.Unknown, (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill),
            await database.ReadPlainEvidenceAsync(acceptedWithMismatchedAttemptTimestamp, cancellationToken));
        Assert.Equal(
            ((int)MailPlainSubmissionEvidenceState.Unknown, (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill),
            await database.ReadPlainEvidenceAsync(acceptedWithSupersededAttempt, cancellationToken));
        Assert.Null(await database.ReadPlainEvidenceAsync(attachmentSucceeded, cancellationToken));
        Assert.Null(await database.ReadPlainEvidenceAsync(attachmentWithoutEvidence, cancellationToken));

        Assert.Equal(
            0L,
            await database.ReadCountAsync(
                "SELECT COUNT(*) FROM mail_plain_submissions WHERE evidence_origin = 1 AND evidence_state IN (0, 1);",
                cancellationToken));
        Assert.Equal(
            0L,
            await database.ReadCountAsync(
                """
                SELECT COUNT(*)
                FROM mail_plain_submissions ps
                JOIN mail_requests mr ON mr.id = ps.request_id
                WHERE mr.attachment_count > 0;
                """,
                cancellationToken));

        var secondApply = await database.Runner.ApplyPendingAsync(cancellationToken);
        Assert.Empty(secondApply);
        Assert.Equal(10L, await database.ReadCountAsync("SELECT COUNT(*) FROM mail_request_recipients;", cancellationToken));
        Assert.Equal(5L, await database.ReadCountAsync("SELECT COUNT(*) FROM mail_plain_submissions;", cancellationToken));

        await File.AppendAllTextAsync(
            Path.Combine(database.MigrationDirectory, "016_recipient_persistence_and_plain_submission_evidence.sql"),
            Environment.NewLine + "-- checksum drift test",
            cancellationToken);
        Assert.False(await database.Runner.IsCurrentSchemaReadyAsync(cancellationToken));
    }

    [Fact]
    public async Task Migration_016_fails_closed_for_processing_requests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);
        await database.InsertRequestAsync(
            status: MailRequestState.Processing,
            attemptCount: 0,
            attachmentCount: 0,
            cancellationToken: cancellationToken);
        await database.Copy016Async();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Runner.ApplyPendingAsync(cancellationToken));

        await database.Assert016RolledBackAsync(cancellationToken);
    }

    [Fact]
    public async Task Migration_016_fails_closed_for_started_attachment_submission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);
        var requestId = await database.InsertRequestAsync(
            status: MailRequestState.Queued,
            attemptCount: 0,
            attachmentCount: 1,
            cancellationToken: cancellationToken);
        await database.InsertAttachmentSubmissionAsync(
            requestId,
            AttachmentSubmissionState.Started,
            cancellationToken);
        await database.Copy016Async();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Runner.ApplyPendingAsync(cancellationToken));

        await database.Assert016RolledBackAsync(cancellationToken);
    }

    [Fact]
    public async Task Migration_016_fails_closed_for_invalid_legacy_recipient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);
        await database.InsertRequestAsync(
            status: MailRequestState.Queued,
            attemptCount: 0,
            attachmentCount: 0,
            recipientEmail: "invalid-recipient",
            cancellationToken: cancellationToken);
        await database.Copy016Async();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Runner.ApplyPendingAsync(cancellationToken));

        await database.Assert016RolledBackAsync(cancellationToken);
    }

    [Fact]
    public async Task Migration_016_fails_closed_for_non_initial_attachment_without_submission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);
        await database.InsertRequestAsync(
            status: MailRequestState.Delivered,
            attemptCount: 1,
            attachmentCount: 1,
            deliveredAt: database.Now,
            cancellationToken: cancellationToken);
        await database.Copy016Async();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Runner.ApplyPendingAsync(cancellationToken));

        await database.Assert016RolledBackAsync(cancellationToken);
    }

    [Fact]
    public async Task Migration_016_fails_closed_for_attachment_submission_request_state_mismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await MigrationDatabase.CreatePre016Async(cancellationToken);
        var requestId = await database.InsertRequestAsync(
            status: MailRequestState.Failed,
            attemptCount: 1,
            attachmentCount: 1,
            failedAt: database.Now,
            cancellationToken: cancellationToken);
        await database.InsertAttachmentSubmissionAsync(
            requestId,
            AttachmentSubmissionState.Succeeded,
            cancellationToken);
        await database.Copy016Async();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Runner.ApplyPendingAsync(cancellationToken));

        await database.Assert016RolledBackAsync(cancellationToken);
    }

    private static void AssertRecipient(
        RecipientRow? row,
        int expectedState)
    {
        Assert.NotNull(row);
        Assert.Equal(0, row.Role);
        Assert.Equal(0, row.Ordinal);
        Assert.Equal(expectedState, row.State);
    }

    private sealed record RecipientRow(int Role, int Ordinal, string Address, string AddressKey, int State);

    private sealed class MigrationDatabase : IAsyncDisposable
    {
        private MigrationDatabase(
            string root,
            string databasePath,
            string migrationDirectory,
            SqliteConnectionFactory factory,
            SqlMigrationRunner runner)
        {
            Root = root;
            DatabasePath = databasePath;
            MigrationDirectory = migrationDirectory;
            Factory = factory;
            Runner = runner;
            Now = SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public string MigrationDirectory { get; }

        public SqliteConnectionFactory Factory { get; }

        public SqlMigrationRunner Runner { get; }

        public string Now { get; }

        public static async Task<MigrationDatabase> CreatePre016Async(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-recipient-migration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var migrationDirectory = Path.Combine(root, "migrations");
            CopyMigrationsThrough(migrationDirectory, "015_attachment_spool_and_submission_evidence.sql");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory, migrationDirectory);
            await runner.ApplyPendingAsync(cancellationToken);
            return new MigrationDatabase(root, databasePath, migrationDirectory, factory, runner);
        }

        public async Task<Guid> InsertRequestAsync(
            MailRequestState status,
            int attemptCount,
            int attachmentCount,
            string? deliveredAt = null,
            string? failedAt = null,
            string? deliveryUnknownAt = null,
            string? completedAt = null,
            string? recipientEmail = null,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            var tenantId = Guid.NewGuid();
            var mailRequestId = Guid.NewGuid();
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email, recipient_display_name,
                    status, attempt_count, max_attempts, delivered_at, failed_at,
                    attachment_count, accepted_at, created_at, updated_at, completed_at,
                    delivery_unknown_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, 'test',
                    '{}', @PayloadHash, 'subject', @RecipientEmail, NULL,
                    @Status, @AttemptCount, 3, @DeliveredAt, @FailedAt,
                    @AttachmentCount, @Now, @Now, @Now, @CompletedAt,
                    @DeliveryUnknownAt);
                """;
            command.Parameters.AddWithValue("@Id", id.ToString("D"));
            command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
            command.Parameters.AddWithValue("@SourceService", "recipient-migration-test");
            command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
            command.Parameters.AddWithValue("@PayloadHash", new string('b', 64));
            command.Parameters.AddWithValue("@RecipientEmail", recipientEmail ?? $"{id:N}@example.com");
            command.Parameters.AddWithValue("@Status", (int)status);
            command.Parameters.AddWithValue("@AttemptCount", attemptCount);
            command.Parameters.AddWithValue("@DeliveredAt", (object?)deliveredAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@FailedAt", (object?)failedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@AttachmentCount", attachmentCount);
            command.Parameters.AddWithValue("@Now", Now);
            command.Parameters.AddWithValue(
                "@CompletedAt",
                (object?)(completedAt ?? deliveredAt ?? failedAt ?? deliveryUnknownAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("@DeliveryUnknownAt", (object?)deliveryUnknownAt ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);

            requestMetadata[id] = new RequestMetadata(tenantId, mailRequestId);
            return id;
        }

        public async Task InsertAttemptAsync(
            Guid requestId,
            MailRequestState status,
            string? errorCode,
            CancellationToken cancellationToken,
            string? completedAt = null)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_attempts (
                    request_id, attempt_number, provider, status, error_code, error_message,
                    retryable, lock_token, started_at, completed_at)
                VALUES (
                    @RequestId, 1, 'mailpit', @Status, @ErrorCode, NULL,
                    0, @LockToken, @Now, @CompletedAt);
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            command.Parameters.AddWithValue("@Status", (int)status);
            command.Parameters.AddWithValue("@ErrorCode", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@Now", Now);
            command.Parameters.AddWithValue("@CompletedAt", completedAt ?? Now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task InsertAttachmentSubmissionAsync(
            Guid requestId,
            AttachmentSubmissionState state,
            CancellationToken cancellationToken,
            string? completedAt = null)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mail_attachment_submissions (
                    request_id, submission_state, provider, submission_started_at,
                    lock_token, provider_message_id, completed_at, created_at, updated_at)
                VALUES (
                    @RequestId, @State, 'mailpit', @Now, @LockToken, NULL, @CompletedAt, @Now, @Now);
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            command.Parameters.AddWithValue("@State", (int)state);
            command.Parameters.AddWithValue("@Now", Now);
            command.Parameters.AddWithValue("@LockToken", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@CompletedAt", (object?)completedAt ?? Now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task InsertBounceAsync(Guid requestId, CancellationToken cancellationToken)
        {
            var metadata = requestMetadata[requestId];
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO bounce_events (
                    id, tenant_id, source_service, mail_request_id, provider,
                    provider_event_id, provider_message_id, delivery_status,
                    status_message, occurred_at, created_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, 'mailpit',
                    @ProviderEventId, @ProviderMessageId, 'bounced', NULL, @Now, @Now);
                """;
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@TenantId", metadata.TenantId.ToString("D"));
            command.Parameters.AddWithValue("@SourceService", "recipient-migration-test");
            command.Parameters.AddWithValue("@MailRequestId", metadata.MailRequestId.ToString("D"));
            command.Parameters.AddWithValue("@ProviderEventId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@ProviderMessageId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@Now", Now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<RecipientRow?> ReadRecipientAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT recipient_role, ordinal, address, address_key, delivery_state
                FROM mail_request_recipients
                WHERE request_id = @RequestId;
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new RecipientRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4))
                : null;
        }

        public async Task<(int State, int Origin)?> ReadPlainEvidenceAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT evidence_state, evidence_origin
                FROM mail_plain_submissions
                WHERE request_id = @RequestId;
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? (reader.GetInt32(0), reader.GetInt32(1))
                : null;
        }

        public async Task<MailRequestState> ReadRequestStatusAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            await using var connection = await Factory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status
                FROM mail_requests
                WHERE id = @RequestId;
                """;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            return (MailRequestState)Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
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

        public async Task Copy016Async()
        {
            var source = Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "Migrations",
                "016_recipient_persistence_and_plain_submission_evidence.sql");
            File.Copy(source, Path.Combine(MigrationDirectory, Path.GetFileName(source)));
            await Task.CompletedTask;
        }

        public async Task Assert016RolledBackAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(
                0L,
                await ReadCountAsync(
                    "SELECT COUNT(*) FROM schema_migrations WHERE version LIKE '016_%';",
                    cancellationToken));
            Assert.Equal(
                0L,
                await ReadCountAsync(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('mail_request_recipients', 'mail_plain_submissions');",
                    cancellationToken));
        }

        private readonly Dictionary<Guid, RequestMetadata> requestMetadata = new();

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

    private sealed record RequestMetadata(Guid TenantId, Guid MailRequestId);

    private static void CopyMigrationsThrough(string destination, string lastVersion)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(
                     Path.Combine(AppContext.BaseDirectory, "Data", "Migrations"),
                     "*.sql",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file)!;
            File.Copy(file, Path.Combine(destination, fileName));
            if (string.Equals(fileName, lastVersion, StringComparison.Ordinal))
            {
                break;
            }
        }
    }
}
