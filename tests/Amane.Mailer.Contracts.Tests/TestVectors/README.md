# payload_hash test vectors

`payload_hash` is computed over the delivery payload, not the routing envelope.

Included fields:

- `source_service`
- `purpose`
- `to`
- `cc`
- `bcc`
- `subject`
- `html_body`
- `text_body`
- `reply_to`
- `metadata`

Excluded fields:

- `tenant_id`: routing/authentication envelope
- `mail_request_id`: idempotency key
- `payload_hash`: self-referential

Optional fields are included only when the App writes them to the payload JSON. Explicit `null` values are canonicalized as `null` and included in the hash.

`to`, `cc`, and `bcc` are the exception to that rule (ADR 0023 D-02): an absent property,
explicit `null`, and an empty array are all equivalent and the role is omitted from the hash
document entirely -- a CC-only request's hash document has no `to` key at all. Non-empty roles
hash the validated canonical recipient value (trimmed, case-preserved address; a whitespace-only
`display_name` is treated as absent), not the raw request bytes, so equivalent-but-differently-
formatted submissions hash identically. See the `cc-only-no-to-key`, `bcc-only-no-to-or-cc-key`,
`to-cc-bcc-combined`, `to-address-trimmed`/`to-address-with-surrounding-whitespace`, and
`cc-display-name-omitted`/`cc-display-name-whitespace-only` vectors.

Numeric JSON values are outside the mail payload contract. `metadata` values are strings only; callers must stringify numeric identifiers before hashing.

Non-.NET reference implementations with vector verification:
[`examples/payload-hash/`](../../../examples/payload-hash/README.md).
