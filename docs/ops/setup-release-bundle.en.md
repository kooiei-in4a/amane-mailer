[日本語](setup-release-bundle.md)

# Easy Setup release-candidate bundles (#455)

This runbook describes how to generate **release-candidate** Easy Setup host
bundles for Windows x64 / Linux x64 / Linux arm64. Packaging stops at candidate
qualification handoff. Publishing is owned by [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458).

Design authority: [ADR 0021](../adr/0021-easy-setup-boundaries.md).

## Artifact composition

Each host RID archive contains:

| Path | Role |
|------|------|
| `Amane.Mailer` / `Amane.Mailer.exe` | Same Native AOT binary (setup assistant + Admin + runtime CLI) |
| `compose.yml` | Fixed deploy Compose template |
| `compose.image-digest.yml` | Digest-pin overlay (`MAILER_IMAGE_REFERENCE`) |
| `compose.recorded-metadata.yml` | Recorded metadata mount overlay |
| `compose.mailpit.yml` | Mode-1 Mailpit overlay |
| `.env.example` | Example only (never a real `.env`) |
| `config/mailer/*.example.json` + schema | Safe examples only |
| `release-bundle-manifest.json` | Distribution inventory (schemaVersion **1**, additive) |
| `SHA256SUMS` | Per-file SHA-256 checksums |
| `README-SETUP.md` | Operator entry notes |
| `oci/` | Local OCI image layout (B1; optional when provided) |

Candidate archive names:

```text
amane-mailer-vX.Y.Z-windows-x64.zip
amane-mailer-vX.Y.Z-linux-x64.tar.gz
amane-mailer-vX.Y.Z-linux-arm64.tar.gz
```

## Manifest schema (schemaVersion 1, additive)

`release-bundle-manifest.json` stays on **schemaVersion 1**. Packaging fields are
additive and validated by `ReleaseBundlePackaging` separately from runtime
`TrustedReleaseInventory.ValidateShape()`.

Required packaging fields include:

- `packagingKind` = `setup-release-candidate`
- `sourceCommitSha`
- `mailerVersion`, `hostRid`
- `imageRepository`, `imageTag`, `imageDigest`
- `ociIndexDigest` (must equal `imageDigest`)
- Compose file digests and launcher version range
- `supportedRecordedSchemaMin` / `Max`
- `supportedInspectEffectiveSchemaMin` / `Max`
- `artifactSha256`, `reproducibility`

Managed deployment metadata under operator `managed/` (ACTIVE, recorded.json,
verification) is a **different** concept. Do not treat the distribution manifest
as deployment metadata.

## OCI index digest pinning (no GHCR push)

Candidates build a local OCI layout via
`scripts/build-candidate-oci-image.sh` (`docker buildx --output type=oci`).

- Layout must include `oci-layout`, `index.json`, and `blobs/sha256/` (Agent B **B1**).
- `ociIndexDigest` is `sha256:` of `index.json` bytes and is pinned into the
  host-bundle manifest.
- This path **never** pushes to GHCR. #458 owns public image publish.

## Reproducibility

Reproducible candidate means:

1. Same Git source commit SHA
2. Same Dockerfile base image digests
3. Same `dotnet publish` RID / AOT flags / version properties
4. Same OCI layout build inputs

→ Same `ociIndexDigest` and same staged payload `artifactSha256`
(archive container metadata such as tar/zip timestamps may differ; compare
`artifactSha256` / `SHA256SUMS`, not archive bytes alone).

## Checksums

- Per-file: `SHA256SUMS` inside each staged tree
- Payload: `artifactSha256` in the manifest (ordered path + content digest)

## Secret scan / artifact smoke

```bash
bash scripts/scan-setup-release-bundle.sh artifacts/setup-release-candidate/staged
bash scripts/smoke-setup-release-bundle.sh artifacts/setup-release-candidate/staged
```

Artifact smoke checks `--help` / `setup assistant --help` when the RID matches
the host. Runtime Docker smoke (`scripts/release-smoke.sh`) remains separate.

## Generate locally

```bash
export MAILER_VERSION=1.2.0-candidate
export SOURCE_SHA="$(git rev-parse HEAD)"
bash scripts/generate-setup-release-bundle.sh
```

Or dispatch `.github/workflows/generate-setup-release-candidate.yml`.

CLI staging entry (after a host binary exists):

```text
Amane.Mailer setup stage-release-bundle --output ... --rid linux-x64 ...
```

## Handoff

| Issue | Owns next |
|-------|-----------|
| [#456](https://github.com/kooiei-in4a/amane-mailer/issues/456) | Qualification / go-no-go |
| [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) | Tag, GHCR, GitHub Release, public checksums |

See `artifacts/setup-release-candidate/CANDIDATE-HANDOFF.md` after generation.

## Explicit non-goals

- Git tag / GitHub Release creation
- GHCR push
- MSI / deb / rpm installers
- Auto updater
- macOS formal artifact
- NAS vendor package
- Setup UI inside the container

## Agent B plan findings addressed

| ID | Finding | Resolution |
|----|---------|------------|
| B1 | Prefer OCI layout over opaque `docker save` tar | `type=oci` layout + `oci-layout` / `index.json` / `blobs/sha256` validation |
| M1 | Keep schemaVersion 1 additive | Packaging fields added without bumping schema |
| M2 | Separate packaging validation from runtime inventory | `ValidatePackagingDocument` vs `ValidateShape` |
| M3 | Pin immutable digest without publishing | Local OCI index digest in manifest; no GHCR push |
| M4 | Emit SHA-256 checksums | `SHA256SUMS` + `artifactSha256` |
| M5 | Secret / private config scan | `scan-setup-release-bundle.sh` + staging scan |
| M6 | Define reproducibility / no `latest` | Manifest `reproducibility` text; forbid `latest` tags |
