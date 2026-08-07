using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

internal static class RecipientPersistenceMigration
{
    public const string MigrationVersion = "016_recipient_persistence_and_plain_submission_evidence.sql";

    private const string DeliveryUnknownConvergenceErrorMessage =
        "Provider acceptance could not be confirmed during recovery.";

    public static readonly SqlMigrationRunner.MigrationTransactionStep Step = new(
        ValidatePreconditionBeforeScriptAsync,
        ApplyDataMigrationAfterScriptAsync);

    private enum PlainEvidenceClassification : byte
    {
        NoEvidence,
        Accepted,
        DefinitelyRejected,
        Unknown,
    }

    private sealed record LegacyRequest(
        string Id,
        string TenantId,
        string SourceService,
        string MailRequestId,
        string RecipientEmail,
        string? RecipientDisplayName,
        int AttachmentCount,
        MailRequestState Status,
        int AttemptCount,
        string? DeliveredAt,
        string? FailedAt,
        string? DeliveryUnknownAt,
        string? CompletedAt,
        string CreatedAt);

    private sealed record AttemptEvidence(
        int AttemptNumber,
        MailRequestState Status,
        string? ErrorCode,
        string? CompletedAt);

    private sealed record AttemptSummary(IReadOnlyList<AttemptEvidence> Entries)
    {
        public int Count => Entries.Count;

        public bool HasDeliveredAttemptAt(string? completedAt) =>
            completedAt is not null
            && Entries.Any(attempt =>
                attempt.Status == MailRequestState.Delivered
                && !string.Equals(
                    attempt.ErrorCode,
                    MailRequestConsumerMutations.SupersededByManualRetryErrorCode,
                    StringComparison.Ordinal)
                && string.Equals(attempt.CompletedAt, completedAt, StringComparison.Ordinal));

        public bool HasDefinitiveFailureAttempt(
            int attemptNumber,
            string? failedAt,
            string? completedAt) =>
            failedAt is not null
            && completedAt is not null
            && Entries.Any(attempt =>
                attempt.AttemptNumber == attemptNumber
                && attempt.Status == MailRequestState.Failed
                && string.Equals(attempt.ErrorCode, MailDeliveryErrorCodes.AcsSendFailed, StringComparison.Ordinal)
                && string.Equals(attempt.CompletedAt, failedAt, StringComparison.Ordinal)
                && string.Equals(attempt.CompletedAt, completedAt, StringComparison.Ordinal));

        public bool HasTerminalAttempt(
            int attemptNumber,
            MailRequestState status,
            string? completedAt) =>
            completedAt is not null
            && Entries.Any(attempt =>
                attempt.AttemptNumber == attemptNumber
                && attempt.Status == status
                && string.Equals(attempt.CompletedAt, completedAt, StringComparison.Ordinal));
    }

    private sealed record AttachmentSubmission(
        int State,
        string? CompletedAt);

    private sealed class AttemptSummaryAccumulator
    {
        private readonly List<AttemptEvidence> entries = [];

        public void Add(
            int attemptNumber,
            int status,
            string? errorCode,
            string? completedAt)
        {
            entries.Add(new AttemptEvidence(
                attemptNumber,
                (MailRequestState)status,
                errorCode,
                completedAt));
        }

        public AttemptSummary ToSummary() =>
            new(entries);
    }

    private static async Task ValidatePreconditionBeforeScriptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var processingCount = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM mail_requests WHERE status = 1;",
            cancellationToken);
        if (processingCount != 0)
        {
            throw new InvalidOperationException(
                "Migration 016 requires zero Processing mail requests before backfill.");
        }

        var startedAttachmentSubmissionCount = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM mail_attachment_submissions WHERE submission_state = 0;",
            cancellationToken);
        if (startedAttachmentSubmissionCount != 0)
        {
            throw new InvalidOperationException(
                "Migration 016 requires zero Started attachment submissions before backfill.");
        }
    }

    private static async Task ApplyDataMigrationAfterScriptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var requests = await LoadRequestsAsync(connection, cancellationToken);
        var attemptSummaries = await LoadAttemptSummariesAsync(connection, cancellationToken);
        var bounceKeys = await LoadBounceKeysAsync(connection, cancellationToken);
        var attachmentSubmissionStates = await LoadAttachmentSubmissionStatesAsync(
            connection,
            cancellationToken);
        var requestsById = requests.ToDictionary(request => request.Id, StringComparer.Ordinal);
        foreach (var requestId in attachmentSubmissionStates.Keys)
        {
            if (!requestsById.TryGetValue(requestId, out var request)
                || request.AttachmentCount == 0)
            {
                throw new InvalidOperationException(
                    "Migration 016 found attachment submission evidence for a non-attachment request.");
            }
        }

        var migrationNow = SqliteTime.ToStorageUtc(DateTimeOffset.UtcNow);

        var noEvidenceCount = 0;
        var acceptedCount = 0;
        var definitelyRejectedCount = 0;
        var unknownCount = 0;

        foreach (var request in requests)
        {
            if (!MailRecipientValidator.TryValidate(
                    [new MailRecipientDto
                    {
                        Email = request.RecipientEmail,
                        DisplayName = request.RecipientDisplayName,
                    }],
                    null,
                    null,
                    out var canonicalRecipients,
                    out _)
                || canonicalRecipients is null
                || canonicalRecipients.To.Count != 1)
            {
                throw new InvalidOperationException(
                    "Migration 016 found an invalid legacy recipient value.");
            }

            var attempts = attemptSummaries.TryGetValue(request.Id, out var summary)
                ? summary
                : null;
            var classification = request.AttachmentCount == 0
                ? ClassifyPlainRequest(request, attempts)
                : ClassifyAttachmentRequest(
                    request,
                    attempts,
                    attachmentSubmissionStates);

            if (request.AttachmentCount == 0)
            {
                switch (classification)
                {
                    case PlainEvidenceClassification.NoEvidence:
                        noEvidenceCount++;
                        break;
                    case PlainEvidenceClassification.Accepted:
                        acceptedCount++;
                        break;
                    case PlainEvidenceClassification.DefinitelyRejected:
                        definitelyRejectedCount++;
                        break;
                    case PlainEvidenceClassification.Unknown:
                        unknownCount++;
                        await ConvergeLegacyUnknownRequestAsync(
                            connection,
                            request,
                            migrationNow,
                            cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException("Migration 016 produced an unknown evidence classification.");
                }

                if (classification != PlainEvidenceClassification.NoEvidence)
                {
                    await InsertLegacyPlainEvidenceAsync(
                        connection,
                        request,
                        classification,
                        cancellationToken);
                }
            }

            var recipient = canonicalRecipients.To[0];
            var deliveryState = MapRecipientDeliveryState(
                classification,
                bounceKeys.Contains((request.TenantId, request.SourceService, request.MailRequestId)));
            await InsertRecipientAsync(
                connection,
                request,
                recipient,
                deliveryState,
                cancellationToken);
        }

        await AssertMigrationResultsAsync(
            connection,
            requests,
            noEvidenceCount,
            acceptedCount,
            definitelyRejectedCount,
            unknownCount,
            cancellationToken);
    }

    private static PlainEvidenceClassification ClassifyPlainRequest(
        LegacyRequest request,
        AttemptSummary? attempts)
    {
        if (request.Status == MailRequestState.Queued
            && request.AttemptCount == 0
            && (attempts?.Count ?? 0) == 0
            && request.DeliveredAt is null
            && request.FailedAt is null
            && request.DeliveryUnknownAt is null
            && request.CompletedAt is null)
        {
            return PlainEvidenceClassification.NoEvidence;
        }

        if (request.Status == MailRequestState.Delivered
            && request.DeliveredAt is not null
            && string.Equals(request.CompletedAt, request.DeliveredAt, StringComparison.Ordinal)
            && attempts?.HasDeliveredAttemptAt(request.DeliveredAt) == true)
        {
            return PlainEvidenceClassification.Accepted;
        }

        if (request.Status == MailRequestState.Failed
            && request.FailedAt is not null
            && string.Equals(request.CompletedAt, request.FailedAt, StringComparison.Ordinal)
            && attempts?.HasDefinitiveFailureAttempt(
                request.AttemptCount,
                request.FailedAt,
                request.CompletedAt) == true)
        {
            return PlainEvidenceClassification.DefinitelyRejected;
        }

        return PlainEvidenceClassification.Unknown;
    }

    private static PlainEvidenceClassification ClassifyAttachmentRequest(
        LegacyRequest request,
        AttemptSummary? attempts,
        IReadOnlyDictionary<string, AttachmentSubmission> submissionStates)
    {
        if (!submissionStates.TryGetValue(request.Id, out var submission))
        {
            if (request.Status != MailRequestState.Queued
                || request.AttemptCount != 0
                || (attempts?.Count ?? 0) != 0
                || request.DeliveredAt is not null
                || request.FailedAt is not null
                || request.DeliveryUnknownAt is not null
                || request.CompletedAt is not null)
            {
                throw new InvalidOperationException(
                    "Migration 016 found an attachment request without submission evidence that is not in its initial Queued state.");
            }

            return PlainEvidenceClassification.NoEvidence;
        }

        if (submission.State == (int)AttachmentSubmissionState.Started)
        {
            throw new InvalidOperationException(
                "Migration 016 found a Started attachment submission after its precondition check.");
        }

        if (submission.CompletedAt is null
            || request.CompletedAt is null
            || !string.Equals(submission.CompletedAt, request.CompletedAt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Migration 016 found an attachment submission/request completion timestamp mismatch.");
        }

        switch (submission.State)
        {
            case (int)AttachmentSubmissionState.Succeeded:
                ValidateAttachmentRequestAggregate(
                    request,
                    attempts,
                    MailRequestState.Delivered,
                    request.DeliveredAt,
                    request.FailedAt,
                    request.DeliveryUnknownAt,
                    "delivered");
                return PlainEvidenceClassification.Accepted;
            case (int)AttachmentSubmissionState.DefinitiveFailed:
                ValidateAttachmentRequestAggregate(
                    request,
                    attempts,
                    MailRequestState.Failed,
                    request.FailedAt,
                    request.DeliveredAt,
                    request.DeliveryUnknownAt,
                    "failed");
                return PlainEvidenceClassification.DefinitelyRejected;
            case (int)AttachmentSubmissionState.Unknown:
                ValidateAttachmentRequestAggregate(
                    request,
                    attempts,
                    MailRequestState.DeliveryUnknown,
                    request.DeliveryUnknownAt,
                    request.DeliveredAt,
                    request.FailedAt,
                    "delivery_unknown");
                return PlainEvidenceClassification.Unknown;
            default:
                throw new InvalidOperationException(
                    "Migration 016 found an unsupported attachment submission state.");
        }
    }

    private static void ValidateAttachmentRequestAggregate(
        LegacyRequest request,
        AttemptSummary? attempts,
        MailRequestState expectedStatus,
        string? expectedTimestamp,
        string? conflictingTimestamp1,
        string? conflictingTimestamp2,
        string stateName)
    {
        if (request.Status != expectedStatus
            || request.AttemptCount <= 0
            || (attempts?.Count ?? 0) == 0
            || expectedTimestamp is null
            || conflictingTimestamp1 is not null
            || conflictingTimestamp2 is not null
            || !string.Equals(request.CompletedAt, expectedTimestamp, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Migration 016 found an attachment submission/request aggregate mismatch for {stateName} state.");
        }

        if (attempts?.HasTerminalAttempt(
                request.AttemptCount,
                expectedStatus,
                expectedTimestamp) != true)
        {
            throw new InvalidOperationException(
                $"Migration 016 found an attachment submission/request attempt history mismatch for {stateName} state.");
        }
    }

    private static async Task ConvergeLegacyUnknownRequestAsync(
        SqliteConnection connection,
        LegacyRequest request,
        string migrationNow,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET status = @DeliveryUnknownStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                completed_at = @Now,
                delivered_at = NULL,
                failed_at = NULL,
                delivery_unknown_at = @Now,
                last_error_message = @LastErrorMessage,
                updated_at = @Now
            WHERE id = @Id;
            """;
        command.Parameters.AddWithValue("@DeliveryUnknownStatus", (int)MailRequestState.DeliveryUnknown);
        command.Parameters.AddWithValue("@Now", migrationNow);
        command.Parameters.AddWithValue("@LastErrorMessage", DeliveryUnknownConvergenceErrorMessage);
        command.Parameters.AddWithValue("@Id", request.Id);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Migration 016 could not converge a legacy Unknown request.");
        }
    }

    private static MailRecipientDeliveryState MapRecipientDeliveryState(
        PlainEvidenceClassification classification,
        bool hasBounce)
    {
        return classification switch
        {
            PlainEvidenceClassification.NoEvidence => MailRecipientDeliveryState.NotSent,
            PlainEvidenceClassification.Accepted => hasBounce
                ? MailRecipientDeliveryState.Bounced
                : MailRecipientDeliveryState.Pending,
            PlainEvidenceClassification.DefinitelyRejected => MailRecipientDeliveryState.Failed,
            PlainEvidenceClassification.Unknown => MailRecipientDeliveryState.Unknown,
            _ => throw new InvalidOperationException("Migration 016 produced an unknown recipient state."),
        };
    }

    private static async Task InsertLegacyPlainEvidenceAsync(
        SqliteConnection connection,
        LegacyRequest request,
        PlainEvidenceClassification classification,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_plain_submissions (
                request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                provider_message_id, resolved_at, created_at, updated_at)
            VALUES (
                @RequestId, @EvidenceState, @EvidenceOrigin, NULL, NULL, NULL,
                NULL, @ResolvedAt, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", request.Id);
        command.Parameters.AddWithValue("@EvidenceState", (int)ToEvidenceState(classification));
        command.Parameters.AddWithValue(
            "@EvidenceOrigin",
            (int)MailPlainSubmissionEvidenceOrigin.LegacyBackfill);
        AddNullableParameter(
            command,
            "@ResolvedAt",
            classification switch
            {
                PlainEvidenceClassification.Accepted => request.DeliveredAt,
                PlainEvidenceClassification.DefinitelyRejected => request.FailedAt,
                _ => null,
            });
        command.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);
        command.Parameters.AddWithValue("@UpdatedAt", request.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MailPlainSubmissionEvidenceState ToEvidenceState(
        PlainEvidenceClassification classification) =>
        classification switch
        {
            PlainEvidenceClassification.Accepted => MailPlainSubmissionEvidenceState.Accepted,
            PlainEvidenceClassification.DefinitelyRejected => MailPlainSubmissionEvidenceState.DefinitelyRejected,
            PlainEvidenceClassification.Unknown => MailPlainSubmissionEvidenceState.Unknown,
            PlainEvidenceClassification.NoEvidence => throw new InvalidOperationException(
                "NoEvidence cannot be stored as legacy plain evidence."),
            _ => throw new InvalidOperationException("Migration 016 produced an unknown evidence state."),
        };

    private static async Task InsertRecipientAsync(
        SqliteConnection connection,
        LegacyRequest request,
        CanonicalMailRecipient recipient,
        MailRecipientDeliveryState deliveryState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_request_recipients (
                request_id, recipient_role, ordinal, address, address_key, display_name,
                delivery_state, provider_message_id, provider_status_detail, created_at, updated_at)
            VALUES (
                @RequestId, @RecipientRole, @Ordinal, @Address, @AddressKey, @DisplayName,
                @DeliveryState, NULL, NULL, @CreatedAt, @UpdatedAt);
            """;
        command.Parameters.AddWithValue("@RequestId", request.Id);
        command.Parameters.AddWithValue("@RecipientRole", (int)MailRecipientRole.To);
        command.Parameters.AddWithValue("@Ordinal", 0);
        command.Parameters.AddWithValue("@Address", recipient.Address);
        command.Parameters.AddWithValue(
            "@AddressKey",
            RecipientEmailNormalizer.Normalize(recipient.Address));
        AddNullableParameter(command, "@DisplayName", recipient.DisplayName);
        command.Parameters.AddWithValue("@DeliveryState", (int)deliveryState);
        command.Parameters.AddWithValue("@CreatedAt", request.CreatedAt);
        command.Parameters.AddWithValue("@UpdatedAt", request.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertMigrationResultsAsync(
        SqliteConnection connection,
        IReadOnlyList<LegacyRequest> requests,
        int noEvidenceCount,
        int acceptedCount,
        int definitelyRejectedCount,
        int unknownCount,
        CancellationToken cancellationToken)
    {
        var recipientCount = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM mail_request_recipients;",
            cancellationToken);
        if (recipientCount != requests.Count)
        {
            throw new InvalidOperationException("Migration 016 recipient row count assertion failed.");
        }

        var plainRequestCount = requests.Count(request => request.AttachmentCount == 0);
        if (plainRequestCount != noEvidenceCount + acceptedCount + definitelyRejectedCount + unknownCount)
        {
            throw new InvalidOperationException("Migration 016 plain classification count assertion failed.");
        }

        var plainEvidenceCount = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM mail_plain_submissions;",
            cancellationToken);
        if (plainEvidenceCount != acceptedCount + definitelyRejectedCount + unknownCount)
        {
            throw new InvalidOperationException("Migration 016 plain evidence row count assertion failed.");
        }

        var forbiddenLegacyStateCount = await ReadCountAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM mail_plain_submissions
            WHERE evidence_origin = 1
              AND evidence_state IN (0, 1);
            """,
            cancellationToken);
        if (forbiddenLegacyStateCount != 0)
        {
            throw new InvalidOperationException("Migration 016 created a forbidden legacy evidence state.");
        }

        var attachmentPlainEvidenceCount = await ReadCountAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM mail_plain_submissions ps
            JOIN mail_requests mr ON mr.id = ps.request_id
            WHERE mr.attachment_count > 0;
            """,
            cancellationToken);
        if (attachmentPlainEvidenceCount != 0)
        {
            throw new InvalidOperationException(
                "Migration 016 created plain evidence for an attachment request.");
        }
    }

    private static async Task<IReadOnlyList<LegacyRequest>> LoadRequestsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var requests = new List<LegacyRequest>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, source_service, mail_request_id, recipient_email,
                   recipient_display_name, attachment_count, status, attempt_count,
                   delivered_at, failed_at, delivery_unknown_at, completed_at, created_at
            FROM mail_requests
            ORDER BY id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new LegacyRequest(
                Id: reader.GetString(0),
                TenantId: reader.GetString(1),
                SourceService: reader.GetString(2),
                MailRequestId: reader.GetString(3),
                RecipientEmail: reader.GetString(4),
                RecipientDisplayName: reader.IsDBNull(5) ? null : reader.GetString(5),
                AttachmentCount: reader.GetInt32(6),
                Status: (MailRequestState)reader.GetInt32(7),
                AttemptCount: reader.GetInt32(8),
                DeliveredAt: reader.IsDBNull(9) ? null : reader.GetString(9),
                FailedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
                DeliveryUnknownAt: reader.IsDBNull(11) ? null : reader.GetString(11),
                CompletedAt: reader.IsDBNull(12) ? null : reader.GetString(12),
                CreatedAt: reader.GetString(13)));
        }

        return requests;
    }

    private static async Task<IReadOnlyDictionary<string, AttemptSummary>> LoadAttemptSummariesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var accumulators = new Dictionary<string, AttemptSummaryAccumulator>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id, attempt_number, status, error_code, completed_at
            FROM mail_attempts
            ORDER BY request_id, attempt_number, id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var requestId = reader.GetString(0);
            if (!accumulators.TryGetValue(requestId, out var accumulator))
            {
                accumulator = new AttemptSummaryAccumulator();
                accumulators.Add(requestId, accumulator);
            }

            accumulator.Add(
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        return accumulators.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToSummary(),
            StringComparer.Ordinal);
    }

    private static async Task<HashSet<(string TenantId, string SourceService, string MailRequestId)>>
        LoadBounceKeysAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        var keys = new HashSet<(string TenantId, string SourceService, string MailRequestId)>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT tenant_id, source_service, mail_request_id
            FROM bounce_events;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return keys;
    }

    private static async Task<IReadOnlyDictionary<string, AttachmentSubmission>> LoadAttachmentSubmissionStatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, AttachmentSubmission>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id, submission_state, completed_at
            FROM mail_attachment_submissions;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(
                reader.GetString(0),
                new AttachmentSubmission(
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return states;
    }

    private static async Task<long> ReadCountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count
            ? count
            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddNullableParameter(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
