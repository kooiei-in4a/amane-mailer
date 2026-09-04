using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestAcceptStoreAttachmentCleanupTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task InsertAcceptedAsync_open_failure_after_spool_commit_deletes_committed_spool()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var connections = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        var spool = scope.ServiceProvider.GetRequiredService<AttachmentSpool>();
        var (insert, attachment) = await CreateStagedInsertAsync(spool, ct);

        var missingRoot = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-accept-open-fault",
            Guid.NewGuid().ToString("N"),
            "missing");
        connections.ConnectionCreatedForTests = connection =>
            connection.ConnectionString = $"Data Source={Path.Combine(missingRoot, "mailer.db")}";

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.InsertAcceptedAsync(insert, ct));

            Assert.False(Directory.Exists(spool.GetStagingDirectory(insert.Id)));
            Assert.False(spool.CommittedDirectoryExists(insert.Id));
            Assert.False(await RequestRowExistsAsync(insert.Id, ct));
        }
        finally
        {
            ClearConnectionHooks(connections);
            CleanupSpool(spool, insert.Id);
            _ = attachment;
        }
    }

    [Fact]
    public async Task InsertAcceptedAsync_pragma_failure_after_spool_commit_deletes_committed_spool()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var connections = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        var spool = scope.ServiceProvider.GetRequiredService<AttachmentSpool>();
        var (insert, _) = await CreateStagedInsertAsync(spool, ct);

        connections.AfterPragmaAppliedForTests = (pragma, _) =>
            throw new InvalidOperationException($"injected pragma fault after {pragma}");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.InsertAcceptedAsync(insert, ct));

            Assert.StartsWith("injected pragma fault after", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(spool.GetStagingDirectory(insert.Id)));
            Assert.False(spool.CommittedDirectoryExists(insert.Id));
            Assert.False(await RequestRowExistsAsync(insert.Id, ct));
        }
        finally
        {
            ClearConnectionHooks(connections);
            CleanupSpool(spool, insert.Id);
        }
    }

    [Fact]
    public async Task InsertAcceptedAsync_begin_failure_after_spool_commit_deletes_committed_spool()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var connections = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        var spool = scope.ServiceProvider.GetRequiredService<AttachmentSpool>();
        var (insert, _) = await CreateStagedInsertAsync(spool, ct);

        SqliteConnection? createdConnection = null;
        SqliteTransaction? heldTransaction = null;
        connections.ConnectionCreatedForTests = connection => createdConnection = connection;
        connections.AfterPragmaAppliedForTests = (pragma, _) =>
        {
            if (pragma.StartsWith("PRAGMA foreign_keys", StringComparison.Ordinal))
            {
                heldTransaction = createdConnection!.BeginTransaction();
            }

            return Task.CompletedTask;
        };

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.InsertAcceptedAsync(insert, ct));

            Assert.NotNull(heldTransaction);
            Assert.False(Directory.Exists(spool.GetStagingDirectory(insert.Id)));
            Assert.False(spool.CommittedDirectoryExists(insert.Id));
            Assert.False(await RequestRowExistsAsync(insert.Id, ct));
        }
        finally
        {
            ClearConnectionHooks(connections);
            heldTransaction?.Dispose();
            CleanupSpool(spool, insert.Id);
        }
    }

    private async Task<(AcceptedMailRequestInsert Insert, CanonicalAttachmentMetadata Attachment)> CreateStagedInsertAsync(
        AttachmentSpool spool,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var spoolKey = Guid.NewGuid();
        var attachment = new CanonicalAttachmentMetadata(
            Order: 0,
            FileName: "note.txt",
            ContentType: "text/plain",
            ByteLength: 7,
            Sha256Hex: new string('a', 64),
            SpoolKey: spoolKey);

        spool.EnsureRootDirectoriesExist();
        spool.EnsureStagingDirectory(requestId);
        await File.WriteAllTextAsync(
            spool.GetStagingFilePath(requestId, spoolKey),
            "payload",
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var insert = new AcceptedMailRequestInsert
        {
            Id = requestId,
            TenantId = MailerWebApplicationFixtureBase.TenantId,
            SourceService = MailerWebApplicationFixtureBase.SourceService,
            MailRequestId = Guid.NewGuid(),
            Purpose = "FormResponseNotification",
            PayloadJson = "{}",
            PayloadHash = new string('b', 64),
            Subject = "Attachment cleanup regression",
            TextBody = "payload",
            RecipientEmail = "recipient@example.com",
            MaxAttempts = 3,
            AcceptedAt = now,
            Attachments = [attachment],
        };

        return (insert, attachment);
    }

    private async Task<bool> RequestRowExistsAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM mail_requests WHERE id = @Id);";
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1L;
    }

    private static void ClearConnectionHooks(SqliteConnectionFactory connections)
    {
        connections.ConnectionCreatedForTests = null;
        connections.AfterPragmaAppliedForTests = null;
        connections.ConnectionDisposedForTests = null;
    }

    private static void CleanupSpool(AttachmentSpool spool, Guid requestId)
    {
        spool.TryDeleteStaging(requestId);
        spool.TryDeleteCommitted(requestId);
    }
}
