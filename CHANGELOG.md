# Changelog

All notable changes to Amane Mailer will be documented in this file.

This project follows semantic versioning while the public API is stabilizing.
During the 0.x series, breaking changes may still occur, but they will be
called out in release notes and migration guidance.

Service release versions, Docker image tags (`vX.Y.Z` + immutable `sha-<git-sha>`),
NuGet package versions (`Amane.Mailer.Contracts`), and OpenAPI `info.version` are
kept in sync under the same `X.Y.Z`. See the Versioning Policy section in
`docs/service-spec.md` for details.

## [0.1.1] - 2026-06-27

### Fixed

- Reject unknown and duplicate JSON properties on mail request payloads before
  accepting or authorizing the request.
- Sanitize provider errors before persistence, logs, and Admin UI display.
- Harden release publish workflows so release tags are validated and existing
  image tags are not overwritten.
- Make fresh local compose data directories writable on Linux/macOS through the
  compose `data-init` path.
- Stabilize release smoke readiness checks and record public smoke evidence.

### Security

- Strengthened provider-error and Admin audit-log sanitization paths to reduce
  secret, PII, and log-forging exposure.

### Documentation

- Documented that the published GHCR runtime image is currently `linux/amd64`
  only and that multi-arch support is tracked separately.
- Prepared `v0.1.1` release evidence so the digest, release tag commit, NuGet
  metadata, and clean-state published image smoke can be recorded after publish.

## [0.1.0] - 2026-06-25

### Added

- Initial open-source release of the Amane Mailer service.
- ASP.NET Core mail request API with tenant-scoped bearer authentication.
- SQLite-backed queue, worker, retry, dead-letter, retention, and health checks.
- Mailpit local delivery provider and Azure Communication Services delivery provider.
- Admin UI for mail request and dead-letter inspection.
- Native AOT-capable Docker image build.
- OpenAPI contract, service specification, ADRs, and operations runbooks.
- Contracts project with DTOs and payload hash helper.
- Local Docker compose workflow with Mailpit smoke testing.

### Security

- Secrets are expected to be supplied through environment variables or mounted
  deploy-time files, not committed to the repository.
- Public examples use placeholder tokens, example tenant IDs, and example email
  addresses only.
- Security reporting is handled through the repository security policy and
  GitHub private vulnerability reporting.
