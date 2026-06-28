[日本語](ghcr-image-publish.md)

# GHCR image publishing

GitHub Actions will publish the Mailer runtime image to GitHub Container Registry.

Workflow:

- `.github/workflows/publish-image.yml`
- Trigger: manual `workflow_dispatch` (succeeds only from a release tag ref)

## Image

- `ghcr.io/<github-org>/amane-mailer`

The image name is derived from `${{ github.repository_owner }}` at publish time.

## Tags

- Run `workflow_dispatch` from the GitHub UI/API with a release tag such as
  `vX.Y.Z`.
- Pre-release tags such as `vX.Y.Z-rc.1` are allowed.
- The workflow has no inputs. The image version tag is derived from
  `GITHUB_REF_NAME`.
- The only pushed tags are `sha-<git-sha>` and the release tag, for example
  `v0.1.1`.
- If either `sha-<git-sha>` or the release tag already exists in GHCR, the
  workflow fails instead of overwriting it.
- Branch refs, malformed tags, or runs where the tag commit does not match the
  checked-out commit / workflow event commit fail before publishing.
- Deploy with the immutable `sha-<git-sha>` tag or the digest whenever possible.

## GitHub Actions permissions

The publish job uses:

- `contents: read`
- `packages: write`

No repository secret is needed for pushing images. The workflow uses `GITHUB_TOKEN`.

The workflow builds the Mailer image from `infra/docker/Dockerfile`. Release
build base images are digest-pinned and reviewed / verified through the
[container image pinning policy](container-image-pinning.en.md).

## Release publish

1. Create a `vX.Y.Z` tag on the release commit. The tag commit must contain this
   hardened workflow, so create the tag on a commit after this change is merged.
   If the Contracts package is published for the same release,
   `src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj` `<Version>` must
   match `X.Y.Z`.
2. Run `Publish Amane Mailer Image` from the release tag ref in GitHub Actions.
3. After the `release` environment approval, the workflow publishes
   `sha-<git-sha>` and `vX.Y.Z`.
4. Confirm that the image run, config-content check, digest / platform / OCI
   label checks, and attestation check pass.
5. Copy the digest and `sha-<git-sha>` from the workflow summary into the
   GitHub Release notes or release evidence. Use the
   [release notes checklist](release-notes-checklist.en.md) for the artifact
   and operational-constraint entries.

The existing `v0.1.0` image already has manual digest / provenance evidence. Do
not republish existing artifacts just because the workflow changed.

The runtime image includes only safe files from `config/mailer`:

- `tenants.example.json`
- `tenants.local-acs.json.example`
- `tenants.schema.json`

Deployment-specific tenant JSON is not baked into the image. Shared deployments
mount a host-owned tenant file with `MAILER_TENANTS_HOST_PATH` and
`MAILER_TENANTS_CONTAINER_PATH` from `infra/deploy/compose.yml`. A tenant JSON
change is therefore a config deploy, not an image rebuild.

## GitHub Environment

Image publishing and NuGet package publishing both use the GitHub Environment
`release`.

- Configure the `release` environment with a required reviewer.
- The environment deployment branch/tag policy should allow release tags, for
  example `v*`.
- Publish attempts from branch refs still fail inside the workflow tag
  validation.

## SBOM / provenance / digest

`docker/build-push-action` runs with:

- `provenance: true`
- `sbom: true`
- `platforms: linux/amd64`

Before publishing, the workflow verifies that neither the `sha-<git-sha>` tag
nor the release tag exists in GHCR. After publishing, it verifies that the build
action returned a non-empty digest, that both tag digests match the build digest,
and that `docker buildx imagetools inspect --raw` contains an attestation
manifest. It also validates OCI labels on the pulled image:

- `org.opencontainers.image.source`
- `org.opencontainers.image.revision`
- `org.opencontainers.image.version`

The digest, platform, OCI labels, and inspect output are written to the workflow
summary. Release notes are not updated automatically, so copy the summary digest
and `sha-<git-sha>` into the release record after publishing, and reflect the
artifact and operational-constraint items from the
[release notes checklist](release-notes-checklist.en.md) in the GitHub Release
notes.

Consumer verification steps for published artifacts live in
[release artifact verification](release-artifact-verification.en.md).

## Deploy host pull access

If GHCR packages are private, the deploy host must authenticate before `docker compose pull`.

Use a read-only personal access token with `read:packages` scope:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```
