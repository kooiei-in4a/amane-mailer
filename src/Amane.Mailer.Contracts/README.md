# Amane.Mailer.Contracts

NuGet package containing Mailer HTTP contract DTOs, status constants, and the
delivery payload hash helper for use by consumer applications and the Mailer service.

The C# root namespace is `Mailer.Contracts`.

## Configure the NuGet source

The package is published to GitHub Packages. GitHub Packages requires
authentication with a GitHub PAT that has the `read:packages` scope.

```xml
<!-- nuget.config (place at solution root or in %AppData%\NuGet\NuGet.Config) -->
<configuration>
  <packageSources>
    <add key="github-kooiei-in4a"
         value="https://nuget.pkg.github.com/kooiei-in4a/index.json" />
  </packageSources>
</configuration>
```

Or add the source with the CLI:

```bash
dotnet nuget add source https://nuget.pkg.github.com/kooiei-in4a/index.json \
  --name github-kooiei-in4a \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT   # read:packages scope required
```

On Linux or macOS, `dotnet nuget add source` may also require
`--store-password-in-clear-text` if no credential provider is available.

## Install

```bash
dotnet add package Amane.Mailer.Contracts
```

## Key Types

| Type | Namespace | Purpose |
|---|---|---|
| `MailRequestCreateRequest` | `Mailer.Contracts.MailRequests` | POST request DTO |
| `MailRequestCreateResponse` | `Mailer.Contracts.MailRequests` | 202 response DTO |
| `MailRecipientDto` | `Mailer.Contracts.MailRequests` | Recipient in `to` array |
| `MailPayloadHasher` | `Mailer.Contracts.Security` | `payload_hash` computation helper |
| `MailRequestAcceptanceStatus` | `Mailer.Contracts.MailRequests` | Response `status` constants |
| `MailerErrorCodes` | `Mailer.Contracts.MailRequests` | Error code constants |

## Minimal Example

```csharp
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mailer.Contracts.Json;
using Mailer.Contracts.MailRequests;
using Mailer.Contracts.Security;

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
