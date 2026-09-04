# @amane/mailer (repo-internal)

TypeScript Consumer SDK for Amane Mailer. Ships as an in-repo ESM package (`private: true`); npm publish is out of scope for Phase 1.

## Requirements

- Node.js 20+

## Tests

```bash
npm test
```

Runs client unit tests with a mock HTTP server.

## Multiple recipients

`to()`, `cc()`, and `bcc()` set a role with one recipient. Use `addTo()`, `addCc()`,
and `addBcc()` to append recipients while preserving role order. Each role accepts at most
10 recipients and all roles accept at most 20 recipients combined.

## Local Mailer integration

With local Mailer running (`http://127.0.0.1:5280`):

```bash
node scripts/send-local.mjs
```

Environment variables:

| Variable | Default |
|---|---|
| `MAILER_BASE_URL` | `http://127.0.0.1:5280` |
| `MAILER_API_KEY` | required managed API key for one Sender |

## mail_request_id generation

`generateMailRequestId()` prefers **UUIDv7** (time-ordered, OpenAPI recommendation). When the timestamp is outside the 48-bit range, it falls back to **UUIDv4** via `crypto.randomUUID()`.

## Error handling

```javascript
import {
  MailerClient,
  MailerIdempotencyConflictError,
  MailerRetryableError,
  MailerValidationError,
} from '@amane/mailer';

try {
  await client.sendMail(request);
} catch (error) {
  if (error instanceof MailerIdempotencyConflictError) {
    // 409 IDEMPOTENCY_CONFLICT
  } else if (error instanceof MailerValidationError) {
    // 422 request validation errors
  } else if (error instanceof MailerRetryableError) {
    // 503 or retryable=true — sendMail retries automatically by default
  }
}
```

## Retries

`sendMail()` retries retryable HTTP errors (503 / `retryable: true`) and transport failures (connection refused, DNS failure, timeout) with exponential backoff. Defaults: 3 retries, 200 ms base delay. Override with `{ maxRetries, baseDelayMs }`.

Idempotent resend: POST the same `mail_request_id` and payload again; Mailer returns `already_accepted` without creating a duplicate queue entry.

The managed API key selects the Sender. The SDK does not send `tenant_id`,
`source_service`, `From`, provider, or `payload_hash`.
