[日本語](release-artifact-verification.md)

# Release Artifact Verification

This runbook explains how OSS consumers can verify published Amane Mailer
release artifacts. Maintainer publishing steps live in
[GHCR image publishing](ghcr-image-publish.en.md) and
`.github/workflows/publish-contracts.yml`.

## What To Verify

For each release, verify:

- The GitHub Release tag and release record (`docs/releases/vX.Y.Z.md`) point to
  the same commit.
- The GHCR release tag and immutable `sha-<git-sha>` tag resolve to the same
  digest.
- The GHCR image OCI `source` / `revision` labels, and the `version` label for
  current-workflow releases, match the release record.
- The GHCR image index contains an SBOM / provenance attestation manifest.
- The NuGet package version, repository metadata, signature, and SourceLink /
  symbol package posture match the release record.

Public packages do not require GHCR or NuGet authentication for read-only
verification. If the GHCR package is private, run `docker login ghcr.io` with a
read-only token that has `read:packages`.

## GHCR Image Digest

Copy these values from `docs/releases/vX.Y.Z.md` or the GitHub Release notes:

- Image: `ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z`
- Digest / index digest
- Platform list
- Runtime manifest digest for each platform
- Attestation manifest digest for each platform
- Immutable tag: `ghcr.io/kooiei-in4a/amane-mailer:sha-<git-sha>`
- OCI `org.opencontainers.image.revision`
- OCI `org.opencontainers.image.version` when the release record lists it

Replace `vX.Y.Z` with the release you are verifying.

```bash
IMAGE_REPO=ghcr.io/kooiei-in4a/amane-mailer
IMAGE_TAG=vX.Y.Z
EXPECTED_INDEX_DIGEST=sha256:replace-with-release-image-digest
EXPECTED_REVISION=replace-with-release-commit-sha

IMAGE_REF="${IMAGE_REPO}:${IMAGE_TAG}"
SHA_REF="${IMAGE_REPO}:sha-${EXPECTED_REVISION}"
```

Inspect the release tag and immutable tag digests:

```bash
docker buildx imagetools inspect "$IMAGE_REF"
docker buildx imagetools inspect "$SHA_REF"
```

Both `Digest:` values must match `EXPECTED_INDEX_DIGEST`. Prefer digest pins over
tags for deployment:

```bash
TARGET_PLATFORM=linux/amd64
docker pull --platform "$TARGET_PLATFORM" "${IMAGE_REPO}@${EXPECTED_INDEX_DIGEST}"
```

## Runtime Manifest And OCI Labels

Verify the runtime manifest digest inside the image index for each platform.
Replace `TARGET_PLATFORM` and `EXPECTED_RUNTIME_MANIFEST_DIGEST` with the
per-platform values from the release record.

```bash
TARGET_PLATFORM=linux/amd64
TARGET_OS="${TARGET_PLATFORM%%/*}"
TARGET_ARCH="${TARGET_PLATFORM#*/}"
EXPECTED_RUNTIME_MANIFEST_DIGEST=sha256:replace-with-runtime-manifest-digest

docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | jq -r --arg os "$TARGET_OS" --arg arch "$TARGET_ARCH" '.manifests[]
    | select(.platform.os == $os and .platform.architecture == $arch)
    | .digest'
```

The output must match `EXPECTED_RUNTIME_MANIFEST_DIGEST`. If `jq` is not
available, inspect the target platform manifest line in
`docker buildx imagetools inspect "$IMAGE_REF"`.

Verify OCI labels from the pulled image:

```bash
docker pull --platform "$TARGET_PLATFORM" "$IMAGE_REF"

docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.source" }}'
docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}'
docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
```

Expected values:

- `org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer`
- `org.opencontainers.image.revision=<release commit sha>`
- `org.opencontainers.image.version=vX.Y.Z` for current-workflow releases. If an
  older release record explicitly marks the version label absent, follow that
  release record.

Informational labels such as `org.opencontainers.image.description` are recorded
for context. They are not part of consumer verification unless a release record
explicitly says otherwise.

## Provenance / Attestation / SBOM

The current image publishing workflow runs `docker/build-push-action` with:

- `provenance: true`
- `sbom: true`
- `platforms: linux/amd64,linux/arm64`

The SBOM and provenance are published as OCI attestation manifests attached to
the GHCR image index, not as standalone GitHub Release assets. Verify the
attestation manifest presence and digest for each runtime manifest digest:

```bash
EXPECTED_ATTESTATION_MANIFEST_DIGEST=sha256:replace-with-attestation-manifest-digest

docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | jq -r --arg runtime "$EXPECTED_RUNTIME_MANIFEST_DIGEST" '.manifests[]
    | select(.annotations["vnd.docker.reference.type"] == "attestation-manifest")
    | select(.annotations["vnd.docker.reference.digest"] == $runtime)
    | .digest'
```

