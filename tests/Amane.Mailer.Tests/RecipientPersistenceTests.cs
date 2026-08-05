using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class RecipientPersistenceTests
{
    [Fact]
    public async Task Accept_store_persists_each_canonical_recipient_shape()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);

        var shapes = new[]
        {
            new[] { Recipient(MailRecipientRole.To, 0, "to@example.com", "To") },
            new[]
            {
                Recipient(MailRecipientRole.To, 0, "to-one@example.com", null),
                Recipient(MailRecipientRole.To, 1, "to-two@example.com", "Second To"),
            },
            new[] { Recipient(MailRecipientRole.Cc, 0, "cc@example.com", null) },
            new[] { Recipient(MailRecipientRole.Bcc, 0, "bcc@example.com", "Private BCC") },
            new[]
            {
                Recipient(MailRecipientRole.To, 0, "to-cc@example.com", null),
                Recipient(MailRecipientRole.Cc, 0, "cc-1@example.com", "CC"),
            },
            new[]
            {
                Recipient(MailRecipientRole.To, 0, "to-bcc@example.com", null),
                Recipient(MailRecipientRole.Bcc, 0, "bcc-1@example.com", "BCC"),
            },
            new[]
            {
                Recipient(MailRecipientRole.To, 0, "to-all@example.com", null),
                Recipient(MailRecipientRole.Cc, 0, "cc-all@example.com", null),
                Recipient(MailRecipientRole.Bcc, 0, "bcc-all@example.com", null),
            },
        };

        foreach (var recipients in shapes)
        {
            var requestId = Guid.NewGuid();
            await repository.InsertAcceptedAsync(
                CreateInsert(requestId, recipients),
                cancellationToken);

            var persisted = await ReadRecipientsAsync(
                database.Factory,
                requestId,
                cancellationToken);

            Assert.Equal(recipients.Length, persisted.Count);
            Assert.Equal(
                recipients.Select(recipient =>
                    (Role: (int)recipient.Role,
                     recipient.Ordinal,
                     recipient.Address,
                     recipient.AddressKey,
                     recipient.DisplayName,
                     State: (int)MailRecipientDeliveryState.NotSent)),
                persisted.Select(row =>
                    (row.Role,
                     row.Ordinal,
                     row.Address,
                     row.AddressKey,
                     row.DisplayName,
                     row.State)));

            var legacyShadow = await ReadLegacyShadowAsync(
                database.Factory,
                requestId,
                cancellationToken);
            if (recipients.All(recipient => recipient.Role == MailRecipientRole.Bcc))
            {
                Assert.Equal(
                    new LegacyShadow(
                        MailRequestLegacyShadow.BccOnlyRecipientEmail,
                        MailRequestLegacyShadow.BccOnlyRecipientDisplayName),
                    legacyShadow);
                Assert.DoesNotContain(
                    recipients.Select(recipient => recipient.Address),
                    address => string.Equals(legacyShadow.Email, address, StringComparison.Ordinal));
                Assert.DoesNotContain(
                    recipients.Where(recipient => recipient.DisplayName is not null)
                        .Select(recipient => recipient.DisplayName!),
                    displayName => string.Equals(legacyShadow.DisplayName, displayName, StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(
                    new LegacyShadow(recipients[0].Address, recipients[0].DisplayName),
                    legacyShadow);
            }
        }
    }

    [Fact]
    public async Task Accept_store_recalculates_address_key_from_address_before_insert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var requestId = Guid.NewGuid();
        var recipient = new CanonicalMailRecipient
        {
            Role = MailRecipientRole.To,
            Ordinal = 0,
            Address = "  MixedCase@example.com  ",
            AddressKey = "forged-key-must-not-be-stored",
            DisplayName = null,
        };

        await repository.InsertAcceptedAsync(
            CreateInsert(requestId, [recipient]),
            cancellationToken);

        var persisted = await ReadRecipientsAsync(database.Factory, requestId, cancellationToken);
        var row = Assert.Single(persisted);
        Assert.Equal(RecipientEmailNormalizer.Normalize(recipient.Address), row.AddressKey);
    }

    [Fact]
    public async Task Accept_store_rolls_back_request_and_recipients_when_duplicate_address_key_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(cancellationToken);
        var repository = MailRequestRepository.CreateStandalone(database.Factory);
        var requestId = Guid.NewGuid();
        var duplicateRecipients = new[]
        {
            Recipient(MailRecipientRole.To, 0, "Duplicate@example.com", null),
            Recipient(MailRecipientRole.Cc, 0, "duplicate@example.com", null),
        };

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.InsertAcceptedAsync(
                CreateInsert(requestId, duplicateRecipients),
                cancellationToken));

        Assert.Equal(
            0L,
            await ReadScalarAsync(
                database.Factory,
                "SELECT COUNT(*) FROM mail_requests WHERE id = @Id;",
                cancellationToken,
                ("@Id", requestId.ToString("D"))));
        Assert.Equal(
            0L,
            await ReadScalarAsync(
                database.Factory,
                "SELECT COUNT(*) FROM mail_request_recipients WHERE request_id = @Id;",
                cancellationToken,
                ("@Id", requestId.ToString("D"))));
    }

    private static CanonicalMailRecipient Recipient(
        MailRecipientRole role,
        int ordinal,
        string address,
        string? displayName) =>
        new()
        {
            Role = role,
            Ordinal = ordinal,
            Address = address,
            AddressKey = RecipientEmailNormalizer.Normalize(address),
            DisplayName = displayName,
        };

    private static AcceptedMailRequestInsert CreateInsert(
        Guid requestId,
        IReadOnlyList<CanonicalMailRecipient> recipients) =>
        new()
        {
            Id = requestId,
            TenantId = Guid.NewGuid(),
            SourceService = "recipient-persistence-test",
            MailRequestId = Guid.NewGuid(),
            Purpose = "test",
            PayloadJson = "{}",
            PayloadHash = new string('a', 64),
            Subject = "subject",
            RecipientEmail = recipients[0].Address,
            RecipientDisplayName = recipients[0].DisplayName,
            MaxAttempts = 3,
            AcceptedAt = DateTimeOffset.UtcNow,
            Recipients = recipients,
        };

    private static async Task<IReadOnlyList<PersistedRecipient>> ReadRecipientsAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var rows = new List<PersistedRecipient>();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_role, ordinal, address, address_key, display_name, delivery_state
            FROM mail_request_recipients
            WHERE request_id = @RequestId
            ORDER BY recipient_role, ordinal;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PersistedRecipient(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5)));
        }

        return rows;
    }

    private static async Task<LegacyShadow> ReadLegacyShadowAsync(
        SqliteConnectionFactory factory,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_email, recipient_display_name
            FROM mail_requests
            WHERE id = @RequestId;
            """;
        command.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new LegacyShadow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<long> ReadScalarAsync(
        SqliteConnectionFactory factory,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PersistedRecipient(
        int Role,
        int Ordinal,
        string Address,
        string AddressKey,
        string? DisplayName,
        int State);

    private sealed record LegacyShadow(string Email, string? DisplayName);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(string root, SqliteConnectionFactory factory)
        {
            Root = root;
            Factory = factory;
        }

        public string Root { get; }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-recipient-persistence",
                Guid.NewGuid().ToString("N"));
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
            return new TestDatabase(root, factory);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
