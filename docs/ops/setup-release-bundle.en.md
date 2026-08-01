[日本語](setup-release-bundle.md)

# Easy Setup release-candidate bundles (#455)

Operator judgment and start procedures are owned by the
[setup guide](setup-guide.en.md).

This runbook describes how to generate **release-candidate** Easy Setup host
bundles for Windows x64 / Linux x64 / Linux arm64. Packaging stops at candidate
qualification handoff. Publishing is owned by [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458).

Design authority: [ADR 0021](../adr/0021-easy-setup-boundaries.md).

## Artifact composition

Each host RID archive contains:

| Path | Role |
|------|------|
| `Amane.Mailer` / `Amane.Mailer.exe` | Same Native AOT binary (setup assistant + Admin + runtime CLI) |
| `LICENSE` | License text |
| `compose.yml` | Fixed deploy Compose template |
| `compose.image-digest.yml` | Digest-pin overlay (`MAILER_IMAGE_REFERENCE`) |
| `compose.recorded-metadata.yml` | Recorded metadata mount overlay |
| `compose.mailpit.yml` | Mode-1 Mailpit overlay |
| `examples/.env.example` | Example only (never a real `.env`) |
| `examples/config/mailer/*.example.json` + schema | Safe examples only |
| `release-bundle-manifest.json` | Distribution inventory (schemaVersion **1**, additive) |
| `FILES-SHA256SUMS` | Per-file SHA-256 checksum inventory |
| `README-SETUP.md` | Operator entry notes |

Host archives do **not** embed `oci/`. The multi-arch OCI layout is a separate
workflow artifact; hosts consume `image-identity.json` (repo / tag / digest only).

Candidate archive names:

```text
amane-mailer-vX.Y.Z-windows-x64.zip
amane-mailer-vX.Y.Z-linux-x64.tar.gz
amane-mailer-vX.Y.Z-linux-arm64.tar.gz
```

## Manifest schema (schemaVersion 1, additive)

`release-bundle-manifest.json` stays on **schemaVersion 1**. Packaging fields are
additive. Emit/validate lives in `tools/Amane.Mailer.ReleaseBundle` (build-only).
Runtime host Docker continues to deserialize via product
`ReleaseBundleManifestDocument` / `TrustedReleaseInventory.ValidateShape()`.

Required packaging fields include:

- `packagingKind` = `setup-release-candidate`
- `artifactId`, `sourceCommitSha`
- `mailerVersion`, `setupLauncherVersion`
- `hostRid` / `targetRid`, `platform`, `architecture`
- `imageRepository`, `imageTag`, `imageDigest`
- `ociIndexDigest` (must equal `imageDigest`; Buildx `containerimage.digest`)
- Compose file digests and launcher version range
- `supportedRecordedSchemaMin` / `Max`
- `supportedInspectEffectiveSchemaMin` / `Max`
- `supportedReleaseManifestSchemaMin` / `Max` (**packaging requires both == 1**;
  runtime `TrustedReleaseInventory.ValidateShape` / resolver do **not** require
  these fields and continue to enforce `schemaVersion == 1` exactly)
- `mailpitImageReference` (**required**, `repo@sha256:<64 lowercase hex>`;
  name-component after the last `/` must not contain `:`; registry ports such as
  `localhost:5000/mailpit@sha256:…` are allowed; `repo:tag@sha256:…` is rejected)
- `payloadTreeSha256` (ordered path + content digest **excluding** the manifest
  and `FILES-SHA256SUMS` / `SHA256SUMS` — non-self-referential)
- `artifactFileName`, `reproducibility`

`archiveSha256` is **not** embedded in the host manifest. It is recorded in
outer `CANDIDATE-SHA256SUMS` and `candidate-provenance.json`.

Managed deployment metadata under operator `managed/` (ACTIVE, recorded.json,
verification) is a **different** concept. Do not treat the distribution manifest
as deployment metadata.

