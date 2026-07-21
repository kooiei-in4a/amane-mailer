# @amane/mailer (repo-internal)

TypeScript Consumer SDK for Amane Mailer. Ships as an in-repo ESM package (`private: true`); npm publish is out of scope for Phase 1.

## Requirements

- Node.js 20+

## Tests

```bash
npm test
```

Runs payload_hash vector cross-checks and client unit tests with a mock HTTP server.

## Local Mailer integration

With local Mailer running (`http://127.0.0.1:5280`):

```bash
node scripts/send-local.mjs
```

Environment variables:

| Variable | Default |
|---|---|
| `MAILER_BASE_URL` | `http://127.0.0.1:5280` |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` |
| `MAILER_TENANT_ID` | `00000000-0000-0000-0000-000000000101` |

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
    // 422 validation / payload_hash errors
  } else if (error instanceof MailerRetryableError) {
    // 503 or retryable=true — sendMail retries automatically by default
  }
}
```

## Retries

`sendMail()` retries retryable errors (503 / `retryable: true`) with exponential backoff. Defaults: 3 retries, 200 ms base delay. Override with `{ maxRetries, baseDelayMs }`.

Idempotent resend: POST the same `mail_request_id` and payload again; Mailer returns `already_accepted` without creating a duplicate queue entry.
