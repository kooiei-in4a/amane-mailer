# Amane Mailer Consumer SDKs

Official TypeScript and Python SDKs for posting mail delivery requests to Amane Mailer.

v2 scope:

- Request builder with pre-validation
- UUID generation (UUIDv7 preferred; UUIDv4 fallback documented)
- Typed handling for `accepted`, `already_accepted`, `IDEMPOTENCY_CONFLICT`, and retryable 503
- Idempotent resend and exponential backoff for retryable errors

Status GET is available on the HTTP API. SDK status-polling helpers remain a
follow-up. Outbound delivery webhooks are not part of v2; poll status instead.

## Quickstart

### TypeScript (Node.js 20+)

```bash
cd sdk/typescript
npm test
```

```javascript
import { MailerClient, MailRequestBuilder } from '@amane/mailer';

const client = new MailerClient({
  baseUrl: process.env.MAILER_BASE_URL ?? 'http://127.0.0.1:5280',
  bearerToken: process.env.MAILER_API_KEY,
});

const response = await client.sendMail(
  MailRequestBuilder.create()
    .generateMailRequestId()
    .purpose('FormResponseNotification')
    .to({ email: 'admin@example.com' })
    .subject('New response')
    .textBody('A new response arrived.')
    .build(),
);

console.log(response.status); // 'accepted' | 'already_accepted'
```

Scheduled send uses OpenAPI `date-time` with timezone `Z` or an explicit offset.
Omit `scheduled_at` (or set it to `null`) for immediate delivery. The field is
included in the server-side canonical payload comparison.

```javascript
const response = await client.sendMail(
  MailRequestBuilder.create()
    .generateMailRequestId()
    .purpose('FormResponseNotification')
    .to({ email: 'admin@example.com' })
    .subject('New response')
    .textBody('A new response arrived.')
    .scheduledAt('2026-08-01T09:00:00Z') // or '2026-08-01T18:00:00+09:00'
    .build(),
);
```

See [typescript/README.md](typescript/README.md) for error handling, retries, and local Mailer integration.

### Python (3.12+)

```bash
cd sdk/python
python -m unittest discover -s tests -v
```

```python
import os

from amane_mailer import MailerClient, MailRequestBuilder

client = MailerClient(
    base_url="http://127.0.0.1:5280",
    bearer_token=os.environ["MAILER_API_KEY"],
)

response = client.send_mail(
    MailRequestBuilder()
    .generate_mail_request_id()
    .purpose("FormResponseNotification")
    .to(email="admin@example.com")
    .subject("New response")
    .text_body("A new response arrived.")
    .build()
)

print(response.status)  # accepted | already_accepted
```

Scheduled send uses the same OpenAPI `date-time` rules (`Z` or an explicit offset).
Omit `scheduled_at` (or set it to `None`) for immediate delivery. The field is
included in the server-side canonical payload comparison.

```python
response = client.send_mail(
    MailRequestBuilder()
    .generate_mail_request_id()
    .purpose("FormResponseNotification")
    .to(email="admin@example.com")
    .subject("New response")
    .text_body("A new response arrived.")
    .scheduled_at("2026-08-01T09:00:00Z")  # or "2026-08-01T18:00:00+09:00"
    .build()
)
```

See [python/README.md](python/README.md) for error handling, retries, and local Mailer integration.

## Related docs

- [OpenAPI](../docs/api/openapi.yaml)
- [ADR 0024 Sender and managed API key identity](../docs/adr/0024-sender-and-managed-api-key-identity.md)
- [Consumer quickstart in README](../README.md#consumer-クイックスタート)
