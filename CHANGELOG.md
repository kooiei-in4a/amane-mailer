# Changelog

All notable changes to Amane Mailer will be documented in this file.

This project follows semantic versioning while the public API is stabilizing.
During the 0.x series, breaking changes may still occur, but they will be
called out in release notes and migration guidance.

Service release versions, Docker image tags (`vX.Y.Z` + immutable `sha-<git-sha>`),
NuGet package versions (`Amane.Mailer.Contracts`), and OpenAPI `info.version` are
kept in sync under the same `X.Y.Z`. See the Versioning Policy section in
`docs/service-spec.md` for details.

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
