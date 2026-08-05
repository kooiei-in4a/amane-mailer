# payload_hash test vectors

`payload_hash` is computed over the delivery payload, not the routing envelope.

## Two fixture files

Vectors are split across two files by contract generation:

- **`payload-hash-vectors.json`** (baseline): pre-ADR-0023 single-To/attachment vectors. Content
  and hash values are frozen. Also read directly by the Python/TypeScript SDK conformance tests
  (`sdk/python/tests/test_payload_hash.py`, `sdk/typescript/test/payload-hash.test.mjs`), which
  implement only the single-To contract until issue
  [#542](https://github.com/kooiei-in4a/amane-mailer/issues/542) lands.
- **`payload-hash-recipient-v1.3-vectors.json`** (recipient v1.3): ADR 0023 `to`/`cc`/`bcc`
  conformance vectors. **Not** read by the SDK conformance tests yet, since the Python/TypeScript
  SDK production code does not implement `cc`/`bcc`/optional-`to` until #542.

Both files are validated by `MailPayloadHasherTests` (`Shared_test_vectors_match_canonical_json_and_hash`
for the baseline, `Recipient_v1_3_test_vectors_match_canonical_json_and_hash` for recipient v1.3)
and by the non-.NET reference verifiers under `examples/payload-hash/`. A third test,
`Baseline_and_recipient_v1_3_vectors_do_not_share_names`, asserts vector names never collide
across the two files. See
[examples/payload-hash/README.md](../../../examples/payload-hash/README.md#vector-fixtures-baseline-vs-recipient-v13)
for the full rationale.

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
