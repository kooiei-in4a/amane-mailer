using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Tests;

public sealed class MailRequestDtoStrictnessTests
{
    [Fact]
    public void MailRequestCreateRequest_rejects_unknown_property()
    {
        const string json = """
            {
              "mail_request_id": "018f7c2a-0000-7000-8000-000000000000",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "user@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "unexpected": "value"
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, MailerContractsJsonContext.Default.MailRequestCreateRequest));
    }

    [Fact]
    public void MailRecipientDto_rejects_unknown_property()
    {
        const string json = """
            { "email": "user@example.com", "role": "admin" }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, MailerContractsJsonContext.Default.MailRecipientDto));
    }

    [Fact]
    public void MailRequestCreateRequest_accepts_known_properties()
    {
        const string json = """
            {
              "mail_request_id": "018f7c2a-0000-7000-8000-000000000000",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "user@example.com", "display_name": "User" }],
              "subject": "Subject",
              "text_body": "Body",
              "metadata": { "form_id": "42" }
            }
            """;

        var request = JsonSerializer.Deserialize(
            json, MailerContractsJsonContext.Default.MailRequestCreateRequest);

        Assert.NotNull(request);
        Assert.Equal(Guid.Parse("018f7c2a-0000-7000-8000-000000000000"), request.MailRequestId);
        Assert.Equal("user@example.com", request.To![0].Email);
    }
}
