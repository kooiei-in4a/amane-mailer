using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Tests;

public sealed class MailDeliveryEventPayloadStrictnessTests
{
    [Fact]
    public void MailDeliveryEventPayload_rejects_unknown_property()
    {
        const string json = """
            {
              "event_id": "018f7c2a-0000-7000-8000-000000000001",
              "event_type": "delivered",
              "occurred_at": "2026-01-01T00:00:00Z",
              "tenant_id": "00000000-0000-0000-0000-000000000101",
              "source_service": "example-service",
              "mail_request_id": "018f7c2a-0000-7000-8000-000000000002",
              "status": "delivered",
              "attempt_count": 1,
              "unexpected": true
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, MailerContractsJsonContext.Default.MailDeliveryEventPayload));
    }
}
