namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Result of <see cref="MailerMaintenanceLeaseStore.TryAcquireAsync"/>: whether the lease was
/// acquired, and (when acquired) the <c>fencing_token</c> now on the row. Callers must present
/// this same fencing token on every subsequent <c>RenewAsync</c>/<c>ReleaseAsync</c>/publish-time
/// validity check for the duration of their hold.
/// </summary>
public readonly record struct LeaseAcquireResult(bool Acquired, long FencingToken)
{
    public static LeaseAcquireResult NotAcquired { get; } = new(false, 0);
}

/// <summary>
/// Durable cross-process maintenance lease shared by Admin backup, CLI backup, and the
/// attachment acceptance gate (ADR 0022 D-09). <see cref="TryAcquireAsync"/> is a single atomic
/// UPSERT: it only takes the lease when absent or already expired, incrementing
/// <c>fencing_token</c> only on a genuine new acquire (never on renewal), so a stale holder's
/// renewal attempt can be told apart from a fresh acquire after expiry.
///
/// <c>fencing_token</c> is the fencing token for this lease: every holder must present the
/// exact token it received from <see cref="TryAcquireAsync"/> to renew, release, or validate
/// the lease. This closes the "expired lease revived" gap in <c>owner_token</c>-only fencing --
/// an owner token can only ever be reused if the caller's <see cref="Guid"/> generator produces
/// a collision, but relying on that alone still let a renewal succeed with no expiry check at
/// all (post-merge review of #533/PR #537). Requiring both owner and fencing token, plus an
/// explicit <c>expires_at &gt; @Now</c> check on renewal, makes an expired lease unrecoverable
/// by its original holder even if it presents the correct owner token.
/// </summary>
public sealed class MailerMaintenanceLeaseStore(SqliteConnectionFactory connections)
{
    public const string BackupLeaseName = "backup";

