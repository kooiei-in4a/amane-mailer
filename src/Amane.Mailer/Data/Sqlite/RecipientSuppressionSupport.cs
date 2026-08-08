using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Shared suppression-check, fencing, and terminal-Failed-convergence primitives used by both
/// the plain request evidence lifecycle (<see cref="MailPlainSubmissionStore"/>) and the
/// attachment request suppression precheck
/// (<see cref="MailRequestRecipientStore.TryApplySuppressionPrecheckAsync"/>) -- Issue #546
/// review finding F2: canonical recipient suppression checking applies to Worker/provider
/// dispatch generally, not only to plain requests, even though the attachment request submission
/// evidence state machine itself (<c>mail_attachment_submissions</c>) is unchanged.
/// </summary>
internal static class RecipientSuppressionSupport
{
    public static async Task<bool> IsFencedProcessingAsync(
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
    public static async Task<HashSet<string>> FindSuppressedAddressKeysAsync(
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

    /// <summary>Issue #546 review finding F5: fail-closed on a partial bulk update.</summary>
    public static async Task ApplySuppressionRecipientStatesAsync(
        SqliteConnection connection,
        Guid requestId,
        IReadOnlySet<string> suppressedKeys,
        int expectedRecipientCount,
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
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != expectedRecipientCount)
        {
            throw new InvalidOperationException(
                $"Mail request {requestId:D} suppression recipient update affected {affected} " +
                $"rows; expected {expectedRecipientCount}.");
        }
    }

    public static async Task<bool> TryUpdateRequestTerminalAsync(
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

    public static Task InsertAttemptAsync(
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
