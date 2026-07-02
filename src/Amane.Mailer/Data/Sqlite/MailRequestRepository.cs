using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Amane.Mailer.Data.Sqlite;

public sealed class MailRequestRepository(SqliteConnectionFactory connections)
{
    private const string ExpiredProcessingReaperProvider = "lease-reaper";
    private const string ExpiredProcessingMaxAttemptsErrorCode = "PROCESSING_LEASE_EXPIRED_MAX_ATTEMPTS";
    private const string ExpiredProcessingMaxAttemptsErrorMessage =
        "Processing lease expired after the request reached max_attempts.";

    public async Task<AdminMailRequestListPage> ListForAdminAsync(
        AdminMailRequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);

        List<AdminMailRequestListRow> rows;
        if (query.Status is not null)
        {
            rows = await ListForAdminStatusAsync(connection, query, query.Status.Value, limit, cancellationToken);
        }
        else
        {
            rows = new List<AdminMailRequestListRow>(limit * 5);
            foreach (var status in KnownAdminListStatuses)
            {
                rows.AddRange(await ListForAdminStatusAsync(connection, query, status, limit, cancellationToken));
            }

            rows = rows
                .OrderByDescending(row => row.UpdatedAt)
                .ThenByDescending(row => row.Id)
                .Take(limit)
                .ToList();
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminMailRequestCursor.Encode(last.UpdatedAt, last.Id);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminMailRequestListPage(rows, nextCursor);
    }

    public async Task<AdminDeadLetterListPage> ListDeadLettersForAdminAsync(
        AdminDeadLetterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var limit = pageSize + 1;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Limit", limit);

        var where = new StringBuilder("""
            WHERE status = @Status
              AND completed_at IS NOT NULL
            """);

        AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (query.CursorCompletedAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (completed_at < @CursorCompletedAt OR (completed_at = @CursorCompletedAt AND id < @CursorId))");
            command.Parameters.AddWithValue("@CursorCompletedAt", query.CursorCompletedAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id,
                recipient_email, subject, last_error_message,
                attempt_count, max_attempts, completed_at
            FROM mail_requests
            {{where}}
            ORDER BY completed_at DESC, id DESC
            LIMIT @Limit;
            """;

        var rows = new List<AdminDeadLetterListRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminDeadLetterListRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                SqliteTime.FromStorage(reader.GetString(9))));
        }

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = AdminDeadLetterCursor.Encode(last.CompletedAt, last.Id);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new AdminDeadLetterListPage(rows, nextCursor);
    }

    public async Task<int> CountDeadLettersForAdminAsync(
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE status = @Status");
        AppendTenantScopeFilter(where, command, allowedTenantIds);
        command.CommandText = $$"""
            SELECT COUNT(*)
            FROM mail_requests
            {{where}};
            """;
        command.Parameters.AddWithValue("@Status", (int)MailRequestState.DeadLettered);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly int[] KnownAdminListStatuses =
    [
        (int)MailRequestState.Queued,
        (int)MailRequestState.Processing,
        (int)MailRequestState.Delivered,
        (int)MailRequestState.Failed,
        (int)MailRequestState.DeadLettered,
    ];

    private static async Task<List<AdminMailRequestListRow>> ListForAdminStatusAsync(
        SqliteConnection connection,
        AdminMailRequestListQuery query,
        int status,
        int limit,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder("WHERE status = @Status");
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@Status", status);

        if (query.TenantId is not null)
        {
            where.AppendLine();
            where.Append("  AND tenant_id = @TenantId");
            command.Parameters.AddWithValue("@TenantId", query.TenantId.Value.ToString("D"));
        }

        AppendTenantScopeFilter(where, command, query.AllowedTenantIds);

        if (!string.IsNullOrWhiteSpace(query.SourceService))
        {
            where.AppendLine();
            where.Append("  AND source_service = @SourceService");
            command.Parameters.AddWithValue("@SourceService", query.SourceService);
        }

        if (query.CursorUpdatedAt is not null && query.CursorId is not null)
        {
            where.AppendLine();
            where.Append("  AND (updated_at < @CursorUpdatedAt OR (updated_at = @CursorUpdatedAt AND id < @CursorId))");
            command.Parameters.AddWithValue("@CursorUpdatedAt", query.CursorUpdatedAt);
            command.Parameters.AddWithValue("@CursorId", query.CursorId.Value.ToString("D"));
        }

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id,
                recipient_email, subject, status, attempt_count, max_attempts,
                updated_at
            FROM mail_requests
            {{where}}
            ORDER BY updated_at DESC, id DESC
            LIMIT @Limit;
            """;
        command.Parameters.AddWithValue("@Limit", limit);

        var rows = new List<AdminMailRequestListRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminMailRequestListRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                (MailRequestState)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                SqliteTime.FromStorage(reader.GetString(9))));
        }

