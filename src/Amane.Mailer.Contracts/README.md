# Amane.Mailer.Contracts

NuGet package containing Mailer HTTP contract DTOs, status constants, and the
delivery payload hash helper for use by consumer applications and the Mailer service.

The C# root namespace is `Amane.Mailer.Contracts`.

## Target Framework

This package targets **.NET 8** (`net8.0`) on purpose. The Mailer runtime service
targets a newer framework (currently `net10.0`), but the Contracts package stays on
a broader consumer-compatible TFM so downstream applications on .NET 8 and later can
reference it without upgrading their own target framework.

Package version numbers stay aligned with the Mailer service release (see the
Versioning Policy section in `docs/service-spec.md`), but the target frameworks do
not have to match. A newer runtime does not imply the Contracts package should move
to the same TFM.

## HTTP Contract Source of Truth

This package is the code-level source of truth for Mailer HTTP request/response
DTOs, error code constants, acceptance status constants, delivery status
constants, JSON serialization context, and payload hash helper. The Mailer
runtime references this package directly, and consumer applications should use
the published NuGet package.

`docs/api/openapi.yaml` is the Consumer-facing HTTP reference / public schema.
It is kept synchronized with this package and the runtime implementation, but it
is not the source of truth. CI validates OpenAPI structure with
`scripts/validate-openapi.mjs` and runs drift assertions with
`scripts/check-contract-drift.mjs`.

When changing the contract, review drift across this package, runtime behavior,
OpenAPI, and tests for DTO JSON property names, required / nullable fields,
`MailerErrorCodes`, `MailRequestAcceptanceStatus`, `MailRequestStatus`, payload
hash fields, and JSON unknown / duplicate property behavior. The drift check
derives DTO / constant expectations from Contracts, compares them to OpenAPI,
and verifies the runtime/test coverage hooks for strict JSON and payload hashing.
If the contract intentionally changes, update the Contracts type/constant first,
then update `docs/api/openapi.yaml`, runtime behavior, examples, and related
tests in the same change. There is no separate generated snapshot to refresh
today; the drift check derives expected DTO / constant shape from source.
Recompute any affected OpenAPI payload hash example and update
`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json`
when canonicalization fixtures change. Validate with:

```bash
node scripts/validate-openapi.mjs docs/api/openapi.yaml
node scripts/check-contract-drift.mjs
```

Service release versions, Docker image tags, NuGet package versions, and
OpenAPI `info.version` are all kept in sync under the same `X.Y.Z`.
During the 0.x series, backward compatibility is not guaranteed; breaking
changes are documented in CHANGELOG release notes. See the Versioning Policy
section in `docs/service-spec.md` for full details.

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
| `MailRequestStatus` | `Amane.Mailer.Contracts.MailRequests` | Worker delivery status constants |
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