## OCI index digest pinning (no GHCR push)

Candidates build a local multi-arch OCI layout via
`scripts/build-candidate-oci-image.sh`:

- `docker buildx --platform linux/amd64,linux/arm64 --metadata-file … --output type=oci,dest=…,tar=false`
- Layout allowlist: `oci-layout`, `index.json`, `blobs/sha256/<referenced digests>`
  (Buildx may emit a transient `ingest/` directory; the candidate script removes it
  before validation)
- Descriptor-graph validation rejects empty indexes, missing amd64/arm64,
  symlinks, extra files, empty blobs, and digest mismatches
- Image digest source of truth: Buildx metadata prefers
  `containerimage.descriptor` (digest / mediaType / size), with careful fallback
  to `containerimage.digest`
- That digest is the **image index / image manifest blob** named by a descriptor
  in `index.json` `manifests[]`. It is **not** `sha256(index.json)` (layout
  entrypoint file digest). `validate-oci` binds `--image-digest` to exactly one
  `manifests[]` descriptor, then checks blob presence, content SHA-256, size,
  and mediaType, and walks the descriptor graph for required platforms
- Host packaging asserts `image-identity.json`: `sourceCommitSha` ==
  `git rev-parse HEAD` / `SOURCE_SHA`, `mailerVersion` == `MAILER_VERSION`,
  platforms exactly `linux/amd64` + `linux/arm64` (order-insensitive)
- Dockerfile accepts `SOURCE_COMMIT` + `MAILER_VERSION` for publish props and labels
- Candidate builds use `--provenance=false --sbom=false` so the OCI index matches
  EXTERNAL_PROVENANCE (no embedded Buildx attestation manifests)
- This path **never** pushes to GHCR. #458 owns public image publish.

Workflow OCI artifact name: **`setup-release-candidate-oci`**
(`oci/` layout + `image-identity.json` + `buildx-metadata.json` +
`oci-index.digest`).

### #456 import notes (Windows Docker Desktop / Linux Engine)

Classic `docker load` accepts a single-platform image tarball and **cannot**
load a multi-platform OCI layout directory. Recommended paths:

1. **Preferred:** enable the Docker **containerd image store**, then import with
   `skopeo copy oci:./oci containers-storage:<repo>@<digest>` (or
   `nerdctl` / `ctr` against an OCI archive derived from the layout).
2. **Platform-specific daemon import:**
   `skopeo copy --override-os linux --override-arch amd64 oci:./oci docker-daemon:<repo>:<tag>`
   (repeat for `arm64` under test).
3. **crane / local registry:** `crane push ./oci <repo>@<digest>` then pull by
   digest — do **not** rebuild the candidate during qualification.

### #458 promote notes

Promote the qualified OCI graph and host archive bytes **without rebuild** when
possible. If attestations (provenance / SBOM) are re-added at publish time, the
public image index digest **may change** even when platform layers are
unchanged — record the promoted digest explicitly. A rebuild always produces a
**new** candidate.
## Version single source

```text
/p:Version=<release_version>
/p:InformationalVersion=<release_version>+<sourceSha>
```

`release_version` is **major.minor.patch only** (for example `1.2.0`, never
`1.2.0-candidate`). After publish, packaging asserts the binary informational
version core equals `release_version` and matching manifest fields.

## Reproducibility / #458 contract

Reproducible candidate means:

1. Same Git source commit SHA
2. Same Dockerfile base image digests
3. Same `dotnet publish` RID / AOT flags / version properties
4. Same OCI layout build inputs

→ Same OCI `containerimage.digest` and same staged `payloadTreeSha256`.

**#458 promotes qualified archive bytes.** A rebuild produces a **new**
candidate (new `archiveSha256` / provenance). Do not assume bit-identical
archive containers across rebuilds; compare payload tree hashes and promote the
qualified archive bytes that were smoked.

## Checksums

