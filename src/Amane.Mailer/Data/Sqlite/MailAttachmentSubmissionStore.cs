using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Request-unique provider submission evidence for attachment requests (ADR 0022 D-08). The
/// PRIMARY KEY on <c>mail_attachment_submissions.request_id</c> is the DB-level guarantee that
/// a request can never have more than one submission lifecycle: <see cref="TryInsertStartedAsync"/>
/// fails closed (returns false, no row created) if evidence already exists.
/// </summary>
public sealed class MailAttachmentSubmissionStore(SqliteConnectionFactory connections)
{
    public async Task<AttachmentSubmissionRow?> FindAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT request_id, submission_state, provider, submission_started_at, lock_token,
                   provider_message_id, completed_at
            FROM mail_attachment_submissions
            WHERE request_id = @RequestId;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AttachmentSubmissionRow(
            Guid.Parse(reader.GetString(0)),
            (AttachmentSubmissionState)reader.GetInt32(1),
            reader.GetString(2),
            SqliteTime.FromStorage(reader.GetString(3)),
            Guid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : SqliteTime.FromStorage(reader.GetString(6)));
    }

    /// <summary>
    /// Commits a durable <c>Started</c> marker before any provider invocation (ADR 0022 D-08
    /// provider invocation boundary step 3). Returns false without creating a row if evidence
    /// for this request already exists -- the caller must never create a second submission
    /// lifecycle and must instead converge from the existing evidence.
    /// </summary>
    public async Task<bool> TryInsertStartedAsync(
        Guid requestId,
        string provider,
        Guid lockToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO mail_attachment_submissions (
                request_id, submission_state, provider, submission_started_at,
                lock_token, provider_message_id, completed_at, created_at, updated_at)
            SELECT @RequestId, @Started, @Provider, @Now, @LockToken, NULL, NULL, @Now, @Now
            WHERE NOT EXISTS (
                SELECT 1 FROM mail_attachment_submissions WHERE request_id = @RequestId
            );
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            command.Parameters.AddWithValue("@Started", (int)AttachmentSubmissionState.Started);
            command.Parameters.AddWithValue("@Provider", provider);
            command.Parameters.AddWithValue("@Now", nowStorage);
            command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
