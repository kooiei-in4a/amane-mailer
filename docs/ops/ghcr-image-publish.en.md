[日本語](ghcr-image-publish.md)

# GHCR image publishing

Official Amane Mailer images have two purpose-specific publication paths:

- Early #649 path: `.github/workflows/publish-release-image.yml`. It builds one
  `linux/amd64` image from the requested source SHA, runs the smoke test and a
  no-cache digest reproducibility gate, then publishes that same OCI layout.
- Qualified release path: digest-preserving **P-OCI-PROMOTE** from a qualified
  OCI layout. The canonical workflow is
  `.github/workflows/promote-qualified-oci.yml`.

The early path is the minimum amd64 public target; it does not replace
multi-arch qualification. Neither path rebuilds different bytes after the
build has been accepted.
The qualified workflow is dispatched from `refs/heads/main`; its candidate ref
is independently bound by the pre-login validator.
The product ref owns candidate bytes and qualification; `main` owns only the
release-infrastructure promotion wrapper and proof generation.

> **v1.2.0 note:** The public OCI was published via **P-OCI-PROMOTE** (promote the
> qualified layout) with `EXTERNAL_PROVENANCE` — no registry attestation
> manifests. See [docs/releases/v1.2.0.md](../releases/v1.2.0.md), the GitHub
> Release attachments, and [release artifact verification](release-artifact-verification.en.md).
> The “Required handoff” section below describes the qualified P-OCI-PROMOTE
> path. `publish-image.yml` is a retired fail-closed tombstone.

## Early #649 path

Maintainers dispatch `.github/workflows/publish-release-image.yml` from
`refs/heads/main` with a 40-hex `source_sha` and a `major.minor.patch`
`release_version`. After the existing `release` environment approval, it:

1. Builds the exact `linux/amd64` image and checks `--help`, `/healthz`, and `/readyz`.
2. Rebuilds without cache and requires the same manifest digest.
3. Pushes only `vX.Y.Z` and `sha-<sourceSha>` from the smoke-tested OCI layout, then verifies both digests.
4. Runs the `verify-public-image` job after a successful publish. The job
   verifies the same digest read-only from GHCR; it does not build, login, or
   push, and it has no `packages: write` permission.

It never creates `latest` or adds registry attestation manifests. If the
pre-publish checks fail, the workflow does not log in or publish. Use the
P-OCI-PROMOTE path below when a multi-arch qualified handoff is required.

## Publication evidence

The publish job keeps the build smoke, no-cache reproducibility, and publication
inputs in a value-free artifact with 14-day retention. The following
read-only verification job stores a final evidence artifact with 30-day
retention after checking:

- the `vX.Y.Z` and `sha-<sourceSha>` tags resolve to the expected digest;
- both tag digests are equal;
- the published digest pulls as `linux/amd64`;
- the digest image's OCI `source` / `revision` / `version` labels match; and
- `--help` succeeds from the digest reference.

The final record is
`artifacts/publish-release-image/release-publication-evidence.json` inside the
workflow artifact. Its `schemaVersion: 1`, `evidenceType:
release-image-publication` schema records `workflowRunId` /
`workflowRunAttempt` / `workflowName` / `workflowRef` /
`gitRef`, source SHA, release version, platform, published digest, both tags,
per-tag verified digests, OCI labels, all three gate results, and
`recordedAtUtc`. The companion `public-consumer-verification.json` contains the
read-only consumer checks. Tokens, credentials, PII, secret URLs, and raw
registry errors are not written to either record.

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
