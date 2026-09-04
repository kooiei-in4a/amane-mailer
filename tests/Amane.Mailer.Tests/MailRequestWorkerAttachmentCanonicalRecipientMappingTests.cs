using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// End-to-end Worker coverage for canonical recipient provider mapping on the attachment
/// dispatch path (ADR 0022 D-08, ADR 0023 D-01/D-03/D-10, Issue #546 review finding F2): before
/// this fix, <c>SendAttachmentRequestAndFinalizeAsync</c> built the suppression check, envelope
/// estimate, and provider job from <c>row.RecipientEmail</c>/<c>row.RecipientDisplayName</c> (the
/// legacy single-To shadow) instead of the canonical <c>mail_request_recipients</c> rows. Requests
/// are seeded directly via <see cref="MailRequestRepository"/> so the provider-focused mapping
/// tests are independent of HTTP authentication and acceptance setup, mirroring
/// <see cref="MailRequestWorkerCanonicalRecipientMappingTests"/> for plain requests.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestWorkerAttachmentCanonicalRecipientMappingTests(MailerWorkerFixture fixture)
    : IClassFixture<MailerWorkerFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        fixture.DeliveryProvider.Reset();
        await fixture.ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Worker_delivers_attachment_request_with_correct_to_cc_bcc_role_assignment()
    {
        var ct = TestContext.Current.CancellationToken;
        var mailRequestId = await SeedAndDispatchAsync(
            ct,
            [
                Recipient(MailRecipientRole.To, 0, "to@example.com", null),
                Recipient(MailRecipientRole.Cc, 0, "cc@example.com", null),
                Recipient(MailRecipientRole.Bcc, 0, "bcc-secret@example.com", null),
            ]);

        var stored = await WaitUntilStatusAsync(mailRequestId, MailRequestState.Delivered, ct);
        Assert.Equal(MailRequestState.Delivered, stored.Status);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal("to@example.com", sent.To);
        Assert.Equal(["cc@example.com"], sent.Cc);
        Assert.Equal(["bcc-secret@example.com"], sent.Bcc);
    }

    [Fact]
    public async Task Worker_delivers_bcc_only_attachment_request_using_canonical_address_not_legacy_shadow_sentinel()
    {
        var ct = TestContext.Current.CancellationToken;
        const string realBccAddress = "bcc-only-real@example.com";
        var mailRequestId = await SeedAndDispatchAsync(
            ct,
            [Recipient(MailRecipientRole.Bcc, 0, realBccAddress, null)]);

        var stored = await WaitUntilStatusAsync(mailRequestId, MailRequestState.Delivered, ct);

        var sent = Assert.Single(fixture.DeliveryProvider.Sent);
        Assert.Equal(string.Empty, sent.To);
        Assert.Empty(sent.Cc ?? []);
        Assert.Equal([realBccAddress], sent.Bcc);

        // Prove the legacy shadow really is the redacted sentinel and differs from what was
        // actually dispatched -- confirming dispatch used the canonical table, not the shadow.
        var legacyShadow = await ReadLegacyShadowAsync(stored.Id, ct);
        Assert.Equal(MailRequestLegacyShadow.BccOnlyRecipientEmail, legacyShadow);
        Assert.NotEqual(realBccAddress, legacyShadow);
    }

    [Fact]
    public async Task Worker_suppresses_attachment_recipient_after_terminal_commit_and_cleans_committed_spool()
    {
        var ct = TestContext.Current.CancellationToken;
        const string suppressedBcc = "bcc-suppressed@example.com";
        const string otherBcc = "bcc-other@example.com";

        var metrics = fixture.Factory.Services.GetRequiredService<Amane.Mailer.Operations.MailerRuntimeMetrics>();
        metrics.ClearForTests();
        var mailRequestId = await SeedAndDispatchAsync(
            ct,
            [
                Recipient(MailRecipientRole.Bcc, 0, suppressedBcc, null),
                Recipient(MailRecipientRole.Bcc, 1, otherBcc, null),
            ],
            suppressedAddress: suppressedBcc);

        var stored = await WaitUntilStatusAsync(mailRequestId, MailRequestState.Failed, ct);
        Assert.Equal(MailRequestState.Failed, stored.Status);
        Assert.Empty(fixture.DeliveryProvider.Sent);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var recipientRows = await repository.ListRecipientsAsync(stored.Id, ct);
        Assert.Equal(MailRecipientDeliveryState.Suppressed, recipientRows.Single(row => row.Ordinal == 0).DeliveryState);
        Assert.Equal(MailRecipientDeliveryState.NotSent, recipientRows.Single(row => row.Ordinal == 1).DeliveryState);
        Assert.Null(await repository.FindAttachmentSubmissionAsync(stored.Id, ct));

        var spool = scope.ServiceProvider.GetRequiredService<AttachmentSpool>();
        await ConditionWait.UntilAsync(
            _ => Task.FromResult(
                !spool.CommittedDirectoryExists(stored.Id)
                && metrics.CaptureSnapshot().AttachmentSpoolCleanups.Count(
                    entry => entry.Kind == "committed_prompt" && entry.Count == 1) == 1),
            ConditionWait.DefaultTimeout,
            ct);

        Assert.False(spool.CommittedDirectoryExists(stored.Id));
        var cleanup = Assert.Single(
            metrics.CaptureSnapshot().AttachmentSpoolCleanups,
            entry => entry.Kind == "committed_prompt");
        Assert.Equal(1, cleanup.Count);
        Assert.Equal(
            0L,
            await ReadScalarAsync(
                fixture.ConnectionString,
                "SELECT COUNT(*) FROM mail_attachment_submissions WHERE request_id = @Id;",
                ct,
                ("@Id", stored.Id.ToString("D"))));

        var legacyShadow = await ReadLegacyShadowAsync(stored.Id, ct);
        Assert.Equal(MailRequestLegacyShadow.BccOnlyRecipientEmail, legacyShadow);
        Assert.NotEqual(suppressedBcc, legacyShadow);
    }

    private async Task<Guid> SeedAndDispatchAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<CanonicalMailRecipient> recipients,
        string? suppressedAddress = null)
    {
        var mailRequestId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var spoolKey = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("hello canonical attachment");
        var sha256Hex = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var spool = scope.ServiceProvider.GetRequiredService<AttachmentSpool>();
            spool.EnsureStagingDirectory(requestId);
            await File.WriteAllBytesAsync(spool.GetStagingFilePath(requestId, spoolKey), content, cancellationToken);

            if (suppressedAddress is not null)
            {
                var suppressions = scope.ServiceProvider.GetRequiredService<MailSuppressionRepository>();
                Assert.True(await suppressions.TryInsertAsync(
                    new MailSuppressionInsert
                    {
                        Id = Guid.NewGuid(),
                        TenantId = V2PersistenceCompatibility.SuppressionScopeId,
                        RecipientEmail = suppressedAddress,
                        Reason = MailSuppressionReasons.HardBounce,
                        CreatedAt = now,
                    },
                    cancellationToken));
            }

            var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
            await repository.InsertAcceptedAsync(
                new AcceptedMailRequestInsert
                {
                    Id = requestId,
                    TenantId = MailerWebApplicationFixtureBase.TenantId,
                    SourceService = MailerWebApplicationFixtureBase.SourceService,
                    MailRequestId = mailRequestId,
                    Purpose = "FormResponseNotification",
                    PayloadJson = JsonSerializer.Serialize(new { }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    PayloadHash = new string('a', 64),
                    Subject = "Attachment canonical recipient mapping test",
                    HtmlBody = "<p>Hello</p>",
                    TextBody = "Hello",
                    RecipientEmail = recipients[0].Address,
                    RecipientDisplayName = recipients[0].DisplayName,
                    MaxAttempts = 3,
                    AcceptedAt = now,
                    Recipients = recipients,
                    Attachments =
                    [
                        new CanonicalAttachmentMetadata(
                            Order: 0,
                            FileName: "note.txt",
                            ContentType: "text/plain",
                            ByteLength: content.Length,
                            Sha256Hex: sha256Hex,
                            SpoolKey: spoolKey),
                    ],
                },
                cancellationToken);
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<Amane.Mailer.Queue.IMailRequestQueue>();
            queue.TrySignalWorkAvailable();
        }

        return mailRequestId;
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

    private async Task<MailRequestDispatchState> WaitUntilStatusAsync(
        Guid mailRequestId,
        MailRequestState status,
        CancellationToken cancellationToken)
    {
        MailRequestDispatchState? lastStored = null;
        try
        {
            return await ConditionWait.UntilAsync<MailRequestDispatchState>(
                async ct =>
                {
                    lastStored = await FindDispatchStateAsync(mailRequestId, ct);
                    return lastStored;
                },
                stored => stored.Status == status,
                ConditionWait.DefaultTimeout,
                cancellationToken,
                wake: fixture.DeliveryProvider.Activity);
        }
        catch (TimeoutException)
        {
            var lastState = lastStored is null ? "not found" : lastStored.Status.ToString();
            throw new TimeoutException(
                $"Mail request did not reach status '{status}'. Last state: {lastState}.");
        }
    }

    private async Task<MailRequestDispatchState?> FindDispatchStateAsync(
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        return await repository.FindDispatchStateByMailRequestIdAsync(mailRequestId, cancellationToken);
    }

    private async Task<string> ReadLegacyShadowAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT recipient_email FROM mail_requests WHERE id = @Id;";
        command.Parameters.AddWithValue("@Id", requestId.ToString("D"));
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ReadScalarAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
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
}
