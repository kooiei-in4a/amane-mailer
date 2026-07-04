# .NET Consumer sample

A minimal, runnable .NET Consumer that POSTs one mail request to a local Mailer, using
`Amane.Mailer.Contracts` for the request/response DTOs and `payload_hash` computation.

This is a full example, not an SDK. See [Out of scope](#out-of-scope) below.

## Prerequisites

- .NET SDK (see [`global.json`](../../global.json)).
- A local Mailer running with Mailpit, per the
  [Mailpit で起動する](../../README.md#mailpit-で起動する) section of the repo README:

  ```bash
  docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
  ```

## Run

```bash
dotnet run --project examples/consumer-dotnet/ConsumerSample.csproj
```

Expected output ends with:

```text
HTTP 202 Accepted — status: accepted
```

Check Mailpit at `http://127.0.0.1:8025/` to see the delivered mail.

### `already_accepted`

Re-run with the same `mail_request_id` printed by the previous run to see the idempotent
resend path:

```bash
dotnet run --project examples/consumer-dotnet/ConsumerSample.csproj -- --request-id <the-printed-guid>
```

```text
HTTP 202 Accepted — status: already_accepted
```

### `IDEMPOTENCY_CONFLICT` (409)

Reuse the same `mail_request_id` but change a delivery field (`--mutate` edits the subject),
so the recomputed `payload_hash` differs from the one stored for that `mail_request_id`:

```bash
dotnet run --project examples/consumer-dotnet/ConsumerSample.csproj -- --request-id <the-printed-guid> --mutate
```

```text
HTTP 409 Conflict: {"code":"IDEMPOTENCY_CONFLICT", ...}
```

See [docs/api/openapi.yaml](../../docs/api/openapi.yaml) for the full error schema, and
[examples/payload-hash/README.md](../payload-hash/README.md#idempotency_conflict-409-vs-invalid_payload_hash-422)
for the `IDEMPOTENCY_CONFLICT` vs `INVALID_PAYLOAD_HASH` distinction.

## Configuration

All settings default to the local compose values and can be overridden with environment
variables:

| Variable | Default |
|---|---|
| `MAILER_BASE_URL` | `http://127.0.0.1:5280/` |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` |
| `MAILER_TENANT_ID` | `00000000-0000-0000-0000-000000000101` |
| `MAILER_SOURCE_SERVICE` | `example-service` |
| `MAILER_RECIPIENT_EMAIL` | `admin@example.com` |

## How `payload_hash` is computed

The sample builds a `MailRequestCreateRequest` and calls
`MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request)` from
[`Amane.Mailer.Contracts`](../../src/Amane.Mailer.Contracts/Security/MailPayloadHasher.cs)
before sending. See [examples/payload-hash/README.md](../payload-hash/README.md) for the
underlying algorithm and troubleshooting non-.NET implementations.

## Referencing Contracts

This sample uses a `ProjectReference` to the local `Amane.Mailer.Contracts` source so it
always builds against the current repo state. A real Consumer application would instead add
a `PackageReference` to the published NuGet package:

```xml
<PackageReference Include="Amane.Mailer.Contracts" Version="x.y.z" />
```

## Out of scope

This sample intentionally does not include a NuGet SDK wrapper, a retry policy library, DI
integration, an ASP.NET template, or production credential handling. See
[issue #152](https://github.com/kooiei-in4a/amane-mailer/issues/152) for scope notes.
