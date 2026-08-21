# @amane/mailer (repo-internal)

TypeScript Consumer SDK for Amane Mailer. Ships as an in-repo ESM package (`private: true`); npm publish is out of scope for Phase 1.

## Requirements

- Node.js 20+

## Tests

```bash
npm test
```

Runs payload_hash vector cross-checks and client unit tests with a mock HTTP server.

## Multiple recipients

`to()`, `cc()`, and `bcc()` set a role with one recipient. Use `addTo()`, `addCc()`,
and `addBcc()` to append recipients while preserving role order. Each role accepts at most
10 recipients and all roles accept at most 20 recipients combined. Omit `to()` or call
`to(null)` for a Cc-only / Bcc-only request; the built request still needs at least one
recipient across all roles.

```javascript
import { MailRequestBuilder } from '@amane/mailer';

const request = MailRequestBuilder.create()
  .tenantId('00000000-0000-0000-0000-000000000101')
  .sourceService('example-service')
  .generateMailRequestId()
  .purpose('FormResponseNotification')
  .to({ email: 'admin@example.com' })
  .cc({ email: 'team@example.com' })
  .bcc({ email: 'audit@example.com' })
  .subject('Invoice attached')
  .textBody('Please find the invoice attached.')
  .attachments([{
    file_name: 'hello.txt',
    content_type: 'text/plain',
    content_base64: 'SGVsbG8=',
    content_sha256: '185f8db32271fe25f561a6fc938b2e264306ec304eda518007d1764826381969',
    byte_length: 5,
  }])
  .build(); // computes payload_hash automatically
```

The service revalidates the attachment. The v1.3 limits are 5 attachments, 2 MiB per
decoded file, 5 MiB decoded total, 8 MiB provider envelope, and 16 MiB HTTP envelope.
Allowed file types are PDF, JPEG, PNG, DOCX, XLSX, CSV, and TXT.

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

`sendMail()` retries retryable HTTP errors (503 / `retryable: true`) and transport failures (connection refused, DNS failure, timeout) with exponential backoff. Defaults: 3 retries, 200 ms base delay. Override with `{ maxRetries, baseDelayMs }`.

Idempotent HTTP retry: `sendMail()` may POST the same `mail_request_id` and payload again;
Mailer returns `already_accepted` without creating a duplicate queue entry. This is distinct
from provider delivery: after durable submission evidence exists, v1.3 never invokes the
provider again for the same request. An ambiguous outcome is terminal `delivery_unknown`;
do not resend it. For a deliberate business resend, build a new request with a new
`mail_request_id`. Status polling is currently an HTTP API operation, not an SDK helper.