    public async Task<LeaseAcquireResult> TryAcquireAsync(
        string leaseName,
        Guid ownerToken,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Two-step fenced UPDATE then insert-if-absent (same idiom as the rest of this codebase's
        // lease claims, e.g. MailAttachmentSubmissionStore.TryInsertStartedAsync's
        // INSERT ... WHERE NOT EXISTS): reclaim an expired lease via a fenced UPDATE, or create
        // the row fresh if none exists yet. Either branch affecting a row means "acquired," and
        // RETURNING hands back the fencing_token now on the row so the caller can present it on
        // every subsequent renew/release/validity check.
        const string reclaimExpiredSql = """
            UPDATE mailer_maintenance_leases
            SET
                owner_token = @OwnerToken,
                fencing_token = fencing_token + 1,
                expires_at = @ExpiresAt,
                acquired_at = @Now,
                updated_at = @Now
            WHERE lease_name = @LeaseName AND expires_at <= @Now
            RETURNING fencing_token;
            """;

        const string insertIfAbsentSql = """
            INSERT INTO mailer_maintenance_leases (
                lease_name, owner_token, fencing_token, expires_at, acquired_at, updated_at)
            SELECT @LeaseName, @OwnerToken, 1, @ExpiresAt, @Now, @Now
            WHERE NOT EXISTS (
                SELECT 1 FROM mailer_maintenance_leases WHERE lease_name = @LeaseName
            )
            RETURNING fencing_token;
            """;

        var nowStorage = SqliteTime.ToStorageUtc(now);
        var expiresAtStorage = SqliteTime.ToStorageUtc(now.Add(duration));

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
        try
        {
            long? fencingToken = null;
            await using (var reclaim = connection.CreateCommand())
            {
                reclaim.CommandText = reclaimExpiredSql;
                reclaim.Parameters.AddWithValue("@LeaseName", leaseName);
                reclaim.Parameters.AddWithValue("@OwnerToken", ownerToken.ToString("D"));
                reclaim.Parameters.AddWithValue("@ExpiresAt", expiresAtStorage);
                reclaim.Parameters.AddWithValue("@Now", nowStorage);
                await using var reader = await reclaim.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    fencingToken = reader.GetInt64(0);
                }
            }

            if (fencingToken is null)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = insertIfAbsentSql;
                insert.Parameters.AddWithValue("@LeaseName", leaseName);
                insert.Parameters.AddWithValue("@OwnerToken", ownerToken.ToString("D"));
                insert.Parameters.AddWithValue("@ExpiresAt", expiresAtStorage);
                insert.Parameters.AddWithValue("@Now", nowStorage);
                await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    fencingToken = reader.GetInt64(0);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return fencingToken is { } token ? new LeaseAcquireResult(true, token) : LeaseAcquireResult.NotAcquired;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Renews the lease's <c>expires_at</c>, fenced on <c>owner_token</c>, <c>fencing_token</c>,
    /// and <c>expires_at &gt; @Now</c> all matching at once -- an already-expired lease can never
    /// be revived by a renewal, even by its original owner presenting the correct tokens; only a
    /// fresh <see cref="TryAcquireAsync"/> can reclaim it (and that bumps the fencing token,
    /// which the stale holder does not have).
    /// </summary>
    public async Task<bool> RenewAsync(
        string leaseName,
        Guid ownerToken,
        long fencingToken,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE mailer_maintenance_leases
            SET expires_at = @ExpiresAt, updated_at = @Now
            WHERE lease_name = @LeaseName
              AND owner_token = @OwnerToken
              AND fencing_token = @FencingToken
              AND expires_at > @Now;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@LeaseName", leaseName);
        command.Parameters.AddWithValue("@OwnerToken", ownerToken.ToString("D"));
        command.Parameters.AddWithValue("@FencingToken", fencingToken);
        command.Parameters.AddWithValue("@ExpiresAt", SqliteTime.ToStorageUtc(now.Add(duration)));
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>
    /// Releases the lease by expiring it immediately, fenced on the caller's own owner token and
    /// fencing token so a lease already reclaimed by someone else (this holder's renewal having
    /// lapsed) is never released out from under its new owner.
    /// </summary>
    public async Task ReleaseAsync(
        string leaseName,
        Guid ownerToken,
        long fencingToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE mailer_maintenance_leases
            SET expires_at = @Now, updated_at = @Now
            WHERE lease_name = @LeaseName AND owner_token = @OwnerToken AND fencing_token = @FencingToken;
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@LeaseName", leaseName);
        command.Parameters.AddWithValue("@OwnerToken", ownerToken.ToString("D"));
        command.Parameters.AddWithValue("@FencingToken", fencingToken);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Used by the attachment acceptance gate, inside its own SQLite transaction (ADR 0022 D-09).</summary>
    public static async Task<bool> IsHeldWithinTransactionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string leaseName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM mailer_maintenance_leases
                WHERE lease_name = @LeaseName AND expires_at > @Now
            );
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@LeaseName", leaseName);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    public async Task<bool> IsHeldAsync(
        string leaseName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        return await IsHeldWithinTransactionAsync(connection, leaseName, now, cancellationToken);
    }

    /// <summary>
    /// True only if <paramref name="ownerToken"/> and <paramref name="fencingToken"/> both still
    /// match the current row and the lease has not expired -- the publish-time gate a backup
    /// must pass immediately before an artifact is treated as a successful backup (ADR 0022
    /// D-09). Distinct from <see cref="MaintenanceLeaseHeartbeat.IsHealthy"/>: the heartbeat
    /// only proves the last renewal it attempted succeeded, not that nothing has changed in the
    /// DB since (e.g. a renewal that hasn't fired yet after a slow snapshot).
    /// </summary>
    public async Task<bool> IsLeaseCurrentlyValidAsync(
        string leaseName,
        Guid ownerToken,
        long fencingToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM mailer_maintenance_leases
                WHERE lease_name = @LeaseName
                  AND owner_token = @OwnerToken
                  AND fencing_token = @FencingToken
                  AND expires_at > @Now
            );
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@LeaseName", leaseName);
        command.Parameters.AddWithValue("@OwnerToken", ownerToken.ToString("D"));
        command.Parameters.AddWithValue("@FencingToken", fencingToken);
        command.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    /// <summary>
    /// True if any attachment request is not yet in a durable terminal state (ADR 0022 D-09
    /// backup preflight: a successful routine backup must never capture a non-terminal
    /// attachment row without its spool).
    /// </summary>
    public async Task<bool> HasActiveAttachmentRequestsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM mail_requests
                WHERE attachment_count > 0
                  AND status NOT IN (
                        @Delivered, @Failed, @DeadLettered, @Cancelled, @DeliveryUnknown)
            );
            """;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Delivered", (int)MailRequestState.Delivered);
        command.Parameters.AddWithValue("@Failed", (int)MailRequestState.Failed);
        command.Parameters.AddWithValue("@DeadLettered", (int)MailRequestState.DeadLettered);
        command.Parameters.AddWithValue("@Cancelled", (int)MailRequestState.Cancelled);
        command.Parameters.AddWithValue("@DeliveryUnknown", (int)MailRequestState.DeliveryUnknown);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }
}
