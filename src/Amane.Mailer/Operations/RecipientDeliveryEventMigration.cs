using Amane.Mailer.Bounce;
using Amane.Mailer.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

internal static class RecipientDeliveryEventMigration
{
    public const string MigrationVersion = "017_recipient_delivery_events.sql";

    public static readonly SqlMigrationRunner.MigrationTransactionStep Step = new(
        ValidatePreconditionBeforeScriptAsync,
        ApplyDataMigrationAfterScriptAsync);

    private sealed record LegacyEvent(
        string Id,
        string TenantId,
        string SourceService,
        string MailRequestId,
        string Provider,
        string ProviderEventId,
        string ProviderMessageId,
        string ProviderStatus,
        string? StatusMessage,
        string OccurredAt,
        string CreatedAt);

    private sealed record RecipientIdentity(
        string RequestId,
        int Role,
        int Ordinal);

    private static async Task ValidatePreconditionBeforeScriptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await RequireZeroAsync(
            connection,
            "SELECT COUNT(*) FROM mail_requests WHERE status = 1;",
            "Migration 017 requires zero Processing mail requests.",
            cancellationToken);
        await RequireZeroAsync(
            connection,
            "SELECT COUNT(*) FROM provider_event_inbox WHERE status = 1;",
            "Migration 017 requires zero Processing provider inbox rows.",
            cancellationToken);

        var requiredObjects = await ReadCountAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN (
                'bounce_events',
                'mail_requests',
                'mail_request_recipients',
                'mail_plain_submissions',
                'mail_attachment_submissions',
                'provider_event_inbox');
            """,
            cancellationToken);
        if (requiredObjects != 6)
        {
            throw new InvalidOperationException("Migration 017 requires the complete migration 016 schema.");
        }

        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(mail_request_recipients);";
        var requiredColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "request_id",
            "recipient_role",
            "ordinal",
            "address",
            "address_key",
            "delivery_state",
        };
        await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            requiredColumns.Remove(reader.GetString(1));
        }

        if (requiredColumns.Count != 0)
        {
            throw new InvalidOperationException("Migration 017 found an incomplete canonical recipient schema.");
        }
    }

    private static async Task ApplyDataMigrationAfterScriptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var events = await LoadLegacyEventsAsync(connection, cancellationToken);
        foreach (var legacyEvent in events)
        {
            var recipient = await ResolveLegacyRecipientAsync(connection, legacyEvent, cancellationToken);
            var appliedState = ClassifyAppliedState(legacyEvent.ProviderStatus);
            await InsertEventAsync(connection, legacyEvent, recipient, appliedState, cancellationToken);
        }

        var newCount = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM recipient_delivery_events;",
            cancellationToken);
        if (newCount != events.Count)
        {
            throw new InvalidOperationException("Migration 017 event row count assertion failed.");
        }

        await ApplyLegacyNegativeWinnersAsync(connection, cancellationToken);
        await ClearLegacyLosingAppliedStatesAsync(connection, cancellationToken);
        await ReconcileLegacyHistoryOnlyStatesAsync(connection, cancellationToken);

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE bounce_events;";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var remainingLegacyTable = await ReadCountAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'bounce_events';",
            cancellationToken);
        if (remainingLegacyTable != 0)
        {
            throw new InvalidOperationException("Migration 017 could not remove the superseded event table.");
        }
    }

    private static async Task<IReadOnlyList<LegacyEvent>> LoadLegacyEventsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, source_service, mail_request_id,
                   provider, provider_event_id, provider_message_id,
                   delivery_status, status_message, occurred_at, created_at
            FROM bounce_events
            ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return rows;
    }

    private static async Task<RecipientIdentity> ResolveLegacyRecipientAsync(
        SqliteConnection connection,
        LegacyEvent legacyEvent,
        CancellationToken cancellationToken)
    {
        var requestIds = new List<string>(2);
        await using (var request = connection.CreateCommand())
        {
            request.CommandText = """
                SELECT id
                FROM mail_requests
                WHERE tenant_id = @TenantId
                  AND source_service = @SourceService
                  AND mail_request_id = @MailRequestId;
                """;
            request.Parameters.AddWithValue("@TenantId", legacyEvent.TenantId);
            request.Parameters.AddWithValue("@SourceService", legacyEvent.SourceService);
            request.Parameters.AddWithValue("@MailRequestId", legacyEvent.MailRequestId);
            await using var reader = await request.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                requestIds.Add(reader.GetString(0));
            }
        }

        if (requestIds.Count != 1)
        {
            throw new InvalidOperationException(
                "Migration 017 could not prove exactly one request for a legacy event.");
        }

        var recipients = new List<RecipientIdentity>(2);
        await using (var recipient = connection.CreateCommand())
        {
            recipient.CommandText = """
                SELECT request_id, recipient_role, ordinal
                FROM mail_request_recipients
                WHERE request_id = @RequestId;
                """;
            recipient.Parameters.AddWithValue("@RequestId", requestIds[0]);
            await using var reader = await recipient.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recipients.Add(new RecipientIdentity(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        if (recipients.Count != 1 || recipients[0].Role != 0 || recipients[0].Ordinal != 0)
        {
            throw new InvalidOperationException(
                "Migration 017 could not prove one legacy To ordinal 0 recipient.");
        }

        return recipients[0];
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        LegacyEvent legacyEvent,
        RecipientIdentity recipient,
        MailRecipientDeliveryState? appliedState,
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
                @AppliedDeliveryState, @StatusMessage, @OccurredAt, @CreatedAt);
            """;
        command.Parameters.AddWithValue("@Id", legacyEvent.Id);
        command.Parameters.AddWithValue("@TenantId", legacyEvent.TenantId);
        command.Parameters.AddWithValue("@SourceService", legacyEvent.SourceService);
        command.Parameters.AddWithValue("@MailRequestId", legacyEvent.MailRequestId);
        command.Parameters.AddWithValue("@RecipientRole", recipient.Role);
        command.Parameters.AddWithValue("@RecipientOrdinal", recipient.Ordinal);
        command.Parameters.AddWithValue("@Provider", legacyEvent.Provider);
        command.Parameters.AddWithValue("@ProviderEventId", legacyEvent.ProviderEventId);
        command.Parameters.AddWithValue("@ProviderMessageId", legacyEvent.ProviderMessageId);
        command.Parameters.AddWithValue("@ProviderStatus", legacyEvent.ProviderStatus);
        command.Parameters.AddWithValue(
            "@AppliedDeliveryState",
            appliedState is null ? DBNull.Value : (int)appliedState.Value);
        command.Parameters.AddWithValue("@StatusMessage", (object?)legacyEvent.StatusMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@OccurredAt", legacyEvent.OccurredAt);
        command.Parameters.AddWithValue("@CreatedAt", legacyEvent.CreatedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Migration 017 could not backfill a legacy event.");
        }
    }

    private static async Task ApplyLegacyNegativeWinnersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT
                    mr.id AS request_id,
                    e.recipient_role,
                    e.recipient_ordinal,
                    e.applied_delivery_state,
                    e.occurred_at,
                    e.provider,
                    e.provider_event_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY mr.id, e.recipient_role, e.recipient_ordinal
                        ORDER BY e.occurred_at DESC, e.provider_event_id DESC
                    ) AS ordering_rank
                FROM recipient_delivery_events e
                JOIN mail_requests mr
                  ON mr.tenant_id = e.tenant_id
                 AND mr.source_service = e.source_service
                 AND mr.mail_request_id = e.mail_request_id
                WHERE e.applied_delivery_state IN (3, 4)
            )
            UPDATE mail_request_recipients
            SET delivery_state = (
                    SELECT applied_delivery_state FROM ranked
                    WHERE ranked.request_id = mail_request_recipients.request_id
                      AND ranked.recipient_role = mail_request_recipients.recipient_role
                      AND ranked.recipient_ordinal = mail_request_recipients.ordinal
                      AND ranked.ordering_rank = 1),
                last_feedback_occurred_at = (
                    SELECT occurred_at FROM ranked
                    WHERE ranked.request_id = mail_request_recipients.request_id
                      AND ranked.recipient_role = mail_request_recipients.recipient_role
                      AND ranked.recipient_ordinal = mail_request_recipients.ordinal
                      AND ranked.ordering_rank = 1),
                last_feedback_provider = (
                    SELECT provider FROM ranked
                    WHERE ranked.request_id = mail_request_recipients.request_id
                      AND ranked.recipient_role = mail_request_recipients.recipient_role
                      AND ranked.recipient_ordinal = mail_request_recipients.ordinal
                      AND ranked.ordering_rank = 1),
                last_feedback_event_id = (
                    SELECT provider_event_id FROM ranked
                    WHERE ranked.request_id = mail_request_recipients.request_id
                      AND ranked.recipient_role = mail_request_recipients.recipient_role
                      AND ranked.recipient_ordinal = mail_request_recipients.ordinal
                      AND ranked.ordering_rank = 1)
            WHERE EXISTS (
                SELECT 1 FROM ranked
                WHERE ranked.request_id = mail_request_recipients.request_id
                  AND ranked.recipient_role = mail_request_recipients.recipient_role
                  AND ranked.recipient_ordinal = mail_request_recipients.ordinal
                  AND ranked.ordering_rank = 1);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearLegacyLosingAppliedStatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recipient_delivery_events AS e
            SET applied_delivery_state = NULL
            WHERE e.applied_delivery_state IN (3, 4)
              AND EXISTS (
                  SELECT 1
                  FROM mail_requests mr
                  JOIN mail_request_recipients rr ON rr.request_id = mr.id
                  WHERE mr.tenant_id = e.tenant_id
                    AND mr.source_service = e.source_service
                    AND mr.mail_request_id = e.mail_request_id
                    AND rr.recipient_role = e.recipient_role
                    AND rr.ordinal = e.recipient_ordinal
                    AND rr.last_feedback_event_id <> e.provider_event_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReconcileLegacyHistoryOnlyStatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Migration 016 could only see that some legacy bounce_events row existed. Restore
        // Accepted recipients to Pending when every migrated status is history-only.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE mail_request_recipients AS rr
            SET delivery_state = @PendingState
            WHERE rr.delivery_state = @BouncedState
              AND EXISTS (
                  SELECT 1
                  FROM mail_requests mr
                  JOIN recipient_delivery_events e
                    ON e.tenant_id = mr.tenant_id
                   AND e.source_service = mr.source_service
                   AND e.mail_request_id = mr.mail_request_id
                  WHERE mr.id = rr.request_id
                    AND e.recipient_role = rr.recipient_role
                    AND e.recipient_ordinal = rr.ordinal)
              AND NOT EXISTS (
                  SELECT 1
                  FROM mail_requests mr
                  JOIN recipient_delivery_events e
                    ON e.tenant_id = mr.tenant_id
                   AND e.source_service = mr.source_service
                   AND e.mail_request_id = mr.mail_request_id
                  WHERE mr.id = rr.request_id
                    AND e.recipient_role = rr.recipient_role
                    AND e.recipient_ordinal = rr.ordinal
                    AND e.provider_status IN ('Bounced', 'Suppressed'))
              AND (
                  EXISTS (
                      SELECT 1 FROM mail_plain_submissions ps
                      WHERE ps.request_id = rr.request_id AND ps.evidence_state = 2)
                  OR EXISTS (
                      SELECT 1 FROM mail_attachment_submissions ats
                      WHERE ats.request_id = rr.request_id AND ats.submission_state = 1));
            """;
        command.Parameters.AddWithValue("@PendingState", (int)MailRecipientDeliveryState.Pending);
        command.Parameters.AddWithValue("@BouncedState", (int)MailRecipientDeliveryState.Bounced);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MailRecipientDeliveryState? ClassifyAppliedState(string providerStatus)
    {
        if (BounceClassifier.IsHardBounce(providerStatus))
        {
            return MailRecipientDeliveryState.Bounced;
        }

        if (BounceClassifier.IsSuppressed(providerStatus))
        {
            return MailRecipientDeliveryState.Suppressed;
        }

        return null;
    }

    private static async Task RequireZeroAsync(
        SqliteConnection connection,
        string sql,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (await ReadCountAsync(connection, sql, cancellationToken) != 0)
        {
            throw new InvalidOperationException(errorMessage);
        }
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
}
