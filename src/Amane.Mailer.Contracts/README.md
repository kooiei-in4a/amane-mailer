# Amane.Mailer.Contracts

NuGet package containing Mailer HTTP contract DTOs, status constants, and the
delivery payload hash helper for use by consumer applications and the Mailer service.

The C# root namespace is `Amane.Mailer.Contracts`.

## NuGet source

The package is published to nuget.org. No custom package source or
package-read authentication is required when the default nuget.org source is enabled.

## Install

```bash
dotnet add package Amane.Mailer.Contracts
```

## Key Types

| Type | Namespace | Purpose |
|---|---|---|
| `MailRequestCreateRequest` | `Amane.Mailer.Contracts.MailRequests` | POST request DTO |
| `MailRequestCreateResponse` | `Amane.Mailer.Contracts.MailRequests` | 202 response DTO |
| `MailRecipientDto` | `Amane.Mailer.Contracts.MailRequests` | Recipient in `to` array |
| `MailPayloadHasher` | `Amane.Mailer.Contracts.Security` | `payload_hash` computation helper |
| `MailRequestAcceptanceStatus` | `Amane.Mailer.Contracts.MailRequests` | Response `status` constants |
| `MailerErrorCodes` | `Amane.Mailer.Contracts.MailRequests` | Error code constants |

## Minimal Example

```csharp
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;

var request = new MailRequestCreateRequest
{
    TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
    SourceService = "my-service",
    MailRequestId = Guid.NewGuid(),   // UUIDv7 recommended
    Purpose = "FormResponseNotification",
    To = [new MailRecipientDto { Email = "user@example.com" }],
    Subject = "Subject line",
    TextBody = "Plain text body",
    PayloadHash = string.Empty,  // Excluded from the hash input
};

// Compute payload_hash before sending
request = request with
{
    PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
};

var requestJson = JsonSerializer.Serialize(
    request,
    MailerContractsJsonContext.Default.MailRequestCreateRequest);

using var httpClient = new HttpClient { BaseAddress = new Uri("http://mailer:8080") };
using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/mail-requests")
{
    Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
};
message.Headers.Authorization = new AuthenticationHeaderValue(
    "Bearer",
    "MAIL_SERVICE_TOKEN_VALUE");

using var httpResponse = await httpClient.SendAsync(message);
httpResponse.EnsureSuccessStatusCode();

await using var responseStream = await httpResponse.Content.ReadAsStreamAsync();
var accepted = await JsonSerializer.DeserializeAsync(
    responseStream,
    MailerContractsJsonContext.Default.MailRequestCreateResponse);

if (accepted is null)
{
    throw new InvalidOperationException("Mailer returned an empty response.");
}

if (accepted.Status == MailRequestAcceptanceStatus.AlreadyAccepted)
{
    // The same mail_request_id and payload_hash were already accepted.
}
```

The bundled JSON context omits null optional properties. If you compute the
hash from raw JSON instead, pass the exact JSON string that will be sent.
