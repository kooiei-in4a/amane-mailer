# Issue #622 — v1.3.1 Qualification Scope Revision

## Authority

This plan is the v1.3.1-specific qualification-scope authority for the
future RC3 release attempt. It does not alter the historical v1.3.0 scope,
the RC2 candidate, binding, qualification run, or evidence.

- Authority issue: #622
- Authority issue body SHA-256: `0f47c435132ffd9e6863546061f084ae80eda9e1123c1298d95ef7f25144230b`
- Scope manifest: `docs/qualification/v1.3.1-scope.json`
- Scope ID/version: `v1.3.1-rc-qualification` / `1`
- Release version: `1.3.1`
- Scope profile version: `1`
- Variant rules version: `6`
- Base authority: `docs/qualification/v1.3.0-scope.json`, scope digest `cf1f2a39b55fc731ab5d4ea6f1ca80622d2951eebc903e898470e47192e643e5`

## Human decision

The v1.3.1 release attempt deliberately does not require the following eight
Hard variants. This is a scope revision, not a Conditional exception:

- `G456-01/win-docker`
- `G456-13/win-docker`
- `G456-14/win-docker`
- `G456-17/win-docker`
- `G456-18/win-docker`
- `G456-19/win-docker`
- `G456-26/win-docker`
- `G456-33/win-docker`

The v1.3.1 overlay removes exactly those keys from the immutable v1.3.0
authority. Every other scenario, variant, gate class, predicate set, owner
class, optional key, and migration authority is inherited unchanged. In
particular, `G583-MIG-01` and `G583-MIG-02` retain both `win-docker` and
`linux-docker`, and `G583-MIG-03` retains `ci-auto`.

The revised Hard total is derived by the runner from the base profile and
the exact overlay: `47 - 8 = 39`. A future v1.3.1 qualification run starts
fresh at `0 / 39`; no RC2 evidence is migrated or reused.

## Runner contract

The runner accepts only these explicit v1.3 profiles:

- v1.3.0 candidates require `v1.3.0-rc-qualification` and Issue #583.
- v1.3.1 candidates require `v1.3.1-rc-qualification` and Issue #622.

An omitted scope, a cross-version scope, v1.3.2 or later, a changed overlay,
an omitted Linux counterpart, a reintroduced G456 Windows variant, a removed
unrelated key, a Hard-to-Conditional reclassification, or a removed G583
route fails closed. The canonical adapter registry and Windows fixture
capability remain available; they are not required-key authority.

## Non-goals and release gate

This revision changes no product, runtime, API, database migration, evidence
schema, seal, handoff, candidate, binding, or publication contract. It does
not create RC3 identities or start qualification. The implementation branch
must be independently reviewed at its exact head before any future RC3
candidate, intake, bind, qualification, seal, promotion, or publish action.
