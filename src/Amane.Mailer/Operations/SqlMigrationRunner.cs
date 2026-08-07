using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Operations;

public sealed class SqlMigrationRunner
{
    internal sealed record MigrationTransactionStep(
        Func<SqliteConnection, CancellationToken, Task> ValidatePreconditionBeforeScriptAsync,
        Func<SqliteConnection, CancellationToken, Task> ApplyDataMigrationAfterScriptAsync);

    private static readonly IReadOnlyDictionary<string, MigrationTransactionStep> KnownTransactionSteps =
        new Dictionary<string, MigrationTransactionStep>(StringComparer.Ordinal)
        {
            [RecipientPersistenceMigration.MigrationVersion] = RecipientPersistenceMigration.Step,
            [RecipientDeliveryEventMigration.MigrationVersion] = RecipientDeliveryEventMigration.Step,
        };

    private readonly SqliteConnectionFactory _connections;
    private readonly string _migrationDirectory;

    public SqlMigrationRunner(SqliteConnectionFactory connections)
        : this(connections, Path.Combine(AppContext.BaseDirectory, "Data", "Migrations"))
    {
    }

    internal SqlMigrationRunner(SqliteConnectionFactory connections, string migrationDirectory)
    {
        _connections = connections;
        _migrationDirectory = migrationDirectory;
    }

    /// <summary>
    /// Test-only gate invoked after a migration script and schema_migrations insert run,
    /// and before COMMIT. Used to cancel mid-transaction without relying on wall-clock sleep.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeMigrationCommitForTests { get; set; }

