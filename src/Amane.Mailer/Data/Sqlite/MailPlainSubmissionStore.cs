using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Durable request-unique provider submission evidence for non-attachment requests
/// (ADR 0023 D-04/D-05, Issue #546). Mirrors the request-unique-evidence shape of
/// <see cref="MailAttachmentSubmissionStore"/> (ADR 0022 D-08) but additionally owns the
/// suppression precheck and the canonical <c>mail_request_recipients</c> bulk state transitions,
/// since plain requests have no separate submission-marker step distinct from the evidence row
/// itself.
/// </summary>
public sealed class MailPlainSubmissionStore(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider,
    MailerRuntimeMetrics? runtimeMetrics = null)
{
    private const string RecipientSuppressedMessage = "Recipient is on the suppression list.";
    private const string SuppressionCheckProvider = "none";

    public async Task<MailPlainSubmissionRow?> FindAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await FindWithinConnectionAsync(connection, requestId, cancellationToken);
    }

    /// <summary>
    /// Provider invocation boundary (ADR 0023 D-04) combined with the provider-invocation-time
    /// suppression precheck (D-05, Issue #546 maintainer decision). Both checks and every write
    /// they trigger run inside one fenced SQLite transaction:
    ///
    /// 1. Re-verify this exact claim (<paramref name="lockToken"/>) still owns the row under an
    ///    unexpired lease, read fresh only after BEGIN IMMEDIATE has established write ownership
    ///    (mirrors <see cref="MailAttachmentSubmissionStore.TryInsertStartedAsync"/>'s fencing
    ///    idiom) -- a lease that expires while this call waited on the SQLite write lock is
    ///    caught here, not raced.
    /// 2. If evidence already exists, return it for the caller to converge from (never a second
    ///    provider invocation lifecycle for the same request).
    /// 3. Read all canonical recipients and check them against the tenant-scoped suppression
    ///    list in one pass, using <c>address_key</c> (never the legacy
    ///    <c>mail_requests.recipient_email</c> shadow).
    /// 4. All-or-nothing: if any recipient is suppressed, commit a terminal
    ///    <c>DefinitelyNotSubmitted</c> evidence row + <c>Failed</c> request + per-recipient
    ///    Suppressed/NotSent states + a <c>RECIPIENT_SUPPRESSED</c> attempt row, and return
    ///    without ever calling the provider. Otherwise commit a <c>Started</c> evidence row and
    ///    move every recipient to <c>Pending</c> in the same transaction, and let the caller
    ///    proceed to invoke the provider.
    /// </summary>
    public async Task<PlainProviderInvocationResult> TryPrepareProviderInvocationAsync(
        Guid requestId,
        Guid tenantId,
        string provider,
        Guid lockToken,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var nowUtc = timeProvider.GetUtcNow();
            var nowStorage = SqliteTime.ToStorageUtc(nowUtc);

            if (!await IsFencedProcessingAsync(connection, requestId, lockToken, nowStorage, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new PlainProviderInvocationResult(PlainProviderInvocationOutcome.FenceFailed);
            }

            var existing = await FindWithinConnectionAsync(connection, requestId, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new PlainProviderInvocationResult(
                    PlainProviderInvocationOutcome.ExistingEvidence,
                    ExistingEvidence: existing);
            }

            var recipients = await MailRequestRecipientStore.ListWithinConnectionAsync(
                connection,
                requestId,
                cancellationToken);

            var suppressedKeys = await FindSuppressedAddressKeysAsync(
                connection,
                tenantId,
                recipients,
                cancellationToken);

            if (suppressedKeys.Count > 0)
            {
                await InsertEvidenceAsync(
                    connection,
                    requestId,
                    MailPlainSubmissionEvidenceState.DefinitelyNotSubmitted,
                    provider,
                    lockToken,
                    nowStorage,
                    resolvedAtStorage: nowStorage,
                    cancellationToken);

                await ApplySuppressionRecipientStatesAsync(
                    connection,
                    requestId,
                    suppressedKeys,
                    nowStorage,
                    cancellationToken);

                var attempt = new MailAttemptInsert
                {
                    RequestId = requestId,
                    AttemptNumber = attemptNumber,
                    Provider = SuppressionCheckProvider,
                    Status = MailRequestState.Failed,
                    ProviderMessageId = null,
                    ErrorCode = MailDeliveryErrorCodes.RecipientSuppressed,
                    ErrorMessage = RecipientSuppressedMessage,
                    Retryable = false,
                    LockToken = lockToken,
                    StartedAt = nowUtc,
                    CompletedAt = nowUtc,
                };
                await InsertAttemptAsync(connection, attempt, cancellationToken);

                if (!await TryUpdateRequestTerminalAsync(
                        connection,
                        requestId,
                        lockToken,
                        MailRequestState.Failed,
                        nowStorage,
                        RecipientSuppressedMessage,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Mail request {requestId:D} could not be moved to Failed for the " +
                        "suppression precheck under the fenced claim token.");
                }

                await transaction.CommitAsync(cancellationToken);
                runtimeMetrics?.RecordAttemptCompleted(attempt);
                runtimeMetrics?.RecordSuppressedSend();
                return new PlainProviderInvocationResult(
                    PlainProviderInvocationOutcome.Suppressed,
                    Recipients: recipients);
            }

            await InsertEvidenceAsync(
                connection,
                requestId,
                MailPlainSubmissionEvidenceState.Started,
                provider,
                lockToken,
                nowStorage,
                resolvedAtStorage: null,
                cancellationToken);

            await UpdateAllRecipientsAsync(
                connection,
                requestId,
                MailRecipientDeliveryState.Pending,
                nowStorage,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PlainProviderInvocationResult(
                PlainProviderInvocationOutcome.Started,
                Recipients: recipients);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Atomic terminal finalize to <c>Accepted</c>/<c>DefinitelyRejected</c>/<c>Unknown</c>
    /// (ADR 0023 D-04). <paramref name="requestLockToken"/> fences the current claim on
    /// <c>mail_requests</c>; <paramref name="evidenceClaimToken"/> and
    /// <paramref name="expectedEvidenceState"/> fence the <c>mail_plain_submissions</c> row
    /// against the exact evidence version the caller observed -- these differ from
    /// <paramref name="requestLockToken"/> on a recovery/converge call (a fresh reclaim
    /// finalizing <c>Started</c> evidence created by a prior, crashed claim), exactly as in
    /// <see cref="MailRequestClaimStore.FinalizeAttachmentSubmissionAsync"/>. If the request
    /// update affects 0 rows, ownership was already lost and neither the evidence row nor
    /// recipients nor attempt history are touched. If the evidence update then affects 0 rows,
    /// the evidence read by the caller was stale; the whole transaction (including the request
    /// update) is rolled back rather than committing a mismatch.
    /// </summary>
    public async Task<bool> FinalizeAsync(
        Guid requestId,
        Guid requestLockToken,
        Guid evidenceClaimToken,
        MailPlainSubmissionEvidenceState expectedEvidenceState,
        DateTimeOffset now,
        MailPlainSubmissionEvidenceState targetEvidenceState,
        string? providerMessageId,
        MailRequestState requestTerminalState,
        MailRecipientDeliveryState? recipientTargetState,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default)
    {
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateRequestSql = """
            UPDATE mail_requests
            SET
                status = @NewStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                delivered_at = @DeliveredAt,
                failed_at = @FailedAt,
                delivery_unknown_at = @DeliveryUnknownAt,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @RequestLockToken;
            """;

        const string updateEvidenceSql = """
            UPDATE mail_plain_submissions
            SET
                evidence_state = @TargetState,
                provider_message_id = @ProviderMessageId,
                resolved_at = @Now,
                updated_at = @Now
            WHERE request_id = @Id
              AND evidence_state = @ExpectedState
              AND claim_token = @EvidenceClaimToken;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            bool requestUpdated;
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateRequestSql;
                update.Parameters.AddWithValue("@NewStatus", (int)requestTerminalState);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue(
                    "@DeliveredAt",
                    requestTerminalState == MailRequestState.Delivered ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "@FailedAt",
                    requestTerminalState == MailRequestState.Failed ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue(
                    "@DeliveryUnknownAt",
                    requestTerminalState == MailRequestState.DeliveryUnknown ? nowStorage : (object)DBNull.Value);
                update.Parameters.AddWithValue("@LastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@RequestLockToken", requestLockToken.ToString("D"));

                requestUpdated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            if (!requestUpdated)
            {
                // Ownership already lost under this exact claim: never touch evidence, recipients,
                // or attempt history for a request this caller no longer owns.
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            bool evidenceUpdated;
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateEvidenceSql;
                update.Parameters.AddWithValue("@TargetState", (int)targetEvidenceState);
                update.Parameters.AddWithValue("@ProviderMessageId", (object?)providerMessageId ?? DBNull.Value);
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue("@Id", requestId.ToString("D"));
                update.Parameters.AddWithValue("@ExpectedState", (int)expectedEvidenceState);
                update.Parameters.AddWithValue("@EvidenceClaimToken", evidenceClaimToken.ToString("D"));
                evidenceUpdated = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
            }

            if (!evidenceUpdated)
            {
                throw new InvalidOperationException(
                    $"Plain submission evidence for request {requestId:D} was not in the expected " +
                    $"'{expectedEvidenceState}' state under the observed claim token during finalize.");
            }

            if (recipientTargetState is { } state)
            {
                await UpdateAllRecipientsAsync(connection, requestId, state, nowStorage, cancellationToken);
            }

            await InsertAttemptAsync(connection, attempt, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            runtimeMetrics?.RecordAttemptCompleted(attempt);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<MailPlainSubmissionRow?> FindWithinConnectionAsync(
        SqliteConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                   provider_message_id, resolved_at
            FROM mail_plain_submissions
            WHERE request_id = @RequestId;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MailPlainSubmissionRow(
            Guid.Parse(reader.GetString(0)),
            (MailPlainSubmissionEvidenceState)reader.GetInt32(1),
            (MailPlainSubmissionEvidenceOrigin)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : SqliteTime.FromStorage(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : SqliteTime.FromStorage(reader.GetString(7)));
    }

    private static async Task<bool> IsFencedProcessingAsync(
        SqliteConnection connection,
        Guid requestId,
        Guid lockToken,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM mail_requests
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at > @Now
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@Now", nowStorage);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <summary>
    /// Tenant-scoped only (ADR 0020 D-06 / Issue #546 maintainer decision): <c>mail_suppressions</c>
    /// has no source_service dimension, so this never widens or narrows that existing contract.
    /// Compares against <c>address_key</c> (trim + lowercase-invariant), the same normalization
    /// <see cref="MailSuppressionRepository"/> and <see cref="MailRequestAcceptStore"/> already
    /// use -- never the legacy <c>mail_requests.recipient_email</c> shadow.
    /// </summary>
    private static async Task<HashSet<string>> FindSuppressedAddressKeysAsync(
        SqliteConnection connection,
        Guid tenantId,
        IReadOnlyList<MailRequestRecipientRow> recipients,
        CancellationToken cancellationToken)
    {
        var distinctKeys = recipients
            .Select(recipient => recipient.AddressKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctKeys.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new string[distinctKeys.Count];
        for (var i = 0; i < distinctKeys.Count; i++)
        {
            var name = $"@Key{i}";
            parameterNames[i] = name;
            command.Parameters.AddWithValue(name, distinctKeys[i]);
        }

        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.CommandText = $"""
            SELECT recipient_email
            FROM mail_suppressions
            WHERE tenant_id = @TenantId
              AND recipient_email IN ({string.Join(", ", parameterNames)});
            """;

        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            suppressed.Add(reader.GetString(0));
        }

        return suppressed;
    }

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        Guid requestId,
        MailPlainSubmissionEvidenceState state,
        string provider,
        Guid lockToken,
        string startedAtStorage,
        string? resolvedAtStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_plain_submissions (
                request_id, evidence_state, evidence_origin, provider, claim_token, started_at,
                provider_message_id, resolved_at, created_at, updated_at)
            VALUES (
                @RequestId, @EvidenceState, @EvidenceOrigin, @Provider, @ClaimToken, @StartedAt,
                NULL, @ResolvedAt, @Now, @Now);
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.Parameters.AddWithValue("@EvidenceState", (int)state);
        command.Parameters.AddWithValue("@EvidenceOrigin", (int)MailPlainSubmissionEvidenceOrigin.Runtime);
        command.Parameters.AddWithValue("@Provider", provider);
        command.Parameters.AddWithValue("@ClaimToken", lockToken.ToString("D"));
        command.Parameters.AddWithValue("@StartedAt", startedAtStorage);
        command.Parameters.AddWithValue("@ResolvedAt", (object?)resolvedAtStorage ?? DBNull.Value);
        command.Parameters.AddWithValue("@Now", startedAtStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAllRecipientsAsync(
        SqliteConnection connection,
        Guid requestId,
        MailRecipientDeliveryState state,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_request_recipients
            SET delivery_state = @State, updated_at = @Now
            WHERE request_id = @RequestId;
            """;
        command.Parameters.AddWithValue("@State", (int)state);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplySuppressionRecipientStatesAsync(
        SqliteConnection connection,
        Guid requestId,
        IReadOnlySet<string> suppressedKeys,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var parameterNames = new string[suppressedKeys.Count];
        var i = 0;
        foreach (var key in suppressedKeys)
        {
            var name = $"@Key{i}";
            parameterNames[i] = name;
            command.Parameters.AddWithValue(name, key);
            i++;
        }

        command.Parameters.AddWithValue("@SuppressedState", (int)MailRecipientDeliveryState.Suppressed);
        command.Parameters.AddWithValue("@NotSentState", (int)MailRecipientDeliveryState.NotSent);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
        command.CommandText = $"""
            UPDATE mail_request_recipients
            SET
                delivery_state = CASE
                    WHEN address_key IN ({string.Join(", ", parameterNames)}) THEN @SuppressedState
                    ELSE @NotSentState
                END,
                updated_at = @Now
            WHERE request_id = @RequestId;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TryUpdateRequestTerminalAsync(
        SqliteConnection connection,
        Guid requestId,
        Guid lockToken,
        MailRequestState terminalState,
        string nowStorage,
        string lastErrorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_requests
            SET
                status = @NewStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                failed_at = @Now,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken;
            """;
        command.Parameters.AddWithValue("@NewStatus", (int)terminalState);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@LastErrorMessage", lastErrorMessage);
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static Task InsertAttemptAsync(
        SqliteConnection connection,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken)
    {
        const string insertAttemptSql = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message, retryable,
                lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @AttemptStatus,
                @ProviderMessageId, @ErrorCode, @ErrorMessage, @Retryable,
                @LockToken, @StartedAt, @CompletedAt);
            """;
        return MailRequestClaimStore.InsertMailAttemptAsync(connection, insertAttemptSql, attempt, cancellationToken);
    }
}
