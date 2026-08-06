[日本語](ghcr-image-publish.md)

# GHCR image publishing

Official Amane Mailer images are published only by digest-preserving
P-OCI-PROMOTE. The canonical workflow is
`.github/workflows/promote-qualified-oci.yml`.
Dispatch it from `refs/heads/main`; the candidate ref is independently bound
by the pre-login validator.

## Required handoff

Promotion consumes the OCI layout and handoff artifacts from one successful
`Generate Setup Release Candidate` run, plus a sealed qualification handoff.
Before registry login it verifies, without rebuilding:

- candidate workflow name/path, event, head branch, head SHA, run ID, and run attempt;
- exact OCI and handoff artifact IDs/names and non-expired state;
- `candidateId`, `qualificationRunId`, `releaseCommitSha`, OCI index digest,
  `image-identity.json`, `candidate-provenance.json`, and `buildx-metadata.json`;
- one immutable qualification binding, `GO_ELIGIBLE` + `APPROVE` decision, and
  exactly one sealed run-status event.

Any mismatch fails closed before registry login. The source digest is the final
image-index blob digest referenced by layout `index.json`.

## Promotion result

The workflow pushes the same OCI layout to exactly two tags:

- `vX.Y.Z`
- `sha-<releaseCommitSha>`

Both tags must resolve to the source image-index digest. `latest` is never
created. Promotion uses `EXTERNAL_PROVENANCE`; it does not add registry
attestation manifests. A runtime-generated `promote-proof.json` records the
candidate, qualification, digest, tag, and workflow identities and is uploaded
as a workflow artifact.

## Legacy route (retired)

`.github/workflows/publish-image.yml` remains only as a fail-closed tombstone.
It performs no product build, registry login, push, or tag creation and exits
with an explicit pointer to `promote-qualified-oci.yml`. Delete it only in a
separate change after all operational references are migrated.

## GitHub permissions and environment

The canonical promotion job uses `contents: read`, `actions: read`, and
`packages: write`, with the existing `release` environment approval. No
repository publish secret is required; the job-scoped `GITHUB_TOKEN` is used.

## Deploy host pull access

If GHCR packages are private, the deploy host must authenticate before
`docker compose pull` with a read-only token having `read:packages` scope:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```

See [digest-preserving OCI promotion](oci-promote.md) and
[release artifact verification](release-artifact-verification.en.md) for the
validation and consumer procedures.
