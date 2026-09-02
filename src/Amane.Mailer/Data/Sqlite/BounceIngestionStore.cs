using Amane.Mailer.Bounce;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Atomically correlates provider feedback through request-level submission evidence and the
/// canonical recipient table, then persists history/current state/suppression/inbox finalize.
/// </summary>
public sealed class BounceIngestionStore(SqliteConnectionFactory connections)
{
    public async Task<RecipientFeedbackProcessResult> ProcessClaimedAsync(
        ProviderEventInboxRow claimed,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.ProviderMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.DeliveryStatus);

        var nowStorage = SqliteTime.ToStorageUtc(now);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            if (!await IsClaimCurrentAsync(connection, claimed, nowStorage, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return RecipientFeedbackProcessResult.FenceFailed;
            }

            var requestCandidates = await FindRequestCandidatesAsync(connection, claimed, cancellationToken);
            if (requestCandidates.Count == 0)
            {
                await FinalizeAsync(
                    connection,
                    claimed,
                    nowStorage,
                    ProviderEventInboxDisposition.Discarded,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return RecipientFeedbackProcessResult.Unmatched;
            }

            if (requestCandidates.Count > 1)
            {
                throw new InvalidOperationException(
                    "Recipient feedback matched more than one request-level submission evidence row.");
            }

            RecipientFeedbackRecipient? recipient;
            try
            {
                recipient = await FindRecipientAsync(
                    connection,
                    requestCandidates[0].RequestId,
                    claimed.RecipientEmail,
                    cancellationToken);
            }
            catch (AmbiguousRecipientCorrelationException)
            {
                throw new InvalidOperationException(
                    "Recipient feedback matched more than one canonical recipient row.");
            }

            if (recipient is null)
            {
                await FinalizeAsync(
                    connection,
                    claimed,
                    nowStorage,
                    ProviderEventInboxDisposition.Discarded,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return RecipientFeedbackProcessResult.RecipientMismatch;
            }

            var occurredAt = claimed.OccurredAt ?? now;
            var safeStatusMessage = string.IsNullOrWhiteSpace(claimed.StatusMessage)
                ? null
                : ProviderErrorSanitizer.Sanitize(claimed.StatusMessage);
            var targetState = BounceClassifier.AppliedRecipientState(claimed.DeliveryStatus);
            var applies = targetState is not null
                && ShouldApplyFeedback(recipient, targetState.Value, occurredAt, claimed.EventId);
            var eventRowId = Guid.CreateVersion7(now);

            var inserted = await InsertEventAsync(
                connection,
                eventRowId,
                requestCandidates[0],
                recipient,
                claimed,
                applies ? targetState : null,
                safeStatusMessage,
                occurredAt,
                now,
                cancellationToken);

            if (!inserted)
            {
                await FinalizeAsync(
                    connection,
                    claimed,
                    nowStorage,
                    ProviderEventInboxDisposition.Processed,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return RecipientFeedbackProcessResult.Duplicate;
            }

            if (applies)
            {
                await UpdateRecipientAsync(
                    connection,
                    requestCandidates[0].RequestId,
                    recipient,
                    targetState!.Value,
                    claimed,
                    safeStatusMessage,
                    occurredAt,
                    nowStorage,
                    cancellationToken);
            }

            if (BounceClassifier.ShouldSuppress(claimed.DeliveryStatus))
            {
                await InsertSuppressionAsync(
                    connection,
                    eventRowId,
                    requestCandidates[0].TenantId,
                    recipient.AddressKey,
                    nowStorage,
                    cancellationToken);
            }

            await FinalizeAsync(
                connection,
                claimed,
                nowStorage,
                ProviderEventInboxDisposition.Processed,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return RecipientFeedbackProcessResult.Processed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal static bool ShouldApplyFeedback(
        RecipientFeedbackRecipient recipient,
        MailRecipientDeliveryState incomingState,
        DateTimeOffset incomingOccurredAt,
        string incomingEventId)
    {
        if (incomingState == MailRecipientDeliveryState.Delivered)
        {
            return recipient.DeliveryState is
                MailRecipientDeliveryState.NotSent or
                MailRecipientDeliveryState.Pending or
                MailRecipientDeliveryState.Failed or
                MailRecipientDeliveryState.Unknown;
        }

        if (incomingState is not (MailRecipientDeliveryState.Bounced or MailRecipientDeliveryState.Suppressed))
        {
            return false;
        }

        if (recipient.DeliveryState is not (MailRecipientDeliveryState.Bounced or MailRecipientDeliveryState.Suppressed)
            || recipient.LastFeedbackOccurredAt is null
            || recipient.LastFeedbackEventId is null)
        {
            return true;
        }

        var timeComparison = incomingOccurredAt.CompareTo(recipient.LastFeedbackOccurredAt.Value);
        return timeComparison > 0
            || (timeComparison == 0
                && string.CompareOrdinal(incomingEventId, recipient.LastFeedbackEventId) > 0);
    }

    private static async Task<bool> IsClaimCurrentAsync(
        SqliteConnection connection,
        ProviderEventInboxRow claimed,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM provider_event_inbox
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;
        command.Parameters.AddWithValue("@Id", claimed.Id.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
        command.Parameters.AddWithValue("@LockToken", claimed.LockToken.ToString("D"));
        command.Parameters.AddWithValue("@Now", nowStorage);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<IReadOnlyList<RecipientFeedbackRequest>> FindRequestCandidatesAsync(
        SqliteConnection connection,
        ProviderEventInboxRow claimed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence.request_id, mr.tenant_id, mr.source_service, mr.mail_request_id
            FROM (
                SELECT request_id
                FROM mail_plain_submissions
                WHERE provider = @Provider
                  AND provider_message_id = @ProviderMessageId
                UNION ALL
                SELECT request_id
                FROM mail_attachment_submissions
                WHERE provider = @Provider
                  AND provider_message_id = @ProviderMessageId
            ) evidence
            JOIN mail_requests mr ON mr.id = evidence.request_id;
            """;
        command.Parameters.AddWithValue("@Provider", claimed.Provider);
        command.Parameters.AddWithValue("@ProviderMessageId", claimed.ProviderMessageId!);

        var rows = new List<RecipientFeedbackRequest>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecipientFeedbackRequest(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3))));
        }

        return rows;
    }

    private static async Task<RecipientFeedbackRecipient?> FindRecipientAsync(
        SqliteConnection connection,
        string requestId,
        string? eventRecipient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventRecipient))
        {
            return null;
        }

        var addressKey = RecipientEmailNormalizer.Normalize(eventRecipient);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_role, ordinal, address_key, delivery_state,
                   last_feedback_occurred_at, last_feedback_provider, last_feedback_event_id
            FROM mail_request_recipients
            WHERE request_id = @RequestId
              AND address_key = @AddressKey;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId);
        command.Parameters.AddWithValue("@AddressKey", addressKey);

        var rows = new List<RecipientFeedbackRecipient>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecipientFeedbackRecipient(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                (MailRecipientDeliveryState)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new AmbiguousRecipientCorrelationException(),
        };
    }

    private static async Task<bool> InsertEventAsync(
        SqliteConnection connection,
        Guid eventRowId,
        RecipientFeedbackRequest request,
        RecipientFeedbackRecipient recipient,
        ProviderEventInboxRow claimed,
        MailRecipientDeliveryState? appliedState,
        string? safeStatusMessage,
        DateTimeOffset occurredAt,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recipient_delivery_events (
                id, tenant_id, source_service, mail_request_id,
                recipient_role, recipient_ordinal,
                provider, provider_event_id, provider_message_id, provider_status,
                applied_delivery_state, status_message, occurred_at, created_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId,
                @RecipientRole, @RecipientOrdinal,
                @Provider, @ProviderEventId, @ProviderMessageId, @ProviderStatus,
                @AppliedDeliveryState, @StatusMessage, @OccurredAt, @CreatedAt)
            ON CONFLICT (provider, provider_event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@Id", eventRowId.ToString("D"));
        command.Parameters.AddWithValue("@TenantId", request.TenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", request.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", request.MailRequestId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientRole", recipient.Role);
        command.Parameters.AddWithValue("@RecipientOrdinal", recipient.Ordinal);
        command.Parameters.AddWithValue("@Provider", claimed.Provider);
        command.Parameters.AddWithValue("@ProviderEventId", claimed.EventId);
        command.Parameters.AddWithValue("@ProviderMessageId", claimed.ProviderMessageId!);
        command.Parameters.AddWithValue("@ProviderStatus", claimed.DeliveryStatus!);
        command.Parameters.AddWithValue(
            "@AppliedDeliveryState",
            appliedState is null ? DBNull.Value : (int)appliedState.Value);
        command.Parameters.AddWithValue("@StatusMessage", (object?)safeStatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(occurredAt));
        command.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(createdAt));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task UpdateRecipientAsync(
        SqliteConnection connection,
        string requestId,
        RecipientFeedbackRecipient recipient,
        MailRecipientDeliveryState state,
        ProviderEventInboxRow claimed,
        string? safeStatusMessage,
        DateTimeOffset occurredAt,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_request_recipients
            SET delivery_state = @DeliveryState,
                provider_message_id = @ProviderMessageId,
                provider_status_detail = @ProviderStatusDetail,
                last_feedback_occurred_at = @OccurredAt,
                last_feedback_provider = @Provider,
                last_feedback_event_id = @ProviderEventId,
                updated_at = @UpdatedAt
            WHERE request_id = @RequestId
              AND recipient_role = @RecipientRole
              AND ordinal = @RecipientOrdinal;
            """;
        command.Parameters.AddWithValue("@DeliveryState", (int)state);
        command.Parameters.AddWithValue("@ProviderMessageId", claimed.ProviderMessageId!);
        command.Parameters.AddWithValue("@ProviderStatusDetail", (object?)safeStatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(occurredAt));
        command.Parameters.AddWithValue("@Provider", claimed.Provider);
        command.Parameters.AddWithValue("@ProviderEventId", claimed.EventId);
        command.Parameters.AddWithValue("@UpdatedAt", nowStorage);
        command.Parameters.AddWithValue("@RequestId", requestId);
        command.Parameters.AddWithValue("@RecipientRole", recipient.Role);
        command.Parameters.AddWithValue("@RecipientOrdinal", recipient.Ordinal);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Recipient feedback current-state update affected an unexpected row count.");
        }
    }

    private static async Task InsertSuppressionAsync(
        SqliteConnection connection,
        Guid eventRowId,
        Guid tenantId,
        string canonicalAddressKey,
        string nowStorage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_suppressions (
                id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
            VALUES (
                @Id, @TenantId, @RecipientEmail, @Reason, @SourceEventId, @CreatedAt)
            ON CONFLICT (tenant_id, recipient_email) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@Id", Guid.CreateVersion7().ToString("D"));
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@RecipientEmail", canonicalAddressKey);
        command.Parameters.AddWithValue("@Reason", MailSuppressionReasons.HardBounce);
        command.Parameters.AddWithValue("@SourceEventId", eventRowId.ToString("D"));
        command.Parameters.AddWithValue("@CreatedAt", nowStorage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizeAsync(
        SqliteConnection connection,
        ProviderEventInboxRow claimed,
        string nowStorage,
        string disposition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE provider_event_inbox
            SET status = @ProcessedStatus,
                disposition = @Disposition,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                last_error_code = NULL,
                updated_at = @Now,
                completed_at = @Now
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;
        command.Parameters.AddWithValue("@ProcessedStatus", (int)ProviderEventInboxState.Processed);
        command.Parameters.AddWithValue("@Disposition", disposition);
        command.Parameters.AddWithValue("@Now", nowStorage);
        command.Parameters.AddWithValue("@Id", claimed.Id.ToString("D"));
        command.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
        command.Parameters.AddWithValue("@LockToken", claimed.LockToken.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Recipient feedback inbox finalize lost its lease fence.");
        }
    }

    private sealed class AmbiguousRecipientCorrelationException : Exception
    {
    }
}

public enum RecipientFeedbackProcessResult
{
    Processed,
    Duplicate,
    Unmatched,
    RecipientMismatch,
    FenceFailed,
}

internal sealed record RecipientFeedbackRequest(
    string RequestId,
    Guid TenantId,
    string SourceService,
    Guid MailRequestId);

internal sealed record RecipientFeedbackRecipient(
    int Role,
    int Ordinal,
    string AddressKey,
    MailRecipientDeliveryState DeliveryState,
    DateTimeOffset? LastFeedbackOccurredAt,
    string? LastFeedbackProvider,
    string? LastFeedbackEventId);