One output digest must match the target platform's
`EXPECTED_ATTESTATION_MANIFEST_DIGEST`. If `jq` is not available, confirm that
an attestation manifest exists:

```bash
docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | grep -E '"vnd\.docker\.reference\.type"[[:space:]]*:[[:space:]]*"attestation-manifest"'
```

At this time, GitHub Releases do not attach separate `.spdx` / `.cdx` SBOM
files. Release records store the image index digest plus the runtime manifest
digest and attestation manifest digest for each platform.

## NuGet Package

`Amane.Mailer.Contracts` is published to nuget.org through Trusted Publishing
for GitHub Actions OIDC. The publish workflow verifies that the release tag,
package version, and project `<Version>` match.

Download the package and inspect repository metadata and signatures:

```bash
PACKAGE_ID=Amane.Mailer.Contracts
PACKAGE_ID_LOWER=amane.mailer.contracts
PACKAGE_VERSION=X.Y.Z
NUPKG="${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"

curl -L -o "$NUPKG" \
  "https://api.nuget.org/v3-flatcontainer/${PACKAGE_ID_LOWER}/${PACKAGE_VERSION}/${PACKAGE_ID_LOWER}.${PACKAGE_VERSION}.nupkg"

dotnet nuget verify "$NUPKG" --all

unzip -p "$NUPKG" "${PACKAGE_ID}.nuspec" | grep -E '<repository '
```

Do not use a package if `dotnet nuget verify --all` fails. This project does not
currently operate a project-specific author-signing certificate. For packages
downloaded from nuget.org, verify the nuget.org repository signature.

The `<repository>` metadata URL must be
`https://github.com/kooiei-in4a/amane-mailer`, and the commit must match the
release record.

## SourceLink / Symbols

The Contracts package uses these package settings for SourceLink and symbol
package generation:

- `Microsoft.SourceLink.GitHub`
- `PublishRepositoryUrl=true`
- `EmbedUntrackedSources=true`
- `IncludeSymbols=true`
- `SymbolPackageFormat=snupkg`
- `ContinuousIntegrationBuild=true` when packed in CI

Record symbol status for each release as four separate checks:

| Item | How to verify | What to record |
| --- | --- | --- |
| Generation | Check the publish workflow `Verify symbol package was produced` step and the `.snupkg` file name in the summary. | `.snupkg` file name and generation step result |
| Push | Check the `Push symbols to nuget.org` step. A `.nupkg` push may create the symbol package, after which the explicit `.snupkg` push can complete with `--skip-duplicate`. | push success, skip-duplicate, or failure |
| Availability | Download the `.snupkg` from the NuGet Gallery symbol package endpoint and confirm it contains the PDB. | endpoint, HTTP result, file size, PDB path |
| Indexing / debugging | Use NuGet Package Explorer or a Visual Studio / Rider debugger session to confirm SourceLink / symbol resolution. | verified / not verified and method used |

Check availability with:

```bash
PACKAGE_ID=Amane.Mailer.Contracts
PACKAGE_VERSION=X.Y.Z
SNUPKG="${PACKAGE_ID}.${PACKAGE_VERSION}.snupkg"

curl -fL -o "$SNUPKG" \
  "https://www.nuget.org/api/v2/symbolpackage/${PACKAGE_ID}/${PACKAGE_VERSION}"

unzip -l "$SNUPKG" | grep 'lib/net8.0/Amane.Mailer.Contracts.pdb'
```

`https://api.nuget.org/v3-flatcontainer/.../*.snupkg` can return 404 even when
the NuGet Gallery symbol package endpoint can return a `.snupkg`. Do not treat a
flat-container `.snupkg` 404 alone as the final symbol package result. Use the
NuGet Gallery symbol package endpoint, NuGet Package Explorer, or debugger
verification instead.

After NuGet indexing, confirm SourceLink resolution with NuGet Package Explorer
or a Visual Studio / Rider debugger session against the release commit on
GitHub. If indexing / debugging cannot be verified, record `not verified` and
keep that separate from `.snupkg` generation, push, and download availability.

At this time, the NuGet package publish does not emit a standalone SBOM file,
SLSA provenance attestation file, or project-specific author signature. NuGet
supply-chain evidence is verified through Trusted Publishing, repository
metadata, repository signature, and SourceLink / `.snupkg`.

## If Verification Fails

Treat these mismatches as release evidence issues:

- The release tag and immutable `sha-<git-sha>` tag have different digests.
- The OCI `revision` label does not match the release record commit.
- The release record lists an OCI `version` label, but the image label differs.
- The attestation manifest is missing or has a different digest than the release
  record.
- The NuGet package version, repository commit, or SourceLink commit differs
  from the release record.
- `dotnet nuget verify --all` fails.

If you find a mismatch, do not deploy that release artifact. Open a GitHub issue
with the release tag, artifact URL, and observed digest / metadata. Do not
include secrets, tokens, recipient email addresses, message bodies, or private
deploy paths in the report.
