using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Canonical recipient reads for provider dispatch (ADR 0023 D-03/D-10). Writes happen inline in
/// <see cref="MailRequestAcceptStore.InsertAcceptedAsync"/> at accept time; for the plain request
/// provider disposition lifecycle in <see cref="MailPlainSubmissionStore"/>; and for the
/// attachment request suppression precheck in <see cref="TryApplySuppressionPrecheckAsync"/>
/// below (Issue #546 review finding F2).
/// </summary>
public sealed class MailRequestRecipientStore(
    SqliteConnectionFactory connections,
    TimeProvider timeProvider,
    MailerRuntimeMetrics? runtimeMetrics = null)
{
    private const string RecipientSuppressedMessage = "Recipient is on the suppression list.";
    private const string SuppressionCheckProvider = "none";

    /// <summary>
    /// Attachment request suppression precheck (ADR 0023 D-05, Issue #546 review finding F2):
    /// canonical recipient mapping and suppression checking applies to Worker/provider dispatch
    /// generally, not only to plain requests. This intentionally does <b>not</b> touch
    /// <c>mail_attachment_submissions</c> -- the attachment submission evidence state machine
    /// (Started/Succeeded/DefinitiveFailed/Unknown, ADR 0022 D-08) is out of Issue #546's scope
    /// and unchanged. A suppression hit here is pre-Started (matching the existing NoEvidence/
    /// rowless semantics for attachment requests that never reach the provider), so it converges
    /// the same way <see cref="MailPlainSubmissionStore.TryPrepareProviderInvocationAsync"/>'s
    /// suppression branch does (canonical recipients checked all-or-nothing, atomic Failed
    /// convergence with per-recipient Suppressed/NotSent states and a RECIPIENT_SUPPRESSED
    /// attempt) but without any evidence-table write.
    /// </summary>
    public async Task<AttachmentSuppressionPrecheckResult> TryApplySuppressionPrecheckAsync(
        Guid requestId,
        Guid tenantId,
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

            if (!await RecipientSuppressionSupport.IsFencedProcessingAsync(connection, requestId, lockToken, nowStorage, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new AttachmentSuppressionPrecheckResult(AttachmentSuppressionPrecheckOutcome.FenceFailed);
            }

            var recipients = await ListWithinConnectionAsync(connection, requestId, cancellationToken);
            if (recipients.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Mail request {requestId:D} has no canonical recipient rows; refusing to " +
                    "invoke the provider.");
            }

            var suppressedKeys = await RecipientSuppressionSupport.FindSuppressedAddressKeysAsync(
                connection, tenantId, recipients, cancellationToken);

            if (suppressedKeys.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new AttachmentSuppressionPrecheckResult(
                    AttachmentSuppressionPrecheckOutcome.NotSuppressed,
                    Recipients: recipients);
            }

            await RecipientSuppressionSupport.ApplySuppressionRecipientStatesAsync(
                connection, requestId, suppressedKeys, recipients.Count, nowStorage, cancellationToken);

            var attempt = new MailAttemptInsert
            {
                RequestId = requestId,
                AttemptNumber = attemptNumber,
                Provider = SuppressionCheckProvider,
                Status = MailRequestState.Failed,
                ErrorCode = MailDeliveryErrorCodes.RecipientSuppressed,
                ErrorMessage = RecipientSuppressedMessage,
                Retryable = false,
                LockToken = lockToken,
                StartedAt = nowUtc,
                CompletedAt = nowUtc,
            };
            await RecipientSuppressionSupport.InsertAttemptAsync(connection, attempt, cancellationToken);

            if (!await RecipientSuppressionSupport.TryUpdateRequestTerminalAsync(
                    connection, requestId, lockToken, MailRequestState.Failed, nowStorage, RecipientSuppressedMessage, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Mail request {requestId:D} could not be moved to Failed for the " +
                    "attachment suppression precheck under the fenced claim token.");
            }

            await transaction.CommitAsync(cancellationToken);
            runtimeMetrics?.RecordAttemptCompleted(attempt);
            runtimeMetrics?.RecordSuppressedSend();
            return new AttachmentSuppressionPrecheckResult(
                AttachmentSuppressionPrecheckOutcome.Suppressed,
                Recipients: recipients);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Ordered role ASC (To=0, Cc=1, Bcc=2), then ordinal ASC within role -- i.e. already in the
    /// provider global order To -&gt; Cc -&gt; Bcc with role-internal submission order preserved
    /// (ADR 0023 D-01).
    /// </summary>
    public async Task<IReadOnlyList<MailRequestRecipientRow>> ListByRequestIdAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await ListWithinConnectionAsync(connection, requestId, cancellationToken);
    }

    internal static async Task<IReadOnlyList<MailRequestRecipientRow>> ListWithinConnectionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT recipient_role, ordinal, address, address_key, display_name, delivery_state
            FROM mail_request_recipients
            WHERE request_id = @RequestId
            ORDER BY recipient_role ASC, ordinal ASC;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var rows = new List<MailRequestRecipientRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MailRequestRecipientRow(
                (MailRecipientRole)reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                (MailRecipientDeliveryState)reader.GetInt32(5)));
        }

        return rows;
    }
}

/// <summary>
/// Outcome of <see cref="MailRequestRecipientStore.TryApplySuppressionPrecheckAsync"/>
/// (Issue #546 review finding F2).
/// </summary>
public enum AttachmentSuppressionPrecheckOutcome
{
    /// <summary>No canonical recipient is suppressed; the caller may proceed to invoke the provider.</summary>
    NotSuppressed,

    /// <summary>
    /// One or more canonical recipients were suppressed. Provider invocation was skipped
    /// entirely and the request already converged terminally to <c>Failed</c> in the same
    /// transaction. The caller must not call the provider and has nothing further to finalize.
    /// </summary>
    Suppressed,

    /// <summary>
    /// The caller's claim no longer fences the request (lease expired or lock token superseded).
    /// No writes were made; the caller must not call the provider.
    /// </summary>
    FenceFailed,
}

public sealed record AttachmentSuppressionPrecheckResult(
    AttachmentSuppressionPrecheckOutcome Outcome,
    IReadOnlyList<MailRequestRecipientRow>? Recipients = null);
