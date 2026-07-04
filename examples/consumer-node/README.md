# Node.js Consumer sample

A minimal, runnable Node.js Consumer that POSTs one mail request to a local Mailer, using
the existing `examples/payload-hash/javascript` helper to compute `payload_hash`.

This is a full example, not an npm package or SDK. See [Out of scope](#out-of-scope)
below.

## Prerequisites

- Node.js 18 or newer.
- A local Mailer running with Mailpit, per the
  [Run With Mailpit](../../README.en.md#run-with-mailpit) section of the repo README:

  ```bash
  docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
  ```

## Run

```bash
node examples/consumer-node/send-mail.mjs
```

Expected output ends with:

```text
HTTP 202 Accepted - status: accepted
```

Check Mailpit at `http://127.0.0.1:8025/` to see the delivered mail.

### `already_accepted`

Re-run with the same `mail_request_id` printed by the previous run to see the idempotent
resend path:

```bash
node examples/consumer-node/send-mail.mjs --request-id <the-printed-guid>
```

```text
HTTP 202 Accepted - status: already_accepted
```

### `IDEMPOTENCY_CONFLICT` (409)

Reuse the same `mail_request_id` but change a delivery field (`--mutate` edits the subject),
so the recomputed `payload_hash` differs from the one stored for that `mail_request_id`:

```bash
node examples/consumer-node/send-mail.mjs --request-id <the-printed-guid> --mutate
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
| `MAILER_TIMEOUT_SECONDS` | `10` |

## How `payload_hash` is computed

The sample builds the mail request object, leaves `payload_hash` as an empty placeholder,
then calls `computeDeliveryPayloadSha256Hex(request)` from
[`examples/payload-hash/javascript/mail_payload_hash.mjs`](../payload-hash/javascript/mail_payload_hash.mjs)
before sending. The helper excludes routing fields such as `tenant_id`,
`mail_request_id`, and `payload_hash` from the hash input.

See [examples/payload-hash/README.md](../payload-hash/README.md) for the algorithm and
troubleshooting notes.

## Out of scope

This sample intentionally does not include an npm package, TypeScript SDK,
production retry policy, framework integration, or production credential handling. See
[issue #154](https://github.com/kooiei-in4a/amane-mailer/issues/154) for scope notes.
