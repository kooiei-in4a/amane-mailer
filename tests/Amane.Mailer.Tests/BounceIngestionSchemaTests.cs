using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class BounceIngestionMigrationTests
{
    [Fact]
    public async Task Db_migrate_011_creates_bounce_tables_indexes_and_preserves_mail_request_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-011", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory);
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.Contains("011_bounce_ingestion.sql", applied);
            Assert.Contains("012_provider_event_inbox_details.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await TableExistsAsync(connection, "provider_event_inbox", ct));
            Assert.True(await TableExistsAsync(connection, "bounce_events", ct));
            Assert.True(await TableExistsAsync(connection, "mail_suppressions", ct));

            var mailRequestColumns = await GetColumnNamesAsync(connection, "mail_requests", ct);
            Assert.Contains("recipient_email", mailRequestColumns);
            Assert.DoesNotContain("bounced_at", mailRequestColumns);

            var mailAttemptColumns = await GetColumnNamesAsync(connection, "mail_attempts", ct);
            Assert.Contains("provider_message_id", mailAttemptColumns);

            var inboxColumns = await GetColumnNamesAsync(connection, "provider_event_inbox", ct);
            Assert.DoesNotContain("payload_json", inboxColumns);
            Assert.DoesNotContain("raw_json", inboxColumns);
            Assert.DoesNotContain("event_json", inboxColumns);
            Assert.Contains("disposition", inboxColumns);
            Assert.Contains("last_error_code", inboxColumns);
            Assert.Contains("status_message", inboxColumns);
            Assert.Contains("occurred_at", inboxColumns);

            var suppressionColumns = await GetColumnNamesAsync(connection, "mail_suppressions", ct);
            Assert.DoesNotContain("removed_at", suppressionColumns);

            var indexes = await GetIndexNamesAsync(connection, ct);
            Assert.Contains("idx_provider_event_inbox_pending_due", indexes);
            Assert.Contains("idx_provider_event_inbox_processing_expired", indexes);
            Assert.Contains("idx_provider_event_inbox_deadletter_completed", indexes);
            Assert.Contains("ix_bounce_events_tenant_occurred", indexes);
            Assert.Contains("ix_bounce_events_mail_request", indexes);
            Assert.Contains("ix_mail_suppressions_tenant_created", indexes);
            Assert.Contains("ix_mail_attempts_provider_message_id", indexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_011_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-011-idem", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));
            var first = await runner.ApplyPendingAsync(ct);
            var second = await runner.ApplyPendingAsync(ct);

            Assert.Contains("011_bounce_ingestion.sql", first);
            Assert.Contains("012_provider_event_inbox_details.sql", first);
            Assert.DoesNotContain("011_bounce_ingestion.sql", second);
            Assert.DoesNotContain("012_provider_event_inbox_details.sql", second);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Db_migrate_012_upgrades_v1_1_0_inbox_and_keeps_existing_rows_processable()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-migration-012", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyMigrationsThrough(migrationDirectory, "011_bounce_ingestion.sql");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory, migrationDirectory);
            var appliedBefore = await runner.ApplyPendingAsync(ct);
            Assert.Contains("011_bounce_ingestion.sql", appliedBefore);
            Assert.DoesNotContain("012_provider_event_inbox_details.sql", appliedBefore);

            var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                var columnsBefore = await GetColumnNamesAsync(connection, "provider_event_inbox", ct);
                Assert.DoesNotContain("status_message", columnsBefore);
                Assert.DoesNotContain("occurred_at", columnsBefore);

                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO provider_event_inbox (
                        id, provider, event_id, provider_message_id, delivery_status, recipient_email,
                        status, disposition, attempt_count, max_attempts, next_attempt_at,
                        created_at, updated_at)
                    VALUES (
                        @Id, 'acs', 'event-pre-012', @MessageId, 'Bounced', 'user@example.com',
                        0, NULL, 0, 3, NULL,
                        @Now, @Now);
                    """;
                seed.Parameters.AddWithValue("@Id", "00000000-0000-0000-0000-000000000460");
                seed.Parameters.AddWithValue("@MessageId", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
                seed.Parameters.AddWithValue("@Now", SqliteTime.ToStorageUtc(now));
                await seed.ExecuteNonQueryAsync(ct);
            }

            File.Copy(
                Path.Combine(GetCurrentMigrationDirectory(), "012_provider_event_inbox_details.sql"),
                Path.Combine(migrationDirectory, "012_provider_event_inbox_details.sql"));

            var applied = await runner.ApplyPendingAsync(ct);
            Assert.Contains("012_provider_event_inbox_details.sql", applied);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(ct);
                var columnsAfter = await GetColumnNamesAsync(connection, "provider_event_inbox", ct);
                Assert.Contains("status_message", columnsAfter);
                Assert.Contains("occurred_at", columnsAfter);

                await using var read = connection.CreateCommand();
                read.CommandText = """
                    SELECT status_message, occurred_at
                    FROM provider_event_inbox
                    WHERE event_id = 'event-pre-012';
                    """;
                await using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
            }

            var claimed = await new ProviderEventInboxRepository(factory)
                .TryClaimOneAsync(now, TimeSpan.FromMinutes(1), ct);
            Assert.NotNull(claimed);
            Assert.Equal("event-pre-012", claimed.EventId);
            Assert.Null(claimed.StatusMessage);
            Assert.Null(claimed.OccurredAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyMigrationsThrough(string destination, string lastVersion)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(GetCurrentMigrationDirectory(), "*.sql", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file)!;
            File.Copy(file, Path.Combine(destination, fileName));
            if (string.Equals(fileName, lastVersion, StringComparison.Ordinal))
            {
                break;
            }
        }
    }

    private static string GetCurrentMigrationDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = @Name
            );
            """;
        command.Parameters.AddWithValue("@Name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<HashSet<string>> GetIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index' AND name IS NOT NULL;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }
}

public sealed class RecipientEmailNormalizerTests
{
    [Theory]
    [InlineData("User@Example.COM", "user@example.com")]
    [InlineData("  mixed.CASE+tag@Example.COM  ", "mixed.case+tag@example.com")]
    public void Normalize_trims_and_lowercases_full_address(string input, string expected)
    {
        Assert.Equal(expected, RecipientEmailNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_rejects_blank(string? input)
    {
        Assert.ThrowsAny<ArgumentException>(() => RecipientEmailNormalizer.Normalize(input!));
    }
}

public sealed class BouncePersistenceRepositoryTests
{
    [Fact]
    public async Task Inbox_claim_finalize_and_dead_letter_at_max_attempts_converge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var repository = new ProviderEventInboxRepository(db.Factory);
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");

        var inserted = await repository.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000101"),
                Provider = "acs",
                EventId = "event-101",
                ProviderMessageId = "11111111-1111-1111-1111-111111111111",
                DeliveryStatus = "Bounced",
                RecipientEmail = "User@Example.COM",
                MaxAttempts = 1,
                CreatedAt = now,
            },
            ct);
        Assert.True(inserted);

        var claimed = await repository.TryClaimOneAsync(now, TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);
        Assert.Equal(ProviderEventInboxState.Processing, claimed.Status);
        Assert.Equal("user@example.com", claimed.RecipientEmail);
        Assert.Equal(1, claimed.AttemptCount);

        var discarded = await repository.FinalizeAsync(
            claimed.Id,
            claimed.LockToken,
            now.AddSeconds(1),
            ProviderEventInboxFinalizeOutcome.Discarded,
            nextAttemptAt: null,
            lastErrorCode: ProviderEventInboxRepository.UnparseableEventErrorCode,
            ct);
        Assert.True(discarded);

        var expiredId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        Assert.True(await repository.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = expiredId,
                Provider = "acs",
                EventId = "event-102",
                MaxAttempts = 1,
                CreatedAt = now,
            },
            ct));

        var expiredClaim = await repository.TryClaimOneAsync(now.AddMinutes(1), TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(expiredClaim);
        Assert.Equal(expiredId, expiredClaim.Id);

        var deadLettered = await repository.DeadLetterExpiredProcessingAtMaxAttemptsAsync(
            now.AddMinutes(3),
            batchSize: 10,
            ct);
        Assert.Contains(deadLettered, row => row.Id == expiredId);
        Assert.All(
            deadLettered,
            row => Assert.Equal(ProviderEventInboxRepository.LeaseExpiredMaxAttemptsErrorCode, row.ErrorCode));

        Assert.False(await repository.HasPendingWorkAsync(now.AddMinutes(5), ct));
    }

    [Fact]
    public async Task Suppression_normalizes_on_insert_and_supports_physical_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var repository = new MailSuppressionRepository(db.Factory);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");

        Assert.True(await repository.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000202"),
                TenantId = tenantId,
                RecipientEmail = "User@Example.COM",
                Reason = MailSuppressionReasons.HardBounce,
                CreatedAt = now,
            },
            ct));

        Assert.True(await repository.ExistsAsync(tenantId, "user@example.com", ct));
        Assert.True(await repository.ExistsAsync(tenantId, "USER@example.com", ct));
        Assert.False(await repository.TryInsertAsync(
            new MailSuppressionInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000203"),
                TenantId = tenantId,
                RecipientEmail = "user@example.com",
                Reason = MailSuppressionReasons.Manual,
                CreatedAt = now,
            },
            ct));

        Assert.True(await repository.TryDeleteAsync(tenantId, "User@Example.COM", ct));
        Assert.False(await repository.ExistsAsync(tenantId, "user@example.com", ct));
        Assert.False(await repository.TryDeleteAsync(tenantId, "user@example.com", ct));
    }

    [Fact]
    public async Task Bounce_event_insert_is_idempotent_on_provider_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var repository = new BounceEventRepository(db.Factory);
        var insert = new BounceEventInsert
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000301"),
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000302"),
            SourceService = "orders",
            MailRequestId = Guid.Parse("00000000-0000-0000-0000-000000000303"),
            Provider = "acs",
            ProviderEventId = "eg-event-1",
            ProviderMessageId = "44444444-4444-4444-4444-444444444444",
            DeliveryStatus = "Bounced",
            StatusMessage = "sanitized-status",
            OccurredAt = DateTimeOffset.Parse("2026-07-26T00:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-07-26T00:00:01Z"),
        };

        Assert.True(await repository.TryInsertAsync(insert, ct));
        Assert.False(await repository.TryInsertAsync(
            new BounceEventInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000304"),
                TenantId = insert.TenantId,
                SourceService = insert.SourceService,
                MailRequestId = insert.MailRequestId,
                Provider = insert.Provider,
                ProviderEventId = insert.ProviderEventId,
                ProviderMessageId = insert.ProviderMessageId,
                DeliveryStatus = insert.DeliveryStatus,
                StatusMessage = insert.StatusMessage,
                OccurredAt = insert.OccurredAt,
                CreatedAt = insert.CreatedAt,
            },
            ct));
        Assert.True(await repository.ExistsAsync(insert.Id, ct));
    }

    [Fact]
    public async Task Inbox_delete_expired_terminal_purges_processed_and_dead_lettered_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var repository = new ProviderEventInboxRepository(db.Factory);
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");

        var staleProcessedId = Guid.Parse("00000000-0000-0000-0000-000000000401");
        Assert.True(await repository.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = staleProcessedId,
                Provider = "acs",
                EventId = "event-401",
                MaxAttempts = 3,
                CreatedAt = now.AddDays(-120),
            },
            ct));
        var claimed = await repository.TryClaimOneAsync(now.AddDays(-120), TimeSpan.FromMinutes(1), ct);
        Assert.NotNull(claimed);
        Assert.True(await repository.FinalizeAsync(
            claimed.Id,
            claimed.LockToken,
            now.AddDays(-120).AddSeconds(1),
            ProviderEventInboxFinalizeOutcome.Processed,
            nextAttemptAt: null,
            lastErrorCode: null,
            ct));

        Assert.True(await repository.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000402"),
                Provider = "acs",
                EventId = "event-402",
                MaxAttempts = 3,
                CreatedAt = now,
            },
            ct));

        var deleted = await repository.DeleteExpiredTerminalAsync(now.AddDays(-90), batchSize: 100, ct);
        Assert.Equal(1, deleted);
        Assert.True(await repository.HasPendingWorkAsync(now.AddMinutes(1), ct));
    }

    private static async Task<MigratedDb> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-bounce-repos", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
        return new MigratedDb(root, factory);
    }

    private sealed class MigratedDb(string root, SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public SqliteConnectionFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            await Task.Run(() => Directory.Delete(root, recursive: true));
        }
    }
}
