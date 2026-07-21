# payload_hash examples (non-.NET)

Reference implementations for computing Mailer `payload_hash` outside .NET.

Official test vectors:

`tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json`

Contract notes (also in `tests/Amane.Mailer.Contracts.Tests/TestVectors/README.md`):

## Included fields

Hash covers delivery payload fields only:

- `source_service`
- `purpose`
- `to`
- `subject`
- `html_body`
- `text_body`
- `reply_to`
- `metadata`

## Excluded fields

Routing envelope and self-reference are excluded:

- `tenant_id`
- `mail_request_id`
- `payload_hash`
- `scheduled_at`

## Null omission vs explicit null

Optional fields participate in the hash only when they appear in the JSON you send.

- **Omitted** optional field: not included in the hash input.
- **Explicit `null`**: canonicalized as `null` and included (for example `"reply_to": null`).

Match the JSON you POST. If your serializer omits null optional properties, compute the hash from that omitted shape—not from an object that includes explicit nulls.

## metadata values are strings

`metadata` values must be strings. Numeric identifiers must be stringified before hashing (for example `"form_id": "42"`, not `42`). Numeric JSON values are outside the mail payload contract.

## Sort and escape rules

After extracting included fields from the request JSON:

1. Sort object keys by .NET `StringComparer.Ordinal` (UTF-16 code-unit order) at every nesting level. JavaScript `Array.prototype.sort()` matches this; Python and Go examples implement the same rule explicitly.
2. Serialize to compact JSON with no extra whitespace.
3. Escape strings with Mailer rules: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\u00xx` for control characters below U+0020.
4. UTF-8 encode the canonical JSON string, SHA-256, lowercase hex (64 characters).

These examples mirror `MailPayloadHasher` in `src/Amane.Mailer.Contracts/Security/MailPayloadHasher.cs`, not a generic RFC 8785 library.

## Language examples

| Language | Implementation | Verify against test vectors | Request JSON verifier |
|---|---|---|---|
| Python | [python/mail_payload_hash.py](python/mail_payload_hash.py) | `python examples/payload-hash/python/verify_vectors.py` | `python examples/payload-hash/python/verify_request.py request.json` |
| JavaScript (Node.js) | [javascript/mail_payload_hash.mjs](javascript/mail_payload_hash.mjs) | `node examples/payload-hash/javascript/verify_vectors.mjs` | — |
| Go | [go/mail_payload_hash.go](go/mail_payload_hash.go) | `go test ./...` in `examples/payload-hash/go` | — |

CI runs all three verifiers in the OpenAPI validation workflow. Contract drift check (`scripts/check-contract-drift.mjs`) asserts these examples stay present and reference the shared test vectors.

## Minimal usage

Build the mail request object you will POST, then set `payload_hash` from delivery fields only:

```python
request = {
    "tenant_id": "...",
    "mail_request_id": "...",
    "source_service": "example-service",
    "purpose": "FormResponseNotification",
    "to": [{"email": "admin@example.com"}],
    "subject": "New response",
    "text_body": "A new response arrived.",
    "payload_hash": "",  # placeholder; excluded from hash input
}
request["payload_hash"] = compute_delivery_payload_sha256_hex(request)
```

Use the same pattern in JavaScript and Go—see each language file for exported helpers.

## Verify your request JSON (Python)

When you already have the JSON file you plan to POST, use the request verifier to inspect
included fields, canonical JSON, computed hash, and whether `payload_hash` matches:

```bash
python examples/payload-hash/python/verify_request.py examples/payload-hash/fixtures/form-response-request.json
```

Example output:

```text
Included fields (hash input):
  - purpose
  - source_service
  - subject
  - text_body
  - to

Excluded from hash (present in request):
  - mail_request_id
  - payload_hash
  - tenant_id

Canonical JSON:
{"purpose":"FormResponseNotification",...}

Computed SHA-256:
7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9

Request payload_hash:
7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9

