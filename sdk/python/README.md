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
10 recipients and all roles accept at most 20 recipients combined.

## Local Mailer integration

With local Mailer running (`http://127.0.0.1:5280`):

```bash
PYTHONPATH=src python -m amane_mailer.scripts.send_local
```

Environment variables:

| Variable | Default |
|---|---|
| `MAILER_BASE_URL` | `http://127.0.0.1:5280` |
| `MAILER_API_KEY` | required managed API key for one Sender |

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

Idempotent resend: POST the same `mail_request_id` and payload again; Mailer returns `already_accepted` without creating a duplicate queue entry.

The managed API key selects the Sender. The SDK does not send `tenant_id`,
`source_service`, `From`, provider, or `payload_hash`.
