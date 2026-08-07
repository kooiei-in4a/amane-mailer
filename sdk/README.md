# Amane Mailer Consumer SDKs

Official TypeScript and Python SDKs for posting mail delivery requests to Amane Mailer.

Phase 1 scope (issue #218):

- Request builder with pre-validation
- Automatic `payload_hash` computation (matches Contracts test vectors)
- UUID generation (UUIDv7 preferred; UUIDv4 fallback documented)
- Typed handling for `accepted`, `already_accepted`, `IDEMPOTENCY_CONFLICT`, and retryable 503
- Idempotent resend and exponential backoff for retryable errors

Status GET (#216) is available on the HTTP API. SDK status-polling helpers remain
a follow-up (Phase 2). Webhook signature helpers wait on Consumer SDK follow-up
after #219.

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
  bearerToken: process.env.MAIL_SERVICE_TOKEN ?? 'local-mail-service-token',
});

const response = await client.sendMail(
  MailRequestBuilder.create()
    .tenantId('00000000-0000-0000-0000-000000000101')
    .sourceService('example-service')
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
excluded from `payload_hash`.

```javascript
const response = await client.sendMail(
  MailRequestBuilder.create()
    .tenantId('00000000-0000-0000-0000-000000000101')
    .sourceService('example-service')
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
from amane_mailer import MailerClient, MailRequestBuilder

client = MailerClient(
    base_url="http://127.0.0.1:5280",
    bearer_token="local-mail-service-token",
)

response = client.send_mail(
    MailRequestBuilder()
    .tenant_id("00000000-0000-0000-0000-000000000101")
    .source_service("example-service")
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
excluded from `payload_hash`.

```python
response = client.send_mail(
    MailRequestBuilder()
    .tenant_id("00000000-0000-0000-0000-000000000101")
    .source_service("example-service")
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

## payload_hash verification

Both SDKs ship cross-check tests against the official vectors:

`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json` and
`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-recipient-v1.3-vectors.json`

CI runs SDK tests in the `sdk-tests` job. Lower-level language examples remain in [examples/payload-hash/](../examples/payload-hash/README.md).

## Related docs

- [OpenAPI](../docs/api/openapi.yaml)
- [ADR 0012 D-05 payload_hash](../docs/adr/0012-mail-via-mailer-microservice.md)
- [Consumer quickstart in README](../README.md#consumer-クイックスタート)
