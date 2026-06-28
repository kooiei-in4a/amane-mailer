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

## Null omission vs explicit null

Optional fields participate in the hash only when they appear in the JSON you send.

- **Omitted** optional field: not included in the hash input.
- **Explicit `null`**: canonicalized as `null` and included (for example `"reply_to": null`).

Match the JSON you POST. If your serializer omits null optional properties, compute the hash from that omitted shape—not from an object that includes explicit nulls.

## metadata values are strings

`metadata` values must be strings. Numeric identifiers must be stringified before hashing (for example `"form_id": "42"`, not `42`). Numeric JSON values are outside the mail payload contract.

## Sort and escape rules

After extracting included fields from the request JSON:

1. Sort object keys lexicographically (Unicode code-point / ordinal order) at every nesting level.
2. Serialize to compact JSON with no extra whitespace.
3. Escape strings with Mailer rules: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t`, and `\u00xx` for control characters below U+0020.
4. UTF-8 encode the canonical JSON string, SHA-256, lowercase hex (64 characters).

These examples mirror `MailPayloadHasher` in `src/Amane.Mailer.Contracts/Security/MailPayloadHasher.cs`, not a generic RFC 8785 library.

## Language examples

| Language | Implementation | Verify against test vectors |
|---|---|---|
| Python | [python/mail_payload_hash.py](python/mail_payload_hash.py) | `python examples/payload-hash/python/verify_vectors.py` |
| JavaScript (Node.js) | [javascript/mail_payload_hash.mjs](javascript/mail_payload_hash.mjs) | `node examples/payload-hash/javascript/verify_vectors.mjs` |
| Go | [go/mail_payload_hash.go](go/mail_payload_hash.go) | `go test ./examples/payload-hash/go/...` |

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
