# Delivery result webhook verification

Mailer pushes signed HTTP POST requests to a tenant-configured HTTPS endpoint when a mail
request reaches a terminal delivery state:

- `delivered`
- `failed`
- `dead_lettered`
- `cancelled`
- `delivery_unknown`

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
`(tenant_id, source_service, mail_request_id)` **while the corresponding mail request row
exists** (**first-wins**). The first terminal state that reaches the outbox
(`failed`, `dead_lettered`, `cancelled`, `delivery_unknown`, or `delivered`) is the only event for that
idempotency key. Later terminal transitions on the same row do **not** insert or replace
the event. Webhook HTTP retries reuse the same `event_id` and body. Consumers must treat
duplicate POSTs with the same `event_id` as success. A webhook HTTP retry is a retry of
the same event delivery; it is not a request to resend the mail through the provider.

### Admin manual retry

Admin manual retry is not an unconditional `Failed` / `DeadLettered` → `Queued` transition.
The current runtime permits it only for an attachment-free request with no plain submission
evidence; requests with attachment metadata or a `mail_plain_submissions` evidence row are
rejected. `delivery_unknown` is never manually retryable, and a `Failed` request with
submission evidence must not be described as always retryable. If a permitted retry later
reaches a different terminal state (for example `delivered`), Mailer does **not** enqueue a
second delivery-result webhook. `GET /internal/mail-requests/{mail_request_id}` can therefore
show `delivered` while the Consumer still holds only the earlier webhook (for example `failed`).
Status GET is authoritative for the current mail-request state; the webhook is a one-shot
first-terminal notification, not a live mirror of status.

Per-delivery-cycle webhook re-notification (latest terminal always pushed) remains
**out of scope**. [ADR 0017](../adr/0017-webhook-first-wins-delivery-cycle.md) keeps
first-wins and records re-evaluation triggers (Consumer demand, #307 bounce identity,
cancelled-resume, or an explicit breaking-contract schedule).

When request retention purges a terminal `mail_requests` row, Mailer deletes the matching
`delivery_events` row in the same transaction. After that purge, a Consumer may reuse the same
`mail_request_id` idempotency key for a new request; Mailer will enqueue a new delivery-result
event with a new `event_id`. Consumer deduplication by `event_id` still applies across
webhook retries for the same logical delivery, but not across separate mail-request
generations after retention.

A periodic reconciliation sweep also scans terminal `mail_requests` that are missing a
corresponding `delivery_events` row and enqueues them, covering crash/retry gaps between
mail finalize and webhook enqueue. Reconciliation never overwrites an existing event
(first-wins); it only inserts when the outbox row is absent.

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
- Metrics: `mail_webhook_finalize_skipped_total` counts webhook `FinalizeAsync` failures
  caused by strict lease fencing (expired `lock_expires_at` or superseded lock token).
  This covers normal delivery outcomes and terminal failure paths such as missing
  webhook configuration/secret or invalid stored payload. It does **not** change the
  at-least-once POST contract: after a skip, Mailer may reclaim and re-POST the same
  `event_id`, so consumers must keep deduplicating by `event_id`. When the counter
  rises, inspect structured Warning logs (`EventId`, `TenantId`, `MailRequestId`,
  `AttemptNumber`, `FinalizeOutcome`, `FinalizeSkipReason`) and the webhook backlog
  gauges. See [metrics-and-alerts.en.md](../ops/metrics-and-alerts.en.md).

Webhook URLs and secrets are never written to audit logs, Admin HTML, metrics labels,
or finalize-skip Warning logs. Payload bodies and recipient PII are also excluded.