    /// <summary>
    /// Returns true when the database has every bundled migration applied with matching
    /// checksums and the objects required by the current binary (including
    /// <c>delivery_events</c> and <c>mail_requests.scheduled_at</c>).
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> only for intentional schema mismatch (missing directory/files,
    /// missing required objects, applied version/checksum drift). Probe execution failures
    /// such as <see cref="SqliteException"/>, I/O errors, and cancellation propagate so
    /// <see cref="MailerReadinessEvaluator"/> can classify them (#342).
    /// </remarks>
    public async Task<bool> IsCurrentSchemaReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_migrationDirectory))
        {
            return false;
        }

        var migrations = await LoadMigrationsAsync(_migrationDirectory, cancellationToken);
        if (migrations.Count == 0)
        {
            return false;
        }

        await using var connection = await _connections.OpenSchemaProbeConnectionAsync(cancellationToken);
        if (!await HasRequiredRuntimeSchemaObjectsAsync(connection, cancellationToken))
        {
            return false;
        }

        if (!await HasChecksumColumnAsync(connection, cancellationToken))
        {
            return false;
        }

        var migrationsByVersion = migrations.ToDictionary(
            migration => migration.Version,
            StringComparer.Ordinal);
        var appliedMigrations = await GetAppliedMigrationsAsync(connection, cancellationToken);

        if (appliedMigrations.Count != migrationsByVersion.Count)
        {
            return false;
        }

        foreach (var appliedMigration in appliedMigrations.Values)
        {
            if (!migrationsByVersion.TryGetValue(appliedMigration.Version, out var migrationFile))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(appliedMigration.Checksum)
                || !string.Equals(
                    migrationFile.Checksum,
                    appliedMigration.Checksum,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var migration in migrations)
        {
            if (!appliedMigrations.ContainsKey(migration.Version))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Read-only schema classification used by <c>db migrate --status</c> and the Managed apply
    /// engine. Never creates the database, never writes, and never applies a migration.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="SetupSchemaClassification.Behind"/> only when the applied set is a
    /// contiguous, checksum-matching prefix of the bundled set. An unknown applied version, a gap
    /// in the applied prefix, or checksum drift is
    /// <see cref="SetupSchemaClassification.AheadOrUnsupported"/> because it cannot be resolved by
    /// applying forward-only migrations.
    /// </remarks>
    public async Task<string> ClassifySchemaAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_migrationDirectory))
        {
            return SetupSchemaClassification.Unknown;
        }

        IReadOnlyList<MigrationFile> bundled;
        try
        {
            bundled = await LoadMigrationsAsync(_migrationDirectory, cancellationToken);
        }
        catch (IOException)
        {
            return SetupSchemaClassification.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return SetupSchemaClassification.Unknown;
        }

        if (bundled.Count == 0)
        {
            return SetupSchemaClassification.Unknown;
        }

        try
        {
            // Schema-probe connections never create the file, so an absent database stays absent.
            await using var connection = await _connections.OpenSchemaProbeConnectionAsync(cancellationToken);

            if (!await HasSchemaMigrationsTableAsync(connection, cancellationToken))
            {
                return SetupSchemaClassification.DatabaseAbsent;
            }

            var applied = await GetAppliedMigrationsAsync(connection, cancellationToken);
            if (applied.Count == 0)
            {
                return SetupSchemaClassification.DatabaseAbsent;
            }

            return ClassifyAppliedSet(bundled, applied);
        }
        catch (SqliteException)
        {
            // A missing database surfaces as an open error because the probe connection refuses
            // create-missing-file. Anything else that fails to open is genuinely unknown.
            var databasePath = _connections.GetConfiguredDatabasePath();
            return databasePath is not null && !File.Exists(databasePath)
                ? SetupSchemaClassification.DatabaseAbsent
                : SetupSchemaClassification.Unknown;
        }
        catch (IOException)
        {
            return SetupSchemaClassification.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return SetupSchemaClassification.Unknown;
        }
    }

    private static string ClassifyAppliedSet(
        IReadOnlyList<MigrationFile> bundled,
        IReadOnlyDictionary<string, AppliedMigration> applied)
    {
        var bundledByVersion = bundled.ToDictionary(migration => migration.Version, StringComparer.Ordinal);
        foreach (var version in applied.Keys)
        {
            if (!bundledByVersion.ContainsKey(version))
            {
                return SetupSchemaClassification.AheadOrUnsupported;
            }
        }

        // Walk the bundled order: everything before the first pending migration must be applied
        // with a matching checksum, and nothing may be applied after it.
        var pendingSeen = false;
        foreach (var migration in bundled)
        {
            if (!applied.TryGetValue(migration.Version, out var appliedMigration))
            {
                pendingSeen = true;
                continue;
            }

            if (pendingSeen)
            {
                return SetupSchemaClassification.AheadOrUnsupported;
            }

            if (string.IsNullOrWhiteSpace(appliedMigration.Checksum)
                || !string.Equals(migration.Checksum, appliedMigration.Checksum, StringComparison.Ordinal))
            {
                return SetupSchemaClassification.AheadOrUnsupported;
            }
        }

        return pendingSeen
            ? SetupSchemaClassification.Behind
            : SetupSchemaClassification.Current;
    }

    private static async Task<bool> HasSchemaMigrationsTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'schema_migrations'
            LIMIT 1;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_migrationDirectory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {_migrationDirectory}");
        }

        var migrations = await LoadMigrationsAsync(_migrationDirectory, cancellationToken);

        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await EnsureSchemaMigrationsTableAsync(connection, migrations, cancellationToken);

        var migrationsByVersion = migrations.ToDictionary(migration => migration.Version, StringComparer.Ordinal);
        var appliedMigrations = await GetAppliedMigrationsAsync(connection, cancellationToken);
        EnsureAppliedMigrationFilesExist(migrationsByVersion, appliedMigrations);

        var applied = new List<string>();
        foreach (var migration in migrations)
        {
            if (appliedMigrations.TryGetValue(migration.Version, out var appliedMigration))
            {
                EnsureChecksumMatches(migration, appliedMigration);
                continue;
            }

            // CA2000: prefer explicit try/finally DisposeAsync over `await using var` here.
            // The analyzer false-positives the using-declaration form in this loop+continue path.
            var transaction = await SqliteImmediateTransaction.BeginAsync(connection, cancellationToken);
            try
            {
                // Migration 016 order: BEGIN IMMEDIATE -> precondition -> schema script ->
                // backfill/classification/assertion -> schema_migrations record -> COMMIT.
                if (KnownTransactionSteps.TryGetValue(migration.Version, out var transactionStep))
                {
                    await transactionStep.ValidatePreconditionBeforeScriptAsync(connection, cancellationToken);
                }

                await using (var script = connection.CreateCommand())
                {
                    script.CommandText = migration.Sql;
                    await script.ExecuteNonQueryAsync(cancellationToken);
                }

                if (KnownTransactionSteps.TryGetValue(migration.Version, out transactionStep))
                {
                    await transactionStep.ApplyDataMigrationAfterScriptAsync(connection, cancellationToken);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.CommandText = """
                        INSERT INTO schema_migrations (version, applied_at, checksum)
                        VALUES (@Version, @AppliedAt, @Checksum);
                        """;
                    record.Parameters.AddWithValue("@Version", migration.Version);
                    record.Parameters.AddWithValue("@AppliedAt", SqliteTime.ToStorageUtc(SqliteTime.UtcNow));
                    record.Parameters.AddWithValue("@Checksum", migration.Checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                if (BeforeMigrationCommitForTests is { } beforeCommit)
                {
                    await beforeCommit(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(migration.Version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }

        return applied;
    }

    private static async Task<IReadOnlyList<MigrationFile>> LoadMigrationsAsync(
        string migrationDirectory,
        CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var migrations = new List<MigrationFile>(files.Length);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var sql = DecodeUtf8Sql(bytes);
            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            migrations.Add(new MigrationFile(Path.GetFileName(file), sql, checksum));
        }

        return migrations;
    }

    private static string DecodeUtf8Sql(byte[] bytes)
    {
        var offset = bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF
            ? 3
            : 0;

        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static async Task EnsureSchemaMigrationsTableAsync(
        SqliteConnection connection,
        IReadOnlyList<MigrationFile> migrations,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     TEXT NOT NULL PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                checksum    TEXT NOT NULL CHECK (length(checksum) = 64)
            );
            """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var hasChecksumColumn = await HasChecksumColumnAsync(connection, cancellationToken);
        if (!hasChecksumColumn)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = """
                ALTER TABLE schema_migrations
                ADD COLUMN checksum TEXT CHECK (checksum IS NULL OR length(checksum) = 64);
                """;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (hasChecksumColumn && !await HasMissingChecksumAsync(connection, cancellationToken))
        {
            return;
        }

        foreach (var migration in migrations)
        {
            await using var backfill = connection.CreateCommand();
            backfill.CommandText = """
                UPDATE schema_migrations
                SET checksum = @Checksum
                WHERE version = @Version
                  AND checksum IS NULL;
                """;
            backfill.Parameters.AddWithValue("@Version", migration.Version);
            backfill.Parameters.AddWithValue("@Checksum", migration.Checksum);
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> HasRequiredRuntimeSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var tables = connection.CreateCommand())
        {
            tables.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                    'schema_migrations',
                    'mail_requests',
                    'mail_attempts',
                    'worker_heartbeats',
                    'delivery_events',
                    'mail_request_attachments',
                    'mail_attachment_submissions',
                    'mailer_maintenance_leases',
                    'mail_request_recipients',
                    'mail_plain_submissions',
                    'recipient_delivery_events');
                """;
            var tableCount = await tables.ExecuteScalarAsync(cancellationToken);
            if (tableCount is not long count || count != 11L)
            {
                return false;
            }
        }

        // Migration-specific test databases may intentionally stop at an earlier bundled
        // migration (for example, the 017 backfill tests). The current production bundle
        // includes 018, so its presence in this runner's directory makes the capability table
        // a required runtime object without making an isolated 017 fixture claim it is current.
        if (File.Exists(Path.Combine(_migrationDirectory, "018_admin_user_capabilities.sql"))
            && !await HasTableAsync(connection, "admin_user_capabilities", cancellationToken))
        {
            return false;
        }

        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(mail_requests);";
        var hasScheduledAt = false;
        var hasAttachmentCount = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "scheduled_at", StringComparison.Ordinal))
                {
                    hasScheduledAt = true;
                }
                else if (string.Equals(columnName, "attachment_count", StringComparison.Ordinal))
                {
                    hasAttachmentCount = true;
                }
            }
        }

        if (!hasScheduledAt || !hasAttachmentCount)
        {
            return false;
        }

        await using var recipientColumns = connection.CreateCommand();
        recipientColumns.CommandText = "PRAGMA table_info(mail_request_recipients);";
        var requiredRecipientColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "last_feedback_occurred_at",
            "last_feedback_provider",
            "last_feedback_event_id",
        };
        await using (var reader = await recipientColumns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                requiredRecipientColumns.Remove(reader.GetString(1));
            }
        }

        if (requiredRecipientColumns.Count != 0)
        {
            return false;
        }

        await using var eventColumns = connection.CreateCommand();
        eventColumns.CommandText = "PRAGMA table_info(recipient_delivery_events);";
        var requiredEventColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "tenant_id",
            "source_service",
            "mail_request_id",
            "recipient_role",
            "recipient_ordinal",
            "provider",
            "provider_event_id",
            "provider_message_id",
            "provider_status",
            "applied_delivery_state",
            "status_message",
            "occurred_at",
            "created_at",
        };
        await using (var reader = await eventColumns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                requiredEventColumns.Remove(reader.GetString(1));
            }
        }

        if (requiredEventColumns.Count != 0)
        {
            return false;
        }

        await using var eventIndexes = connection.CreateCommand();
        eventIndexes.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN (
                'ix_recipient_delivery_events_request_occurred',
                'ix_recipient_delivery_events_provider_message');
            """;
        return Convert.ToInt64(await eventIndexes.ExecuteScalarAsync(cancellationToken)) == 2;
    }

    private static async Task<bool> HasChecksumColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(schema_migrations);";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "checksum", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = @TableName);
            """;
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture)
            == 1;
    }

    private static async Task<bool> HasMissingChecksumAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM schema_migrations WHERE checksum IS NULL LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<IReadOnlyDictionary<string, AppliedMigration>> GetAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var migrations = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations ORDER BY version;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetString(0);
            var checksum = reader.IsDBNull(1) ? null : reader.GetString(1);
            migrations.Add(version, new AppliedMigration(version, checksum));
        }

        return migrations;
    }

    private static void EnsureAppliedMigrationFilesExist(
        IReadOnlyDictionary<string, MigrationFile> migrationsByVersion,
        IReadOnlyDictionary<string, AppliedMigration> appliedMigrations)
    {
        foreach (var appliedMigration in appliedMigrations.Values)
        {
            if (migrationsByVersion.ContainsKey(appliedMigration.Version))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Applied database migration '{appliedMigration.Version}' is not present in the migration directory. "
                + "Migration SQL files are forward-only and must remain bundled after release.");
        }
    }

    private static void EnsureChecksumMatches(MigrationFile migration, AppliedMigration appliedMigration)
    {
        if (string.IsNullOrWhiteSpace(appliedMigration.Checksum))
        {
            throw new InvalidOperationException(
                $"Applied database migration '{appliedMigration.Version}' is missing a checksum.");
        }

        if (!string.Equals(migration.Checksum, appliedMigration.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Applied database migration '{migration.Version}' checksum mismatch. "
                + $"Stored checksum is {appliedMigration.Checksum}; current file checksum is {migration.Checksum}. "
                + "Migration SQL files are forward-only and must not be edited after release.");
        }
    }

    private sealed record MigrationFile(string Version, string Sql, string Checksum);

    private sealed record AppliedMigration(string Version, string? Checksum);
}
