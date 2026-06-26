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
              "tenant_id": "00000000-0000-0000-0000-000000000301",
              "source_service": "example-service",
              "mail_request_id": "018f7c2a-0000-7000-8000-000000000000",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "user@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "0000000000000000000000000000000000000000000000000000000000000000",
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
              "tenant_id": "00000000-0000-0000-0000-000000000301",
              "source_service": "example-service",
              "mail_request_id": "018f7c2a-0000-7000-8000-000000000000",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "user@example.com", "display_name": "User" }],
              "subject": "Subject",
              "text_body": "Body",
              "metadata": { "form_id": "42" },
              "payload_hash": "0000000000000000000000000000000000000000000000000000000000000000"
            }
            """;

        var request = JsonSerializer.Deserialize(
            json, MailerContractsJsonContext.Default.MailRequestCreateRequest);

        Assert.NotNull(request);
        Assert.Equal("example-service", request.SourceService);
        Assert.Equal("user@example.com", request.To[0].Email);
    }
}