        return rows;
    }

    public async Task<AdminMailRequestDetail?> GetDetailForAdminAsync(
        Guid id,
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("WHERE id = @Id");
        AppendTenantScopeFilter(where, command, allowedTenantIds);

        command.CommandText = $$"""
            SELECT
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts,
                next_attempt_at, lock_token, lock_expires_at,
                delivered_at, failed_at, last_error_message,
                accepted_at, created_at, updated_at, completed_at
            FROM mail_requests
            {{where}}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AdminMailRequestDetail(
            Id: Guid.Parse(reader.GetString(0)),
            TenantId: Guid.Parse(reader.GetString(1)),
            SourceService: reader.GetString(2),
            MailRequestId: Guid.Parse(reader.GetString(3)),
            Purpose: reader.GetString(4),
            PayloadJson: reader.GetString(5),
            PayloadHash: reader.GetString(6),
            Subject: reader.GetString(7),
            HtmlBody: reader.IsDBNull(8) ? null : reader.GetString(8),
            TextBody: reader.IsDBNull(9) ? null : reader.GetString(9),
            ReplyTo: reader.IsDBNull(10) ? null : reader.GetString(10),
            RecipientEmail: reader.GetString(11),
            RecipientDisplayName: reader.IsDBNull(12) ? null : reader.GetString(12),
            MetadataJson: reader.IsDBNull(13) ? null : reader.GetString(13),
            Status: (MailRequestState)reader.GetInt32(14),
            AttemptCount: reader.GetInt32(15),
            MaxAttempts: reader.GetInt32(16),
            NextAttemptAt: reader.IsDBNull(17) ? null : SqliteTime.FromStorage(reader.GetString(17)),
            LockToken: reader.IsDBNull(18) ? null : reader.GetString(18),
            LockExpiresAt: reader.IsDBNull(19) ? null : SqliteTime.FromStorage(reader.GetString(19)),
            DeliveredAt: reader.IsDBNull(20) ? null : SqliteTime.FromStorage(reader.GetString(20)),
            FailedAt: reader.IsDBNull(21) ? null : SqliteTime.FromStorage(reader.GetString(21)),
            LastErrorMessage: reader.IsDBNull(22) ? null : reader.GetString(22),
            AcceptedAt: SqliteTime.FromStorage(reader.GetString(23)),
            CreatedAt: SqliteTime.FromStorage(reader.GetString(24)),
            UpdatedAt: SqliteTime.FromStorage(reader.GetString(25)),
            CompletedAt: reader.IsDBNull(26) ? null : SqliteTime.FromStorage(reader.GetString(26)));
    }

    public async Task<IReadOnlyList<AdminMailAttemptRow>> ListAttemptsForAdminAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                attempt_number, provider, status,
                provider_message_id, error_code, error_message,
                started_at, completed_at
            FROM mail_attempts
            WHERE request_id = @RequestId
            ORDER BY attempt_number ASC;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var rows = new List<AdminMailAttemptRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminMailAttemptRow(
                AttemptNumber: reader.GetInt32(0),
                Provider: reader.GetString(1),
                Status: reader.GetInt32(2),
                ProviderMessageId: reader.IsDBNull(3) ? null : reader.GetString(3),
                ErrorCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                ErrorMessage: reader.IsDBNull(5) ? null : reader.GetString(5),
                StartedAt: SqliteTime.FromStorage(reader.GetString(6)),
                CompletedAt: SqliteTime.FromStorage(reader.GetString(7))));
        }

        return rows;
    }

    private static void AppendTenantScopeFilter(
        StringBuilder where,
        SqliteCommand command,
        IReadOnlySet<Guid>? allowedTenantIds)
    {
        if (allowedTenantIds is null)
            return;

        where.AppendLine();
        if (allowedTenantIds.Count == 0)
        {
            where.Append("  AND 1 = 0");
            return;
        }

        var parameterNames = new List<string>(allowedTenantIds.Count);
        var index = 0;
        foreach (var tenantId in allowedTenantIds.OrderBy(id => id))
        {
            var parameterName = "@AllowedTenantId" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, tenantId.ToString("D"));
            index++;
        }

        where.Append("  AND tenant_id IN (");
        where.Append(string.Join(", ", parameterNames));
        where.Append(')');
    }

    public async Task<MailRequestIdempotencyRow?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, payload_hash, status, next_attempt_at
            FROM mail_requests
            WHERE tenant_id = @TenantId
              AND source_service = @SourceService
              AND mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@TenantId", tenantId.ToString("D"));
        command.Parameters.AddWithValue("@SourceService", sourceService);
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MailRequestIdempotencyRow
        {
            Id = Guid.Parse(reader.GetString(0)),
            PayloadHash = reader.GetString(1),
            Status = (MailRequestState)reader.GetInt32(2),
            NextAttemptAt = reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)),
        };
    }

    public async Task InsertAcceptedAsync(
        AcceptedMailRequestInsert insert,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO mail_requests (
                id, tenant_id, source_service, mail_request_id, purpose,
                payload_json, payload_hash, subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name, metadata_json,
                status, attempt_count, max_attempts,
                accepted_at, created_at, updated_at)
            VALUES (
                @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                @PayloadJson, @PayloadHash, @Subject, @HtmlBody, @TextBody, @ReplyTo,
                @RecipientEmail, @RecipientDisplayName, @MetadataJson,
                @Status, 0, @MaxAttempts,
                @AcceptedAt, @CreatedAt, @UpdatedAt);
            """;

        var nowStorage = SqliteTime.ToStorageUtc(insert.AcceptedAt);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@Id", insert.Id.ToString("D"));
            command.Parameters.AddWithValue("@TenantId", insert.TenantId.ToString("D"));
            command.Parameters.AddWithValue("@SourceService", insert.SourceService);
            command.Parameters.AddWithValue("@MailRequestId", insert.MailRequestId.ToString("D"));
            command.Parameters.AddWithValue("@Purpose", insert.Purpose);
            command.Parameters.AddWithValue("@PayloadJson", insert.PayloadJson);
            command.Parameters.AddWithValue("@PayloadHash", insert.PayloadHash);
            command.Parameters.AddWithValue("@Subject", insert.Subject);
            command.Parameters.AddWithValue("@HtmlBody", (object?)insert.HtmlBody ?? DBNull.Value);
            command.Parameters.AddWithValue("@TextBody", (object?)insert.TextBody ?? DBNull.Value);
            command.Parameters.AddWithValue("@ReplyTo", (object?)insert.ReplyTo ?? DBNull.Value);
            command.Parameters.AddWithValue("@RecipientEmail", insert.RecipientEmail);
            command.Parameters.AddWithValue("@RecipientDisplayName", (object?)insert.RecipientDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@MetadataJson", (object?)insert.MetadataJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", (int)MailRequestState.Queued);
            command.Parameters.AddWithValue("@MaxAttempts", insert.MaxAttempts);
            command.Parameters.AddWithValue("@AcceptedAt", nowStorage);
            command.Parameters.AddWithValue("@CreatedAt", nowStorage);
            command.Parameters.AddWithValue("@UpdatedAt", nowStorage);

            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MailRequestRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE mail_requests
            SET
                status = @ProcessingStatus,
                lock_token = @LockToken,
                lock_expires_at = @LockExpiresAt,
                attempt_count = attempt_count + 1,
                updated_at = @Now
            WHERE id = (
                SELECT id
                FROM mail_requests
                WHERE
                    (status = @QueuedStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                ORDER BY created_at ASC
                LIMIT 1
            )
              AND (
                    (status = @QueuedStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                  )
            RETURNING
                id, tenant_id, source_service, mail_request_id,
                subject, html_body, text_body, reply_to,
                recipient_email, recipient_display_name,
                attempt_count, max_attempts, lock_token, lock_expires_at;
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var lockExpiresAtStorage = SqliteTime.ToStorageUtc(now.Add(leaseDuration));

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
            command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
            command.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));
            command.Parameters.AddWithValue("@LockExpiresAt", lockExpiresAtStorage);
            command.Parameters.AddWithValue("@Now", nowStorage);

            MailRequestRow? row = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    row = MapClaimedRow(reader);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return row;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ExpiredProcessingDeadLetteredRequest>> DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string selectSql = """
            SELECT id, mail_request_id, attempt_count, lock_token, updated_at
            FROM mail_requests
            WHERE status = @ProcessingStatus
              AND lock_token IS NOT NULL
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count >= max_attempts
            ORDER BY lock_expires_at ASC, created_at ASC
            LIMIT @BatchSize;
            """;

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @DeadLetteredStatus,
                next_attempt_at = NULL,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @Now,
                failed_at = @Now,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at IS NOT NULL
              AND lock_expires_at <= @Now
              AND attempt_count = @AttemptNumber
              AND attempt_count >= max_attempts;
            """;

        const string insertAttemptSql = """
            INSERT INTO mail_attempts (
                request_id, attempt_number, provider, status,
                provider_message_id, error_code, error_message, retryable,
                lock_token, started_at, completed_at)
            VALUES (
                @RequestId, @AttemptNumber, @Provider, @AttemptStatus,
                NULL, @ErrorCode, @ErrorMessage, 1,
                @LockToken, @StartedAt, @CompletedAt);
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var requestedBatchSize = Math.Max(1, batchSize);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            var candidates = new List<(Guid Id, Guid MailRequestId, int AttemptNumber, Guid LockToken, DateTimeOffset StartedAt)>();

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = selectSql;
                select.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                select.Parameters.AddWithValue("@Now", nowStorage);
                select.Parameters.AddWithValue("@BatchSize", requestedBatchSize);

                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidates.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt32(2),
                        Guid.Parse(reader.GetString(3)),
                        SqliteTime.FromStorage(reader.GetString(4))));
                }
            }

            if (candidates.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var deadLettered = new List<ExpiredProcessingDeadLetteredRequest>(candidates.Count);
            foreach (var candidate in candidates)
            {
                await using (var update = connection.CreateCommand())
                {
                    update.CommandText = updateSql;
                    update.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
                    update.Parameters.AddWithValue("@Now", nowStorage);
                    update.Parameters.AddWithValue("@LastErrorMessage", ExpiredProcessingMaxAttemptsErrorMessage);
                    update.Parameters.AddWithValue("@Id", candidate.Id.ToString("D"));
                    update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                    update.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                    update.Parameters.AddWithValue("@AttemptNumber", candidate.AttemptNumber);

                    var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                    if (affected == 0)
                    {
                        continue;
                    }
                }

                await using (var insertAttempt = connection.CreateCommand())
                {
                    insertAttempt.CommandText = insertAttemptSql;
                    insertAttempt.Parameters.AddWithValue("@RequestId", candidate.Id.ToString("D"));
                    insertAttempt.Parameters.AddWithValue("@AttemptNumber", candidate.AttemptNumber);
                    insertAttempt.Parameters.AddWithValue("@Provider", ExpiredProcessingReaperProvider);
                    insertAttempt.Parameters.AddWithValue("@AttemptStatus", (int)MailRequestState.DeadLettered);
                    insertAttempt.Parameters.AddWithValue("@ErrorCode", ExpiredProcessingMaxAttemptsErrorCode);
                    insertAttempt.Parameters.AddWithValue("@ErrorMessage", ExpiredProcessingMaxAttemptsErrorMessage);
                    insertAttempt.Parameters.AddWithValue("@LockToken", candidate.LockToken.ToString("D"));
                    insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(candidate.StartedAt));
                    insertAttempt.Parameters.AddWithValue("@CompletedAt", nowStorage);
                    await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
                }

                deadLettered.Add(new ExpiredProcessingDeadLetteredRequest(
                    candidate.Id,
                    candidate.MailRequestId,
                    candidate.AttemptNumber,
                    ExpiredProcessingMaxAttemptsErrorCode,
                    ExpiredProcessingMaxAttemptsErrorMessage));
            }

            await transaction.CommitAsync(cancellationToken);
            return deadLettered;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> FinalizeAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        MailRequestFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorMessage,
        MailAttemptInsert attempt,
        CancellationToken cancellationToken = default)
    {
        var (newStatus, completedAt, deliveredAt, failedAt) = MapOutcome(outcome, now, nextAttemptAt);
        var nowStorage = SqliteTime.ToStorageUtc(now);

        const string updateSql = """
            UPDATE mail_requests
            SET
                status = @NewStatus,
                next_attempt_at = @NextAttemptAt,
                lock_token = NULL,
                lock_expires_at = NULL,
                updated_at = @Now,
                completed_at = @CompletedAt,
                delivered_at = @DeliveredAt,
                failed_at = @FailedAt,
                last_error_message = @LastErrorMessage
            WHERE id = @Id
              AND status = @ProcessingStatus
              AND lock_token = @LockToken
              AND lock_expires_at > @Now;
            """;

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

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = updateSql;
                update.Parameters.AddWithValue("@NewStatus", (int)newStatus);
                update.Parameters.AddWithValue(
                    "@NextAttemptAt",
                    nextAttemptAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(nextAttemptAt.Value));
                update.Parameters.AddWithValue("@Now", nowStorage);
                update.Parameters.AddWithValue(
                    "@CompletedAt",
                    completedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(completedAt.Value));
                update.Parameters.AddWithValue(
                    "@DeliveredAt",
                    deliveredAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(deliveredAt.Value));
                update.Parameters.AddWithValue(
                    "@FailedAt",
                    failedAt is null ? DBNull.Value : SqliteTime.ToStorageUtc(failedAt.Value));
                update.Parameters.AddWithValue("@LastErrorMessage", (object?)lastErrorMessage ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", id.ToString("D"));
                update.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
                update.Parameters.AddWithValue("@LockToken", lockToken.ToString("D"));

                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return false;
                }
            }

            await using (var insertAttempt = connection.CreateCommand())
            {
                insertAttempt.CommandText = insertAttemptSql;
                insertAttempt.Parameters.AddWithValue("@RequestId", attempt.RequestId.ToString("D"));
                insertAttempt.Parameters.AddWithValue("@AttemptNumber", attempt.AttemptNumber);
                insertAttempt.Parameters.AddWithValue("@Provider", attempt.Provider);
                insertAttempt.Parameters.AddWithValue("@AttemptStatus", (int)attempt.Status);
                insertAttempt.Parameters.AddWithValue("@ProviderMessageId", (object?)attempt.ProviderMessageId ?? DBNull.Value);
                insertAttempt.Parameters.AddWithValue("@ErrorCode", (object?)attempt.ErrorCode ?? DBNull.Value);
                insertAttempt.Parameters.AddWithValue("@ErrorMessage", (object?)attempt.ErrorMessage ?? DBNull.Value);
                insertAttempt.Parameters.AddWithValue("@Retryable", attempt.Retryable ? 1 : 0);
                insertAttempt.Parameters.AddWithValue("@LockToken", attempt.LockToken.ToString("D"));
                insertAttempt.Parameters.AddWithValue("@StartedAt", SqliteTime.ToStorageUtc(attempt.StartedAt));
                insertAttempt.Parameters.AddWithValue("@CompletedAt", SqliteTime.ToStorageUtc(attempt.CompletedAt));
                await insertAttempt.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<bool> HasDispatchableWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM mail_requests
                WHERE
                    (status = @QueuedStatus AND (next_attempt_at IS NULL OR next_attempt_at <= @Now))
                    OR (
                        status = @ProcessingStatus
                        AND lock_expires_at IS NOT NULL
                        AND lock_expires_at <= @Now
                        AND attempt_count < max_attempts
                    )
                LIMIT 1
            );
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@QueuedStatus", (int)MailRequestState.Queued);
        command.Parameters.AddWithValue("@ProcessingStatus", (int)MailRequestState.Processing);
        command.Parameters.AddWithValue("@Now", nowStorage);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<MailRequestDispatchState?> FindDispatchStateByMailRequestIdAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, status, attempt_count, next_attempt_at, completed_at, last_error_message, lock_token
            FROM mail_requests
            WHERE mail_request_id = @MailRequestId
            LIMIT 1;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MailRequestDispatchState
        {
            Id = Guid.Parse(reader.GetString(0)),
            Status = (MailRequestState)reader.GetInt32(1),
            AttemptCount = reader.GetInt32(2),
            NextAttemptAt = reader.IsDBNull(3) ? null : SqliteTime.FromStorage(reader.GetString(3)),
            CompletedAt = reader.IsDBNull(4) ? null : SqliteTime.FromStorage(reader.GetString(4)),
            LastErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
            LockToken = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        };
    }

    public async Task<int> CountAttemptsAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM mail_attempts
            WHERE request_id = @RequestId;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? (int)count : 0;
    }

    public async Task<int> DeleteExpiredCompletedAsync(
        DateTimeOffset completedBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM mail_requests
            WHERE id IN (
                SELECT id
                FROM mail_requests
                WHERE status IN (@DeliveredStatus, @FailedStatus, @DeadLetteredStatus)
                  AND completed_at IS NOT NULL
                  AND completed_at < @CompletedBefore
                ORDER BY completed_at ASC
                LIMIT @BatchSize
            );
            """;

        var completedBeforeStorage = SqliteTime.ToStorageUtc(completedBefore);

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@DeliveredStatus", (int)MailRequestState.Delivered);
            command.Parameters.AddWithValue("@FailedStatus", (int)MailRequestState.Failed);
            command.Parameters.AddWithValue("@DeadLetteredStatus", (int)MailRequestState.DeadLettered);
            command.Parameters.AddWithValue("@CompletedBefore", completedBeforeStorage);
            command.Parameters.AddWithValue("@BatchSize", batchSize);

            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpsertHeartbeatAsync(
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO worker_heartbeats (name, last_heartbeat_at)
            VALUES (@Name, @Now)
            ON CONFLICT(name) DO UPDATE SET last_heartbeat_at = excluded.last_heartbeat_at;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Models.WorkerHeartbeat>> GetHeartbeatsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT name, last_heartbeat_at FROM worker_heartbeats;";

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var heartbeats = new List<Models.WorkerHeartbeat>();
        while (await reader.ReadAsync(cancellationToken))
        {
            heartbeats.Add(new Models.WorkerHeartbeat(
                reader.GetString(0),
                SqliteTime.FromStorage(reader.GetString(1))));
        }

        return heartbeats;
    }

    private static MailRequestRow MapClaimedRow(SqliteDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            SourceService = reader.GetString(2),
            MailRequestId = Guid.Parse(reader.GetString(3)),
            Subject = reader.GetString(4),
            HtmlBody = reader.IsDBNull(5) ? null : reader.GetString(5),
            TextBody = reader.IsDBNull(6) ? null : reader.GetString(6),
            ReplyTo = reader.IsDBNull(7) ? null : reader.GetString(7),
            RecipientEmail = reader.GetString(8),
            RecipientDisplayName = reader.IsDBNull(9) ? null : reader.GetString(9),
            AttemptCount = reader.GetInt32(10),
            MaxAttempts = reader.GetInt32(11),
            LockToken = Guid.Parse(reader.GetString(12)),
            LockExpiresAt = SqliteTime.FromStorage(reader.GetString(13)),
            Status = MailRequestState.Processing,
        };

    private static (MailRequestState Status, DateTimeOffset? CompletedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? FailedAt)
        MapOutcome(MailRequestFinalizeOutcome outcome, DateTimeOffset now, DateTimeOffset? nextAttemptAt) =>
        outcome switch
        {
            MailRequestFinalizeOutcome.Delivered => (
                MailRequestState.Delivered,
                now,
                now,
                null),
            MailRequestFinalizeOutcome.RetryScheduled => (
                MailRequestState.Queued,
                null,
                null,
                null),
            MailRequestFinalizeOutcome.Failed => (
                MailRequestState.Failed,
                now,
                null,
                now),
            MailRequestFinalizeOutcome.DeadLettered => (
                MailRequestState.DeadLettered,
                now,
                null,
                now),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}