- Per-file: `FILES-SHA256SUMS` inside each staged tree
- Payload: `payloadTreeSha256` in the manifest (excludes manifest + checksums)
- Archive: `archiveSha256` in `CANDIDATE-SHA256SUMS` + provenance/handoff

## Secret scan / artifact smoke

Artifact smoke runs on an **extracted** archive on a matching OS/arch runner:

1. Verify archive SHA-256
2. Extract to a fresh temp directory
3. Verify `FILES-SHA256SUMS` inventory
4. Parse manifest
5. Assert binary version core
6. `--help`
7. `setup assistant --help` or `setup assistant-self-check`
8. Linux: verify executable bit after extract **without** `chmod`
9. Secret / `latest` structural scan

Cross-RID “binary present” is a **failure**, not a pass. Each RID job runs on a
matching runner so real exec is possible.

```bash
bash scripts/scan-setup-release-bundle.sh <extracted-rid-dir>
bash scripts/smoke-setup-release-bundle.sh <archive> <archiveSha256> <rid> <release_version>
```

Runtime Docker smoke (`scripts/release-smoke.sh`) remains separate.

## Generate via workflow

Dispatch `.github/workflows/generate-setup-release-candidate.yml` with:

| Input | Rule |
|-------|------|
| `release_version` | `major.minor.patch` only |
| `mailpit_image_ref` | required `repo@sha256:<64 lowercase hex>` |

Jobs: `build-oci` → `package-linux-x64` / `package-linux-arm64` /
`package-win-x64` → `assemble-handoff`.

Packaging CLI is **build-only**:

```text
dotnet run --project tools/Amane.Mailer.ReleaseBundle -- stage ...
```

The product `Amane.Mailer` binary does **not** offer `setup stage-release-bundle`.

## Handoff

Handoff package includes source SHA, workflow run id/attempt, artifact names,
archive filenames, `archiveSha256`, RID, versions, OCI index digest, platforms,
Mailpit digest, SDK/toolchain, and smoke results.

| Issue | Owns next |
|-------|-----------|
| [#456](https://github.com/kooiei-in4a/amane-mailer/issues/456) | Qualification / go-no-go |
| [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) | Tag, GHCR, GitHub Release, public checksums; promote qualified archive bytes |

## Explicit non-goals

- Git tag / GitHub Release creation
- GHCR push
- MSI / deb / rpm installers
- Auto updater
- macOS formal artifact
- NAS vendor package
- Setup UI inside the container

## Agent B implementation-review findings

| ID | Finding | Status |
|----|---------|--------|
| B1 | Multi-job candidate workflow + agreed artifacts | Addressed |
| B2 | Mailpit required on Mode-1 capable manifests | Addressed |
| B3 | Split payloadTreeSha256 vs archiveSha256; handoff evidence | Addressed |
| M1 | Artifact smoke on extracted archives (real exec) | Addressed |
| M2 | Version single source (`Version` + `InformationalVersion`) | Addressed |
| M3 | OCI allowlist as descriptor graph; OCI not inside host zips | Addressed |
| M4 | Packaging moved out of product CLI into tools project | Addressed |
| M5 | Evidence honesty (unit tests + workflow_dispatch E2E residual) | Addressed |
| m1 | LICENSE + manifest contract fields | Addressed |
| m2 | PR finding table mapped to Agent B IDs | Addressed |

## Agent B re-review findings

| ID | Finding | Status |
|----|---------|--------|
| B1 | Bind Buildx image digest to OCI layout `index.json` | Addressed |
| M1 | Workflow input shell-injection via `env:` | Addressed |
| M2 | Native AOT clang/zlib prerequisites on linux package jobs | Addressed |
| m1 | Mailpit parser rejects tag-before-digest; allows registry ports | Addressed |
| m2 | Additive `supportedReleaseManifestSchemaMin`/`Max` (packaging == 1) | Addressed |
