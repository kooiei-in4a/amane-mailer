# Qualification runner / durable store

Issue [#580](https://github.com/kooiei-in4a/amane-mailer/issues/580) provides
the durable execution boundary between a candidate handoff and the sealed
qualification handoff consumed by Issues #504 and #505. It preserves the
existing Issue #456 contract; it does not change the scenario table, gate
classes, or the meaning of a seal.

## Security boundary

The store root is a maintainer-controlled path supplied at runtime. It must
not be inside the repository and must not be uploaded as one GitHub Actions
artifact. Candidate archives, producer identity, the Phase-1 object inventory,
and the migration PIN are checked before binding. The runner rejects symlinks,
path traversal, floating-point JSON, non-ASCII schema keys, and secret/PII-like
evidence fields. ACS credentials, recipient/sender values, provider errors,
and private keys are never accepted as evidence payloads.

The runner does not execute Docker, ACS, HTTPS, or production operations. A
lane adapter executes one bound `(scenarioId, variantId)` and writes a complete
value-free evidence envelope. Restricted maintainer lanes run outside the
ordinary CI trust boundary and identify their owner in
`executedByRole` / `executedByIdentity`.

Only the scenario validators implemented in the runner can contribute a PASS
(`G456-03`, `G456-04`, `G456-05`, `G456-06`, and `G456-42..44`). Other #456
lanes are deliberately fail-closed until a dedicated validator is registered;
`predicateResult=PASS` alone is never sufficient. This keeps an unfinished
Admin/HTTPS/security/AOT lane from becoming an accidental GO.

## Lifecycle

```text
candidate handoff
  -> intake (candidateId, producer-bound Phase-1)
  -> bind (bindingId, qualificationRunId, authorization, Phase-2)
  -> evidence + disposition (append-only Phase-3)
  -> seal (decision, Phase-4 manifest, terminal sealed event)
  -> verify
  -> publication-only handoff (GO_ELIGIBLE + APPROVE only)
```

Each object is write-once. A sealed or abandoned run is terminal; corrections
require a new `qualificationRunId`. The final `run-status-events/*.json`
object is the sole seal authority. `verify` revalidates the candidate,
authorization, migration PIN/tree, evidence envelopes, replay state, decision,
and complete object inventory before handoff.

## Commands

The following are interfaces for a maintainer-controlled harness. All values
are synthetic placeholders. Do not put secrets in shell history or logs.

```bash
python3 scripts/qualification-runner.py intake \
  --candidate-root <downloaded-candidate-handoff> \
  --store-root <maintainer-durable-store> \
  --release-commit-sha <exact-40-hex-sha> \
  --expected-oci-digest sha256:<64-hex> \
  --expected-workflow-ref <trusted-producer-workflow-ref>

python3 scripts/qualification-runner.py bind \
  --store-root <maintainer-durable-store> \
  --candidate-id <candidate-id> \
  --issue-snapshot <normalized-value-free-issue-snapshot.json> \
  --plan-file docs/agent-workflows/issue-456-release-qualification-plan.md \
  --plan-commit-sha <plan-commit-sha> \
  --repo-root <checkout-containing-exact-release-commit> \
  --migration-pin <migration-pin.json> \
  --run-attempt-nonce <operator-generated-nonce> \
  --evidence-owners <owner-map.json> \
  --qualification-lead-role <role> \
  --qualification-lead-identity <value-free-handle> \
  --conditional-approver-role <role> \
  --conditional-approver-identity <value-free-handle>
```

`bind` requires the complete canonical G456-01..44 Issue table, exact variant
cardinality, owner coverage (including optional G456-38..41 keys), and a
release-tree-verified migration PIN. The saved binding, authorization, Phase-2
manifest, and migration PIN are cross-checked again at every later command.

### Evidence envelope

`evidence --observations` is mandatory; the runner does not construct a
qualification result from a short CLI result flag. The JSON must contain the
complete #456 common envelope: schema/kind, evidence type and 64-hex evidence
ID, candidate/source/issue/plan/binding/run identity, `attempt: 1`, result,
RFC3339 timestamps, executed actor, procedure/revision, runner/tool version,
attestation time, value-free `identity`, a PASS prohibited-content scan with
scanner/report digest, and a scenario-specific `typePayload`.

```bash
python3 scripts/qualification-runner.py evidence \
  --run-root <store-root>/runs/<qualification-run-id> \
  --evidence-id <64-hex-evidence-id> \
  --scenario-id G456-03 --variant-id acs-staging-nosend \
  --result PASS \
  --executed-by-role <lane-owner-role> \
  --executed-by-identity <value-free-owner-handle> \
  --observations <complete-evidence-envelope.json>
```

The ACS and migration validators enforce the exact #456 predicates and both
PASS/FAIL directions. Direct `result=EXCEPTION` evidence is rejected. For
unsupported scenario lanes, a PASS is rejected until its dedicated validator
is implemented.

### Dispositions and Conditional exceptions

Disposition actions are append-only (`accept`, `supersede`, `invalidate`,
`restore`) and require the owner or qualification lead according to the
transition. Restore is key-scoped. Conditional exceptions are separate
write-once objects created by the evidence owner and approved by the named
conditional approver; Hard and migration rows cannot use them.

```bash
python3 scripts/qualification-runner.py exception \
  --run-root <store-root>/runs/<qualification-run-id> \
  --exception-id <64-hex-exception-id> \
  --scenario-id G456-29 --variant-id win-docker \
  --reason-not-executable <value-free-reason> \
  --alternate-verification <value-free-procedure> \
  --residual-risk <value-free-risk> \
  --impact-scope <value-free-scope> \
  --created-by-role <owner-role> \
  --created-by-identity <owner-handle>

python3 scripts/qualification-runner.py exception-disposition \
  --run-root <store-root>/runs/<qualification-run-id> \
  --scenario-id G456-29 --variant-id win-docker --action approve \
  --target-exception-id <64-hex-exception-id> \
  --reason-code <value-free-code> \
  --approved-by-role <conditional-approver-role> \
  --approved-by-identity <conditional-approver-handle>
```

### Seal, verify, and handoff

The seal actor must be supplied explicitly and must match the authorization
snapshot. An approved handoff is emitted only when the recomputed machine
verdict is `GO_ELIGIBLE` and the human decision is `APPROVE`.

```bash
python3 scripts/qualification-runner.py seal \
  --run-root <store-root>/runs/<qualification-run-id> \
  --current-issue-snapshot <fresh-normalized-issue-snapshot.json> \
  --repo-root <checkout-containing-exact-release-commit> \
  --human-decision APPROVE \
  --approved-by-role <qualification-lead-role> \
  --approved-by-identity <qualification-lead-handle>

python3 scripts/qualification-runner.py verify \
  --run-root <store-root>/runs/<qualification-run-id> \
  --repo-root <checkout-containing-exact-release-commit>

python3 scripts/qualification-runner.py handoff \
  --run-root <store-root>/runs/<qualification-run-id> \
  --output-root <empty-publication-directory> \
  --repo-root <checkout-containing-exact-release-commit>
```

The publication-only handoff contains only the sealed binding, `go-no-go`,
terminal sealed event, and a value-free manifest. It must be copied byte-for-
byte; no workflow may rebuild or normalize the sealed JSON. A NO_GO or
unapproved run cannot produce a handoff.

## Abandon, recovery, retention, and audit

Phase-4 writes are deliberately not treated as one filesystem transaction. If
the process or durable backend fails after any Phase-4 object is written,
stop the run, do not edit or delete partial JSON, and append the terminal
abandon event when the store is writable:

```bash
python3 scripts/qualification-runner.py abandon \
  --run-root <store-root>/runs/<qualification-run-id> \
  --reason-code phase4-write-failed \
  --phase4-incomplete
```

An abandoned run is never repaired or reused. Preserve its immutable objects
and hashes for audit, then start a new bind with a new run-attempt nonce. If
the backend cannot append the abandon event, preserve the directory as
incomplete and record only its run ID and value-free failure code in the
maintainer audit channel; do not publish it as a handoff.

The durable-store owner must retain candidate intake, run objects, terminal
event, and value-free hash inventory for the repository's release-audit
retention period (at least the corresponding release record's retention
period). Retention cleanup is an external backend operation: it must be
approved, logged by run ID/digest only, and must never copy or log candidate
archives, ACS data, recipients, provider errors, tokens, or private keys.
The publication workflow receives only the three sealed JSON objects and its
manifest; the full store is not uploaded to Actions artifacts.

## Validation

The offline self-test uses synthetic candidate and full canonical Issue rows;
it contains no real candidate, secret, PII, registry, ACS, or GitHub data. It
also asserts that an arbitrary generic PASS and a value-bearing envelope are
rejected:

```bash
python3 -m py_compile scripts/qualification-runner.py scripts/qualification-runner-self-test.py
python3 scripts/qualification-runner-self-test.py
```

Passing this test proves only the store, identity, append-only, fail-closed
validator, seal, and handoff mechanics. It is not qualification evidence and
does not authorize RC2 promotion or production release.
