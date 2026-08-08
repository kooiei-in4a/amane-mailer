# v1.3.0 RC qualification scope (Issue #583)

This document is the versioned scope authority for the v1.3.0 RC qualification
input materialization. The v1.2.0 qualification plan in Issue #456/#458 is a
historical profile and is not edited by this document.

## Authority

- scope ID: `v1.3.0-rc-qualification`
- scope version: `1`
- authority issue: `#583`
- authority issue body SHA-256: `37319e0638405573b6594497392513a2b4230c54908f557a2d67f48fe2d87219`
- release version: `1.3.0`

The authority body digest is checked when the scope manifest is loaded. A
changed Issue body requires a new scope version; it is not silently accepted.

## Scenario authority

G456-01 through G456-41 are reused only as explicitly listed in the v1.3
scope manifest. Their variants, gate classes, owner classes, and predicate set
remain the legacy definitions unless a versioned overlay is present in that
manifest. The runner never falls back from a v1.3 profile to the legacy table.

The v1.3 migration scenarios are new, scope-qualified IDs:

- `G583-MIG-01` — fresh apply from an empty database; all migrations 001..018
  are applied in runner order and the 014..018 delta is recorded.
- `G583-MIG-02` — upgrade from the v1.2.0 baseline through 013 to 018; the
  baseline and delta histories are both required and ordered.
- `G583-MIG-03` — schema/privacy contract; the 014..018 schema allowlist,
  constraints/indexes, and value-free PII/secret canary must pass.

All three migration scenarios are Hard gates and require the
`maintainer-migration` owner class.

## Migration inventory authority

The v1.2.0 baseline is migrations 001..013. The v1.3.0 delta is migrations
014..018. The full inventory is the exact runner-order list 001..018 from the
RC tree. The scope manifest records every path, SHA-256, Git blob SHA, the full
inventory digest, the delta digest, and the predicate-set version. Missing,
extra, reordered, or tree-mismatched files fail closed.

## Binding requirements

The scope manifest digest, authority issue/body digest, plan path/revision/file
digest, migration baseline/delta/full digests, and predicate-set version are
bound into `binding.json`, Phase 2, the qualification run identity, Phase 4,
and the publication-only handoff. Legacy #456/#458 runs retain their existing
schema and validation behavior.

## Required validation

Positive tests cover the legacy profile unchanged and the v1.3 profile with
all three migration scenarios. Negative tests reject missing or stale scope
authority, wrong plan/body digest, baseline/delta/full inventory mismatch,
extra migration 019, a legacy G456 migration payload under the v1.3 profile,
unsupported generic PASS, wrong RC/OCI identity, and wrong owner/restricted
lane identity.

No B-MIG-PIN, candidate dispatch, formal qualification, seal, promotion, tag,
publish, or deploy may run until this scope implementation is reviewed on a
fixed GitHub head and accepted by Agent B.
