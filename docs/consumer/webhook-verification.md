# Delivery result webhook verification

Mailer pushes signed HTTP POST requests to a tenant-configured HTTPS endpoint when a mail
request reaches a terminal delivery state:

- `delivered`
- `failed`
- `dead_lettered`
- `cancelled`

The JSON body matches `MailDeliveryEventPayload` in `docs/api/openapi.yaml` and
`src/Amane.Mailer.Contracts/MailRequests/MailDeliveryEventPayload.cs`. It never includes
recipient, subject, body, reply-to, metadata values, webhook URLs, or secrets.

## Tenant configuration

Configure an optional `webhook` object in tenant JSON:

```json
{
  "webhook": {
    "url": "https://consumer.example.com/internal/mailer/webhooks",
    "secret_env": "MAIL_WEBHOOK_SECRET",
    "allowed_host_suffixes": ["example.com"]
  }
}
```

- `url` must be HTTPS. Userinfo is forbidden.
- `secret_env` names an environment variable on the Mailer host. The secret value is never
  stored in tenant JSON or SQLite.
- `allowed_host_suffixes` is optional defense-in-depth for outbound hostnames.

## Request headers

| Header | Description |
|---|---|
| `Content-Type` | `application/json` |
| `X-Mailer-Event-Id` | Same UUID as body `event_id` |
| `X-Mailer-Timestamp` | Unix epoch seconds (UTC) when Mailer signed the request |
| `X-Mailer-Signature` | `sha256=<lowercase-hex-hmac>` |

## Signature algorithm

1. Read the raw request body bytes (`body`).
2. Parse `X-Mailer-Timestamp` as a decimal integer (`ts`).
3. Compute `payload = UTF8(ts + "." + body)`.
4. Compute `signature = HMACSHA256(secret, payload)`.
5. Compare `X-Mailer-Signature` to `sha256=` + lowercase hex of `signature` using a
   constant-time comparison.

Reject requests when:

- the timestamp skew exceeds your tolerance (recommended: 5 minutes)
- the signature does not match
- `event_id` was already processed (at-least-once delivery)

## Idempotency contract

Mailer enqueues at most one delivery-result event per
`(tenant_id, source_service, mail_request_id)`. Retries reuse the same `event_id` and body.
Consumers must treat duplicate POSTs with the same `event_id` as success.

## Example verification (pseudocode)

```text
ts = header("X-Mailer-Timestamp")
body = raw_body_bytes()
expected = "sha256=" + hex_lower(HMAC_SHA256(secret, utf8(ts + "." + body)))
assert constant_time_equals(header("X-Mailer-Signature"), expected)
assert abs(now_unix() - int(ts)) <= 300
assert not already_processed(header("X-Mailer-Event-Id"))
```

## Operational visibility

- Admin UI: `/admin/webhook-dead-letters`
- CLI: `db stats` outputs `webhook_events_pending` and `webhook_events_dead_lettered`

Webhook URLs and secrets are never written to audit logs or Admin HTML.
