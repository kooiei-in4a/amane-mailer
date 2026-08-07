using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

[Collection(MailerTestCollection.Name)]
public sealed class AdminCanonicalRecipientReadTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task List_detail_and_dead_letter_reads_use_canonical_recipients_without_legacy_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var cases = new[]
        {
            new RecipientCase("single-to", MailRequestState.Queued,
                [new(MailRecipientRole.To, 0, "canonical-to@example.com", "Canonical To")]),
            new RecipientCase("multiple-to", MailRequestState.Queued,
                [
                    new(MailRecipientRole.To, 0, "to-0@example.com", "To 0"),
                    new(MailRecipientRole.To, 1, "to-1@example.com", "To 1"),
                ]),
            new RecipientCase("cc-only", MailRequestState.Queued,
                [new(MailRecipientRole.Cc, 0, "cc@example.com", "Canonical Cc")]),
            new RecipientCase("bcc-only", MailRequestState.Queued,
                [new(MailRecipientRole.Bcc, 0, "bcc-secret@example.com", "Secret Bcc")]),
            new RecipientCase("to-cc", MailRequestState.Queued,
                [
                    new(MailRecipientRole.To, 0, "mixed-to@example.com", "Mixed To"),
                    new(MailRecipientRole.Cc, 0, "mixed-cc@example.com", "Mixed Cc"),
                ]),
            new RecipientCase("to-bcc", MailRequestState.Queued,
                [
                    new(MailRecipientRole.To, 0, "to-bcc-to@example.com", "To Bcc To"),
                    new(MailRecipientRole.Bcc, 0, "to-bcc-secret@example.com", "To Bcc Secret"),
                ]),
            new RecipientCase("to-cc-bcc", MailRequestState.DeadLettered,
                [
                    new(MailRecipientRole.To, 0, "all-to@example.com", "All To"),
                    new(MailRecipientRole.Cc, 0, "all-cc@example.com", "All Cc"),
                    new(MailRecipientRole.Bcc, 0, "all-bcc-secret@example.com", "All Bcc Secret"),
                ]),
        };

        var seeded = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            seeded[item.Name] = await SeedRequestAsync(item, ct);
        }

        var repository = fixture.Factory.Services.GetRequiredService<MailRequestRepository>();
        var tenantScope = new HashSet<Guid> { MailerWebApplicationFixtureBase.TenantId };
        var list = await repository.ListForAdminAsync(
            new AdminMailRequestListQuery
            {
                Status = (int)MailRequestState.Queued,
                AllowedTenantIds = tenantScope,
                PageSize = 50,
            },
            ct);

        Assert.Equal(6, list.Items.Count);
        foreach (var item in cases.Where(item => item.Status == MailRequestState.Queued))
        {
            var row = Assert.Single(list.Items.Where(candidate => candidate.Id == seeded[item.Name]));
            Assert.Equal(string.Empty, row.RecipientEmail);
            AssertCanonicalRecipients(row.Recipients, item.Recipients);
        }

        var detailCase = cases.Single(item => item.Name == "to-cc-bcc");
        var detail = await repository.GetDetailForAdminAsync(seeded[detailCase.Name], tenantScope, ct);
        Assert.NotNull(detail);
        Assert.Equal(string.Empty, detail.RecipientEmail);
        Assert.Null(detail.RecipientDisplayName);
        AssertCanonicalRecipients(detail.Recipients, detailCase.Recipients);

        var deadLetters = await repository.ListDeadLettersForAdminAsync(
            new AdminDeadLetterListQuery
            {
                AllowedTenantIds = tenantScope,
                PageSize = 50,
            },
            ct);
        var deadLetter = Assert.Single(deadLetters.Items);
        Assert.Equal(string.Empty, deadLetter.RecipientEmail);
        AssertCanonicalRecipients(deadLetter.Recipients, detailCase.Recipients);

        var detailHtml = AdminMailRequestDetailPage.RenderHtml(
            detail,
            [],
            new MailerAdminOptions { MaskRecipients = false, MaskSubjects = false });
        Assert.Contains("all-to@example.com", detailHtml, StringComparison.Ordinal);
        Assert.Contains("all-cc@example.com", detailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("all-bcc-secret@example.com", detailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("All Bcc Secret", detailHtml, StringComparison.Ordinal);
    }

    private async Task<Guid> SeedRequestAsync(RecipientCase item, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var mailRequestId = Guid.NewGuid();
        var now = SqliteTime.ToStorageUtc(new DateTimeOffset(2026, 8, 7, 2, 0, 0, TimeSpan.Zero));
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var request = connection.CreateCommand())
        {
            request.CommandText = """
                INSERT INTO mail_requests (
                    id, tenant_id, source_service, mail_request_id, purpose,
                    payload_json, payload_hash, subject, recipient_email,
                    status, attempt_count, max_attempts, attachment_count,
                    accepted_at, created_at, updated_at, completed_at)
                VALUES (
                    @Id, @TenantId, @SourceService, @MailRequestId, @Purpose,
                    '{}', @PayloadHash, @Subject, @LegacyRecipient,
                    @Status, 0, 3, 0, @Now, @Now, @Now, @CompletedAt);
                """;
            request.Parameters.AddWithValue("@Id", requestId.ToString("D"));
            request.Parameters.AddWithValue("@TenantId", MailerWebApplicationFixtureBase.TenantId.ToString("D"));
            request.Parameters.AddWithValue("@SourceService", MailerWebApplicationFixtureBase.SourceService);
            request.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
            request.Parameters.AddWithValue("@Purpose", item.Name);
            request.Parameters.AddWithValue("@PayloadHash", new string('b', 64));
            request.Parameters.AddWithValue("@Subject", "canonical read test");
            request.Parameters.AddWithValue("@LegacyRecipient", $"legacy-{item.Name}@example.invalid");
            request.Parameters.AddWithValue("@Status", (int)item.Status);
            request.Parameters.AddWithValue(
                "@CompletedAt",
                item.Status == MailRequestState.DeadLettered ? now : DBNull.Value);
            request.Parameters.AddWithValue("@Now", now);
            await request.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var recipient in item.Recipients)
        {
            await using var canonical = connection.CreateCommand();
            canonical.CommandText = """
                INSERT INTO mail_request_recipients (
                    request_id, recipient_role, ordinal, address, address_key, display_name,
                    delivery_state, provider_message_id, provider_status_detail, created_at, updated_at)
                VALUES (
                    @RequestId, @Role, @Ordinal, @Address, @AddressKey, @DisplayName,
                    0, NULL, NULL, @Now, @Now);
                """;
            canonical.Parameters.AddWithValue("@RequestId", requestId.ToString("D"));
            canonical.Parameters.AddWithValue("@Role", (int)recipient.Role);
            canonical.Parameters.AddWithValue("@Ordinal", recipient.Ordinal);
            canonical.Parameters.AddWithValue("@Address", recipient.Address);
            canonical.Parameters.AddWithValue("@AddressKey", RecipientEmailNormalizer.Normalize(recipient.Address));
            canonical.Parameters.AddWithValue("@DisplayName", recipient.DisplayName);
            canonical.Parameters.AddWithValue("@Now", now);
            await canonical.ExecuteNonQueryAsync(cancellationToken);
        }

        return requestId;
    }

    private static void AssertCanonicalRecipients(
        IReadOnlyList<AdminRecipientSummary> actual,
        IReadOnlyList<RecipientSeed> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var recipient in expected)
        {
            var row = Assert.Single(actual.Where(candidate =>
                candidate.Role == recipient.Role && candidate.Ordinal == recipient.Ordinal));
            if (recipient.Role == MailRecipientRole.Bcc)
            {
                Assert.Null(row.Address);
                Assert.Null(row.DisplayName);
            }
            else
            {
                Assert.Equal(recipient.Address, row.Address);
                Assert.Equal(recipient.DisplayName, row.DisplayName);
            }
        }
    }

    private sealed record RecipientCase(
        string Name,
        MailRequestState Status,
        IReadOnlyList<RecipientSeed> Recipients);

    private sealed record RecipientSeed(
        MailRecipientRole Role,
        int Ordinal,
        string Address,
        string DisplayName);
}