Result: MATCH
```

Exit code `0` means match (or no `payload_hash` field to compare); `1` means mismatch; `2` means input error.

Run verifier tests against official vectors:

```bash
python examples/payload-hash/python/verify_request_vectors.py
```

## Troubleshooting `payload_hash` mismatches

Applies to both .NET and non-.NET Consumers — Mailer recomputes the hash from
the request JSON it received, so any deviation from the rules above produces
a mismatch regardless of implementation language.

### `INVALID_PAYLOAD_HASH` (422)

Mailer recomputes the hash from your own request body and compares it against
the `payload_hash` you sent. A 422 `INVALID_PAYLOAD_HASH` means those two
values disagree for **that single request**—it does not compare against any
previous request. Typical causes:

1. **Included vs. excluded fields mixed up.** Hashing `tenant_id`,
   `mail_request_id`, `payload_hash`, or `scheduled_at` (routing/schedule/self-reference fields), or
   omitting one of the delivery fields (`source_service`, `purpose`, `to`,
   `subject`, `html_body`, `text_body`, `reply_to`, `metadata`) that is
   actually present in the request JSON from the hash input.
2. **Omitted vs. explicit `null` mismatch.** Computing the hash as if an
   optional field were omitted, then POSTing it as `"reply_to": null` (or the
   reverse). The hash input must match the JSON shape you actually send.
3. **Empty string treated as omitted.** Some application code skips adding
   a field to the hash input when its value is an empty string `""`,
   treating it like "no value." But `""` is a present, non-null value—if
   the JSON you send includes `"reply_to": ""`, the hash input must include
   it too. Only an actually absent key should be treated as omitted.
4. **Serializer null handling differs from what you hashed.** Some
   JSON libraries drop `null`-valued properties by default, or add them back
   on deserialize/reserialize. If your serializer's actual output differs
   from the shape you hashed, the hashes diverge even though your code
   "looks" correct.
5. **Non-ordinal key sorting.** Locale-aware or case-insensitive sort
   implementations can reorder keys differently from .NET
   `StringComparer.Ordinal` (UTF-16 code-unit order), producing a different
   canonical JSON string and thus a different hash.
6. **Hash computed against different field values than the ones POSTed.**
   Mailer parses the JSON and canonicalizes semantic values, so whitespace,
   key order, or string-escaping style in your request body do not by
   themselves cause a mismatch. What does cause one is hashing a payload
   that is later mutated before send—for example, hashing a request object
   and then trimming, re-encoding, or otherwise editing a field's value
   (subject, body, `metadata` entry, etc.) before the actual POST.
7. **Digest encoded incorrectly.** `payload_hash` must be a lowercase,
   64-character hex-encoded SHA-256 digest computed over the UTF-8 bytes of
   the canonical JSON. Uppercase hex, base64, or a digest computed over a
   non-UTF-8 byte encoding will not match, even if the canonical JSON itself
   is correct.

Use the [request JSON verifier](#verify-your-request-json-python) to inspect
exactly which fields were included/excluded and see the canonical JSON Mailer
would compute, before you POST.

Note: non-string `metadata` values (for example `"form_id": 42` instead of
`"form_id": "42"`) are **not** a cause of `INVALID_PAYLOAD_HASH`. Mailer
deserializes the request body into a typed contract before hash validation
runs, and a non-string `metadata` value fails that step first, returning
400 `INVALID_REQUEST`. Stringify identifiers before sending regardless, to
avoid the 400.

### `IDEMPOTENCY_CONFLICT` (409) vs. `INVALID_PAYLOAD_HASH` (422)

These two error codes check different things, in this order:

1. Mailer first checks that **your own `payload_hash` is self-consistent**
   with your own request body (`INVALID_PAYLOAD_HASH`, 422). This does not
   look at any other request.
2. Only if that check passes does Mailer look up whether the same
   `mail_request_id` (scoped to `tenant_id` + `source_service`) was already
   accepted. If it was, and the **newly computed hash differs from the hash
   stored for that earlier request**, Mailer returns `IDEMPOTENCY_CONFLICT`
   (409).

In short: `INVALID_PAYLOAD_HASH` means your hash doesn't match your own
payload. `IDEMPOTENCY_CONFLICT` means your hash is fine, but the payload
content changed between two requests that reused the same idempotency key —
usually because a retry was sent with edited subject/body/recipient/metadata
instead of the original content.
