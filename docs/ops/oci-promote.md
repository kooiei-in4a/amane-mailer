# Digest-preserving OCI promotion (P-OCI-PROMOTE)

Official image publication is a version-independent promotion of the sealed,
qualified candidate OCI layout. The promotion workflow never rebuilds the
product image and never creates a replacement digest.

Dispatch the canonical workflow from `refs/heads/main`; the candidate's exact
head branch/SHA is supplied as an input and validated separately.

The product ref owns candidate bytes and qualification. The release
infrastructure ref (`main`) owns only this promotion wrapper and proof
generation; it never checks out and rebuilds product image bytes.

## Authority

- Canonical workflow: `.github/workflows/promote-qualified-oci.yml`
- Promotion script: `scripts/promote-candidate-oci.sh`
- Candidate validator: `scripts/validate-candidate-oci-run.sh`
- Candidate handoff validator: `scripts/validate-candidate-oci-handoff.sh`
- Qualification validator: `scripts/validate-qualification-handoff.sh`
- Shared artifact preparation contract:
  [`docs/ops/shared-qualification-artifact-contract.md`](shared-qualification-artifact-contract.md)
- Tool: pinned `crane` (`go-containerregistry` `v0.20.3`), checksum-verified by
  `scripts/install-pinned-crane.sh`

The candidate workflow run, run attempt, OCI artifact ID/name, handoff artifact
ID/name, candidateId, qualificationRunId, releaseCommitSha, and OCI index digest
are all compared before registry login. The qualification handoff must contain
one immutable `binding.json`, an approved `decision/go-no-go.json`, and exactly
one sealed run-status event. Any mismatch or missing field stops the workflow.

Before the strict qualification validator runs, the workflow derives the
expected producer identity from the trusted Actions run, validates the exact
five-file production artifact with `prepare-qualification-handoff.py`, and
creates a byte-identical sealed-only view outside the immutable download root.
Git promotion uses the same preparation boundary and production-shape fixture.

The workflow generates `promote-proof.json` from the runtime destination
digests and workflow identity after promotion, then uploads it as an artifact.
External proof input is never trusted.

## Digest and tag contract

The source digest is the final image-index blob digest referenced by the OCI
layout `index.json` (not the SHA-256 of `index.json`). The workflow pushes the
same layout to the version tag (`vX.Y.Z`) and the immutable SHA tag
(`sha-<releaseCommitSha>`), then verifies both destination digests equal the
source digest. It does not publish `latest`.

Attestation mode remains `EXTERNAL_PROVENANCE`; no registry attestation
manifests are added by promotion.

## Local readiness proof

```bash
bash scripts/validate-candidate-oci-run-self-test.sh
bash scripts/promote-candidate-oci-self-test.sh
```

The tests use fixtures and a disposable localhost registry only. They do not
log in to or push to GHCR, and do not build the product Native AOT image.

## Legacy workflow

`.github/workflows/publish-image.yml` is retained as a fail-closed tombstone.
It intentionally stops before checkout, registry login, build, push, or tag
creation and points operators to this canonical workflow. Future deletion is a
separate change after all runbooks and operational references are migrated.
