using Amane.Mailer.Bounce;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Correlation lookup and atomic bounce persist (bounce_events + optional suppress + inbox finalize).
/// </summary>
public sealed class BounceIngestionStore(SqliteConnectionFactory connections)
{
    public async Task<BounceCorrelationMatch?> FindByProviderMessageIdAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerMessageId);

        // Exact TEXT match — no casing/format normalization (ADR 0020 D-03 / #299 F-1).
        const string sql = """
            SELECT mr.tenant_id, mr.source_service, mr.mail_request_id, mr.recipient_email
            FROM mail_attempts ma
            INNER JOIN mail_requests mr ON mr.id = ma.request_id
            WHERE ma.provider_message_id = @ProviderMessageId
            ORDER BY ma.id DESC
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@ProviderMessageId", providerMessageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BounceCorrelationMatch(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3));
    }

    public async Task<bool> PersistCorrelatedAsync(
        ProviderEventInboxRow claimed,
        BounceCorrelationMatch match,
        string? rawStatusMessage,
        bool suppress,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.ProviderMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimed.DeliveryStatus);

        var bounceEventId = Guid.CreateVersion7(now);
        var sanitizedStatusMessage = rawStatusMessage is null
            ? null
            : ProviderErrorSanitizer.Sanitize(rawStatusMessage);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var bounce = connection.CreateCommand())
            {
                bounce.CommandText = """
                    INSERT INTO bounce_events (
                        id, tenant_id, source_service, mail_request_id,
                        provider, provider_event_id, provider_message_id,
                        delivery_status, status_message, occurred_at, created_at)
                    VALUES (
                        @Id, @TenantId, @SourceService, @MailRequestId,
                        @Provider, @ProviderEventId, @ProviderMessageId,
                        @DeliveryStatus, @StatusMessage, @OccurredAt, @CreatedAt)
                    ON CONFLICT (provider, provider_event_id) DO NOTHING;
                    """;
                bounce.Parameters.AddWithValue("@Id", bounceEventId.ToString("D"));
                bounce.Parameters.AddWithValue("@TenantId", match.TenantId.ToString("D"));
                bounce.Parameters.AddWithValue("@SourceService", match.SourceService);
                bounce.Parameters.AddWithValue("@MailRequestId", match.MailRequestId.ToString("D"));
                bounce.Parameters.AddWithValue("@Provider", claimed.Provider);
                bounce.Parameters.AddWithValue("@ProviderEventId", claimed.EventId);
                bounce.Parameters.AddWithValue("@ProviderMessageId", claimed.ProviderMessageId);
                bounce.Parameters.AddWithValue("@DeliveryStatus", claimed.DeliveryStatus);
                bounce.Parameters.AddWithValue("@StatusMessage", (object?)sanitizedStatusMessage ?? DBNull.Value);
                bounce.Parameters.AddWithValue("@OccurredAt", SqliteTime.ToStorageUtc(now));
                bounce.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
                await bounce.ExecuteNonQueryAsync(cancellationToken);
            }

            if (suppress)
            {
                var normalizedRecipient = RecipientEmailNormalizer.Normalize(match.RecipientEmail);
                await using var suppression = connection.CreateCommand();
                suppression.CommandText = """
                    INSERT INTO mail_suppressions (
                        id, tenant_id, recipient_email, reason, source_bounce_event_id, created_at)
                    VALUES (
                        @Id, @TenantId, @RecipientEmail, @Reason, @SourceBounceEventId, @CreatedAt)
                    ON CONFLICT (tenant_id, recipient_email) DO NOTHING;
                    """;
                suppression.Parameters.AddWithValue("@Id", Guid.CreateVersion7(now).ToString("D"));
                suppression.Parameters.AddWithValue("@TenantId", match.TenantId.ToString("D"));
                suppression.Parameters.AddWithValue("@RecipientEmail", normalizedRecipient);
                suppression.Parameters.AddWithValue("@Reason", MailSuppressionReasons.HardBounce);
                suppression.Parameters.AddWithValue("@SourceBounceEventId", bounceEventId.ToString("D"));
                suppression.Parameters.AddWithValue("@CreatedAt", SqliteTime.ToStorageUtc(now));
                await suppression.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var finalize = connection.CreateCommand())
            {
                finalize.CommandText = """
                    UPDATE provider_event_inbox
                    SET
                        status = @ProcessedStatus,
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
                finalize.Parameters.AddWithValue("@ProcessedStatus", (int)ProviderEventInboxState.Processed);
                finalize.Parameters.AddWithValue("@Disposition", ProviderEventInboxDisposition.Processed);
                finalize.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
                finalize.Parameters.AddWithValue("@Id", claimed.Id.ToString("D"));
                finalize.Parameters.AddWithValue("@ProcessingStatus", (int)ProviderEventInboxState.Processing);
                finalize.Parameters.AddWithValue("@LockToken", claimed.LockToken.ToString("D"));

                var affected = await finalize.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}

public sealed record BounceCorrelationMatch(
    Guid TenantId,
    string SourceService,
    Guid MailRequestId,
    string RecipientEmail);
