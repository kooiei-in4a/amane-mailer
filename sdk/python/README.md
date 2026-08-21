# amane-mailer (repo-internal)

Python Consumer SDK for Amane Mailer. Ships as an in-repo package (`0.0.0.dev0`); PyPI publish is out of scope for Phase 1.

## Requirements

- Python 3.12+

## Install (editable, for local development)

```bash
pip install -e .
```

## Tests

```bash
PYTHONPATH=src python -m unittest discover -s tests -v
```

On Windows PowerShell:

```powershell
$env:PYTHONPATH = "src"
python -m unittest discover -s tests -v
```

## Multiple recipients

`to()`, `cc()`, and `bcc()` set a role with one recipient. Use `add_to()`, `add_cc()`,
and `add_bcc()` to append recipients while preserving role order. Each role accepts at most
10 recipients and all roles accept at most 20 recipients combined. `to(email=None)` or
omitting `to()` allows a Cc-only / Bcc-only request; the built request still needs at least
one recipient across all roles.

```python
from amane_mailer import MailRequestBuilder

request = (
    MailRequestBuilder()
    .tenant_id("00000000-0000-0000-0000-000000000101")
    .source_service("example-service")
    .generate_mail_request_id()
    .purpose("FormResponseNotification")
    .to(email="admin@example.com")
    .cc(email="team@example.com")
    .bcc(email="audit@example.com")
    .subject("Invoice attached")
    .text_body("Please find the invoice attached.")
    .attachments([{
        "file_name": "hello.txt",
        "content_type": "text/plain",
        "content_base64": "SGVsbG8=",
        "content_sha256": "185f8db32271fe25f561a6fc938b2e264306ec304eda518007d1764826381969",
        "byte_length": 5,
    }])
    .build()  # computes payload_hash automatically
)
```

The service revalidates the attachment. The v1.3 limits are 5 attachments, 2 MiB per
decoded file, 5 MiB decoded total, 8 MiB provider envelope, and 16 MiB HTTP envelope.
Allowed file types are PDF, JPEG, PNG, DOCX, XLSX, CSV, and TXT.

## Local Mailer integration

With local Mailer running (`http://127.0.0.1:5280`):

```bash
PYTHONPATH=src python -m amane_mailer.scripts.send_local
```

Environment variables:

| Variable | Default |
|---|---|
| `MAILER_BASE_URL` | `http://127.0.0.1:5280` |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` |
| `MAILER_TENANT_ID` | `00000000-0000-0000-0000-000000000101` |

## mail_request_id generation

`generate_mail_request_id()` prefers **UUIDv7** (time-ordered, OpenAPI recommendation). When the timestamp is outside the 48-bit range, it falls back to **UUIDv4**.

## Error handling

```python
from amane_mailer import (
    MailerClient,
    MailerIdempotencyConflictError,
    MailerRetryableError,
    MailerValidationError,
)

try:
    client.send_mail(request)
except MailerIdempotencyConflictError:
    ...
except MailerValidationError:
    ...
except MailerRetryableError:
    ...
```

## Retries

`send_mail()` retries retryable errors (503 / `retryable: true`) with exponential backoff. Defaults: 3 retries, 0.2 s base delay. Override with `max_retries` and `base_delay_seconds`.

Idempotent HTTP retry: `send_mail()` may POST the same `mail_request_id` and payload again;
Mailer returns `already_accepted` without creating a duplicate queue entry. This is distinct
from provider delivery: after durable submission evidence exists, v1.3 never invokes the
provider again for the same request. An ambiguous outcome is terminal `delivery_unknown`;
do not resend it. For a deliberate business resend, build a new request with a new
`mail_request_id`. Status polling is currently an HTTP API operation, not an SDK helper.
