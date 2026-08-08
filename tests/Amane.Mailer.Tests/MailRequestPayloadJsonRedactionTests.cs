using System.Net;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests;

/// <summary>
/// ADR 0022 D-04/D-14 and ADR 0023 D-10: attachment content_base64 and recipient address/display
/// name PII must never be persisted to the safe mail_requests.payload_json snapshot.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class MailRequestPayloadJsonRedactionTests(MailerAdminDbOpsFixture fixture)
    : IClassFixture<MailerAdminDbOpsFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Accepted_attachment_request_never_persists_content_base64_in_payload_json()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var attachment = MailRequestTestData.CreateTextAttachment(fileName: "invoice.txt", content: "invoice body");
        var request = MailRequestTestData.CreateRequest(attachments: [attachment]);

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var storedPayloadJson = await ReadPayloadJsonAsync(request.MailRequestId, ct);
        Assert.NotNull(storedPayloadJson);

        Assert.DoesNotContain(attachment.ContentBase64, storedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("content_base64", storedPayloadJson, StringComparison.Ordinal);

        // Everything else about the declared attachment is preserved for audit purposes.
        Assert.Contains("invoice.txt", storedPayloadJson, StringComparison.Ordinal);
        Assert.Contains(attachment.ContentSha256, storedPayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"attachments\"", storedPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepted_non_attachment_request_redacts_recipient_pii_from_payload_json()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MailerWebApplicationFixtureBase.Token);

        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var storedPayloadJson = await ReadPayloadJsonAsync(request.MailRequestId, ct);
        if (storedPayloadJson is null)
            throw new InvalidOperationException("payload_json was not persisted.");

        if (request.To is null)
            throw new InvalidOperationException("test recipient was not initialized.");

        var recipient = request.To[0];
        var displayName = recipient.DisplayName;
        if (displayName is null)
            throw new InvalidOperationException("test recipient display name was not initialized.");
        Assert.DoesNotContain("attachments", storedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient.Email, storedPayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(displayName, storedPayloadJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", storedPayloadJson, StringComparison.Ordinal);
    }

    private async Task<string?> ReadPayloadJsonAsync(Guid mailRequestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM mail_requests WHERE mail_request_id = @MailRequestId;";
        command.Parameters.AddWithValue("@MailRequestId", mailRequestId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }
}
