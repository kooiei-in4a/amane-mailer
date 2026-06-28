[日本語](container-image-pinning.md)

# Container Image Pinning Policy

This document defines the pinning policy for Mailer release-image build inputs
and local / smoke helper images.

## Policy

- The .NET SDK / runtime-deps base images in `infra/docker/Dockerfile` are pinned
  as `tag@sha256:<digest>`. The tag remains for readability and Dependabot
  update detection; the digest fixes the actual build input.
- The publish workflow builds `linux/amd64` and `linux/arm64`. The Dockerfile
  uses registry manifest-list digests, and the workflow selects the target
  platforms with `platforms: linux/amd64,linux/arm64`.
- Mailpit in `infra/docker/docker-compose.local.yml` and
  `infra/docker/docker-compose.release-smoke.yml` is a local-only helper that is
  not included in the production / release artifact. Its default intentionally
  remains `axllent/mailpit:latest` so routine local verification picks up
  Mailpit fixes naturally.
- To reproduce a specific Mailpit build, or when supply-chain review needs an
  explicit pin, override it with `MAILPIT_IMAGE=axllent/mailpit:<tag>` or
  `MAILPIT_IMAGE=axllent/mailpit@sha256:<digest>`.

The only intentional `latest` usage is the Mailpit helper. The Mailer release
image base images, published GHCR image, and deploy compose Mailer image do not
use `latest`.

## Digest Updates

Dependabot checks `/infra/docker` weekly through the `docker` ecosystem in
`.github/dependabot.yml`. Dockerfile digest update PRs are reviewed as normal
dependency updates and are not auto-merged.

For update PRs, verify:

1. The tag still uses the intended lineage, for example `10.0-noble-aot` or
   `10.0-noble-chiseled`. If the tag changes, treat it as a .NET / Ubuntu base
   change.
2. The old and new digests with `docker buildx imagetools inspect`, including
   that the `linux/amd64` and `linux/arm64` manifests used by the workflow
   exist.
3. Upstream .NET container image / Ubuntu / chiseled image notes, security
   updates, and breaking changes.
4. The Mailer image builds from `infra/docker/Dockerfile`, and
   `/app/Amane.Mailer --help` or the equivalent workflow image run passes.
5. Before release, the publish workflow digest / platform / OCI label /
   attestation gates pass, followed by the release image smoke.

Example:

```bash
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0-noble-aot
docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
docker build --file infra/docker/Dockerfile --tag amane-mailer:base-image-review .
docker run --rm amane-mailer:base-image-review --help
```

Mailpit `latest` is not treated as a Dependabot digest-update target because it
is covered by the local-helper policy. When a local / release-smoke failure may
be caused by Mailpit, record the digest with
`docker buildx imagetools inspect axllent/mailpit:latest`, then rerun with
`MAILPIT_IMAGE` pinned if needed.

## Release Evidence

Release evidence records the published GHCR image digest. For a release that
updates base image digests, leave the following in the PR or release notes:

- Dockerfile .NET SDK / runtime-deps tags and digests
- Digest update PR review result
- Docker build / image run / release smoke result
- Published image index digest, per-platform runtime manifest digests, and
  per-platform attestation manifest digests from the publish workflow summary
