# Public repository security summary

Consumer-facing summary of security verification for the public `amane-mailer`
repository. This document records value-free conclusions only. It does not
contain credential values, deploy secrets, or maintainer operational evidence.

Last reviewed: 2026-06-27 JST

## Repository secret scan

Status: reviewed on 2026-06-26 JST.

- Tool: `gitleaks 8.30.1` with repository `.gitleaks.toml`
- Public git history: no leaks found with project configuration
- Working tree: no leaks found with project configuration
- Default-configuration scan: two false positives from the documented
  local-only sample token `local-mail-service-token` in README curl examples
  only (`README.md`, `README.en.md`)

Conclusion: the public repository content and history contain no recorded real
credential values.

## Release artifact consistency

Status: verified on 2026-06-26 JST for `v0.1.0`.

- GHCR image `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`, immutable
  `sha-<git-sha>` tag, release record digest, runtime manifest digest,
  attestation manifest digest, and OCI source/revision labels are consistent.
  The initial `v0.1.0` image did not set an OCI version label.
- NuGet package `Amane.Mailer.Contracts` `0.1.0` SourceLink commit matches the
  release tag commit

Reference: [docs/releases/v0.1.0.md](../releases/v0.1.0.md)

Consumer verification procedure:
[docs/ops/release-artifact-verification.en.md](release-artifact-verification.en.md)

## Release image smoke

Status: passed on 2026-06-27 JST for `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`
(digest `sha256:b0e513663df2be1df6045b8bff39d1fcd93536cae98287b91f07cfc7b8700677`).

- Clean-state smoke via [`scripts/release-smoke.sh`](../../scripts/release-smoke.sh):
  8 checks passed, 0 failed (`/healthz`, `/readyz`, valid POST, Mailpit delivery,
  idempotent repost, conflict, invalid token, invalid `source_service`)
- Environment class: Windows 11 host, Docker Desktop, `linux/amd64` container runtime

Reference: [docs/releases/v0.1.0.md](../releases/v0.1.0.md) (full pass/fail table),
[release image smoke runbook](release-image-smoke.en.md),
[GitHub Release v0.1.0](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v0.1.0).

## NuGet publishing

Status: verified on 2026-06-26 JST.

- `Amane.Mailer.Contracts` is published through nuget.org Trusted Publishing
  for GitHub Actions OIDC
- No long-lived NuGet API key is stored in the public repository
- Symbol package (.snupkg) for `0.1.0`: availability not verified at release
  time. The publish workflow pushed `.snupkg` but did not confirm nuget.org
  acceptance. Starting from the next release, the workflow verifies `.snupkg`
  generation before push and records symbol status in the job summary.

## CodeQL disposition

| Rule | Disposition | Notes |
| --- | --- | --- |
| `cs/user-controlled-bypass` | False positive, dismissed | Request body size is enforced during read, not only from `Content-Length`. |
| `cs/log-forging` | True positive, fixed ([#48](https://github.com/kooiei-in4a/amane-mailer/issues/48)) | Admin audit logging sanitizes control characters before stdout output. |

## Scope

This summary is intended for OSS consumers. Maintainer-only verification
evidence is not published here.
