# Amane Mailer Consumer SDKs

Official TypeScript and Python SDKs for posting mail delivery requests to Amane Mailer.

Phase 1 scope (issue #218), with v1.3 recipient and attachment support:

- Request builder with pre-validation
- Automatic `payload_hash` computation (matches Contracts test vectors)
- UUID generation (UUIDv7 preferred; UUIDv4 fallback documented)
- Typed handling for `accepted`, `already_accepted`, `IDEMPOTENCY_CONFLICT`, and retryable 503
- Idempotent resend and exponential backoff for retryable errors
- Multiple `To` / `CC` / `BCC` roles, including optional `to` for Cc-only / Bcc-only requests
- Attachment metadata builders and v1.3 payload-hash projection

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

#### v1.3 recipients and attachment

The builder API mirrors the HTTP fields. `to()`, `cc()`, and `bcc()` set the
first recipient for a role; `addTo()`, `addCc()`, and `addBcc()` append in role
order. Each role accepts at most 10 recipients and all roles at most 20. A
Cc-only request is expressible by omitting `.to()`; the Python builder can use
`.to(email=None)` when an explicit `null` is useful. The server still validates
duplicates and canonical addresses.

```javascript
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

The attachment list is bounded at 5 items, 2 MiB per decoded file, and 5 MiB
decoded total. The service also enforces provider / HTTP envelope limits and
revalidates filename, content type, structure, digest, and length. For a
Bcc-only request, use `.cc(null)` / `.to(null)` as needed and provide at least
one `.bcc(...)`.

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

### Retry and delivery status boundary

SDK retries are HTTP-client behavior: a retryable `503` or transport failure can
repeat the POST with the same `mail_request_id` and payload. This is separate
from Mailer's provider-delivery boundary. In v1.3, once durable submission
evidence exists, Mailer does not invoke ACS/Mailpit again for the same request.
An ambiguous provider outcome becomes terminal `delivery_unknown`; do not resend
that request. If a business resend is required, create a new request with a new
`mail_request_id`. The SDKs do not yet provide a status-polling helper; use the
HTTP GET documented in the service spec.

## payload_hash verification

Both SDKs ship cross-check tests against the official vectors:

`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json` and
`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-recipient-v1.3-vectors.json`

CI runs SDK tests in the `sdk-tests` job. Lower-level language examples remain in [examples/payload-hash/](../examples/payload-hash/README.md).

## Related docs

- [OpenAPI](../docs/api/openapi.yaml)
- [ADR 0012 D-05 payload_hash](../docs/adr/0012-mail-via-mailer-microservice.md)
- [Consumer quickstart in README](../README.md#consumer-クイックスタート)
