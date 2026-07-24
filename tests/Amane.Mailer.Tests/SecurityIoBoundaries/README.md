# Security / I/O boundary regression suite (#355)

Cross-cutting regression coverage for security and I/O boundaries fixed in
#341–#345. Tests live in the existing `Amane.Mailer.Tests` project; this folder
groups the inventory by boundary responsibility.

| File | Child issue | Boundary under test |
|------|-------------|---------------------|
| `SecurityBoundaryTests.cs` | #341 | Environment-scoped Admin cookie policy (`Secure` / `__Host-` vs Development `ALLOW_HTTP`) |
| `HttpEncodingTests.cs` | #343 | Raw request bytes decoded as strict UTF-8; invalid sequences → `400 INVALID_REQUEST` |
| `ReadinessFailureClassificationTests.cs` | #342 | Schema mismatch vs SQLite / I/O / cancellation classification for `/readyz` |
| `SqliteConnectionOwnershipTests.cs` | #344 | Factory disposes `SqliteConnection` on open/PRAGMA failure before ownership transfer |
| `WebhookStreamingResponseTests.cs` | #345 | Outbound webhook judges status from headers (`ResponseHeadersRead`) without buffering body |

## Design rules

- Prefer real I/O seams: raw HTTP bytes, real SQLite paths, custom `HttpMessageHandler` / test servers.
- Do not replace integration boundaries with mock-only tests that hide the seam.
- Avoid long wall-clock sleeps; use TCS, fault injection, or existing sync helpers.
- Test names should identify premise, action, and expected outcome so failures point at the boundary.

Shared readiness fixtures used by classification and #330 observability tests:

- `../Fixtures/ReadyzObservabilityHarness.cs`
- `../Fixtures/ReadyzAssertionHelpers.cs`
