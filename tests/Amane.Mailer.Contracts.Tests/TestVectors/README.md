# payload_hash test vectors

`payload_hash` is computed over the delivery payload, not the routing envelope.

Included fields:

- `source_service`
- `purpose`
- `to`
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

Numeric JSON values are outside the mail payload contract. `metadata` values are strings only; callers must stringify numeric identifiers before hashing.

Non-.NET reference implementations with vector verification:
[`examples/payload-hash/`](../../../examples/payload-hash/README.md).
