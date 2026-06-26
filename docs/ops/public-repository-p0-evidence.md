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
  `sha-<git-sha>` tag, release record digest, and OCI source/revision labels
  are consistent
- NuGet package `Amane.Mailer.Contracts` `0.1.0` SourceLink commit matches the
  release tag commit

Reference: [docs/releases/v0.1.0.md](../releases/v0.1.0.md)

## NuGet publishing

Status: verified on 2026-06-26 JST.

- `Amane.Mailer.Contracts` is published through nuget.org Trusted Publishing
  for GitHub Actions OIDC
- No long-lived NuGet API key is stored in the public repository

## CodeQL disposition

| Rule | Disposition | Notes |
| --- | --- | --- |
| `cs/user-controlled-bypass` | False positive, dismissed | Request body size is enforced during read, not only from `Content-Length`. |
| `cs/log-forging` | True positive, fixed ([#48](https://github.com/kooiei-in4a/amane-mailer/issues/48)) | Admin audit logging sanitizes control characters before stdout output. |

## Scope

This summary is intended for OSS consumers. Maintainer-only verification
evidence is not published here.
