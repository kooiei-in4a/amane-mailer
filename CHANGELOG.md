# Changelog

All notable changes to Amane Mailer will be documented in this file.

This project follows semantic versioning while the public API is stabilizing.
During the 0.x series, breaking changes may still occur, but they will be
called out in release notes and migration guidance.

Service release versions, Docker image tags (`vX.Y.Z` + immutable `sha-<git-sha>`),
NuGet package versions (`Amane.Mailer.Contracts`), and OpenAPI `info.version` are
kept in sync under the same `X.Y.Z`. See the Versioning Policy section in
`docs/service-spec.md` for details.

## [Unreleased]

### Changed

- Map provider delivery failures to a stable `error_code` taxonomy
  (`MailDeliveryErrorCodes` / `ProviderErrorClassifier`) instead of library
  exception type names (#279). Unknown exceptions become `PROVIDER_UNKNOWN`
  with `retryable: false`, so the worker marks the request `Failed` on that
  attempt without further retries. Existing attempt rows are left unchanged.

## [0.9.1] - 2026-07-22

### Fixed

- Prevent duplicate provider send when finalize loses the processing lease, and
  persist delivered evidence even if a reaper already terminalized the request
  (#238).
- Fail `GET /readyz` when worker or sweep heartbeats are stale so orchestrators
  do not treat a hung background path as ready (#241).
- Classify SQLite `SQLITE_FULL` as `STORAGE_FULL` and document disk / retention
  recovery in the ops runbook (#244).
- Drain in-flight webhook deliveries on graceful shutdown so stop does not cut
  off mid-send (#245).

### Changed

- Split `MailRequestRepository` into focused store types for accept, claim,
  consumer mutations, and admin queries (#242).
- Align public release image defaults and smoke guidance on `v0.9.1`.
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `0.9.1`.

### Documentation

- Document delivery uniqueness guarantees in the service spec (#239).
- Introduce `docs/implementation-status.json` as the current-status manifest,
  with CI format validation (#250, #252).
- Refresh ADR 0013/0014/0015 implementation status notes (#240).
- Add regression coverage for manual cancel vs worker claim races (#243).
- Add v0.9.1 release evidence draft.

### Breaking / Migration

- No breaking public HTTP contract change. Existing POST acceptance behavior is
  unchanged.
- No manual database migration is required for this release.
- Operators should treat `STORAGE_FULL` as a disk / SQLite capacity incident and
  follow [sqlite-disk-and-retention](docs/ops/sqlite-disk-and-retention.md).

## [0.9.0] - 2026-07-22

### Added

- Tenant-scoped `GET /internal/mail-requests/{mail_request_id}` delivery status
  query (PII-free). Contracts `MailRequestStatusResponse` / `MailRequestStatus`
  (including `processing` and `cancelled`) synchronized with OpenAPI (#216).
- Prometheus `GET /metrics` for queue backlog, delivery results, retries, dead
  letters, and worker heartbeat. Ops runbook with scrape and alert examples
  (#217).
- Official TypeScript and Python Consumer SDKs under `sdk/` with payload hash
  alignment to Contracts test vectors, typed errors, and retryable 503 backoff
  (#218).
- Signed delivery-result webhooks for terminal states (`delivered` / `failed` /
  `dead_lettered` / `cancelled`): HMAC headers, outbox retries, Dead Letter,
  SSRF controls, and Consumer verification docs (#219).
- Scheduled send via optional `scheduled_at`, plus cancel and reschedule APIs.
  Independent from retry `next_attempt_at`; max horizon 30 days UTC; payload_hash
  excludes `scheduled_at` (#220).

### Changed

- Align public release image defaults and smoke guidance on `v0.9.0`.
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `0.9.0`.
- Version numbers intentionally advance from `0.4.0` to `0.9.0` for this feature
  bundle (no intermediate public releases).

### Documentation

- Sync Consumer and ops docs for status GET, metrics, SDKs, webhooks, and
  scheduled send (JA/EN service-spec, README, Contracts README, metrics EN
  runbook).
- Add v0.9.0 release evidence draft.

### Breaking / Migration

- Additive HTTP contract surface for consumers: status GET, cancel, reschedule,
  optional `scheduled_at` on create, and outbound webhook payload schema.
  Existing POST acceptance behavior is unchanged.
- Operators enabling webhooks must set tenant `webhook` config and the secret
  env named by `webhook.secret_env` (never store secrets in tenant JSON).
- DB migrations for delivery events and `scheduled_at` are applied via
  `db migrate` (idempotent; existing rows keep `scheduled_at` null = immediate).

## [0.4.0] - 2026-07-21

### Added

- `admin provider register-acs` / `admin provider check-acs-preflight` CLI commands
  (MAILER-ACS-INPUT-01): safe interactive registration of the ACS connection string
  (deploy-time secret file, never tenant JSON, never the DB) and a new tenant-independent
  platform-owned sender identity file (`platform-sender.json` / `platform-sender.schema.json`)
  for future System Admin platform-owned mail. Interactive-terminal-only input, exclusive
  cross-process locking from preflight through commit, and a two-phase write with rollback so a
  failure never leaves only one of the two files registered (#203).
- `mailer-acs-admin` Compose service (profile `acs-admin`) with dedicated read-write mounts for
  the ACS secret and platform-sender directories, separate from the read-only mount `mailer`
  uses and excluded entirely from `mailer-migrate` (#203).
- `scripts/pty-smoke-register-acs.py`: real-PTY smoke test (synthetic values only) that drives
  the built CLI through an actual pseudo-terminal to confirm success, re-run rejection,
  partial-state rejection, and that secret input is never echoed to the terminal — behavior a
  unit test with a fake console cannot verify (#203).

### Changed

- `infra/deploy/compose.yml` / `infra/deploy/.env.example`: Staging/Production no longer accept
  the bare `ACS_CONNECTION_STRING` environment variable. Only the file-based
  `ACS_CONNECTION_STRING_FILE` secret (written by `admin provider register-acs`) is wired. The
  local ACS drill (`infra/deploy/drills/mail-05a-acs-drill.sh`) is unaffected — it already
  injected `ACS_CONNECTION_STRING` through its own compose override, independent of this file
  (#203, #204).
- Docs / ops sync after MAILER-ACS-INPUT-01: `config/mailer/README` (ja/en), service-spec (ja/en),
  SECURITY, root README ops links, release-notes checklist, and register-acs runbooks now
  describe the file-secret Staging/Production path. `.dockerignore` and
  `scripts/check-dockerignore-secrets.mjs` exclude `infra/deploy/secrets/` and
  `infra/deploy/config/platform-sender/`. `scripts/validate-tenant-config.mjs` accepts
  `ACS_CONNECTION_STRING_FILE` as well as bare `ACS_CONNECTION_STRING` (#204).
- Align public release image defaults and smoke guidance on `v0.4.0`.
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `0.4.0`.
- Dependabot batch on `develop`: GitHub Actions, NuGet, and Docker base image digest updates
  (#207).

### Fixed

- `SecretFileWriter.DiscardPrepared` no longer silently swallows delete failures; surfaces
  `REJECTED_CLEANUP_FAILED` (#206).
- MAILER-ACS-INPUT-01 review fixes: temp-file leak on rollback failure, accurate
  `REJECTED_ROLLBACK_FAILED` code, doc comment accuracy (#205).

### Ops note (image UID / Docker verification)

- Empirically confirmed on published `ghcr.io/kooiei-in4a/amane-mailer:v0.3.0`:
  `Config.User=1654` (uid/gid `1654`). Documented in the register-acs runbook with the
  requirement to re-check per deployed tag.
- Docker Known-gap checks completed 2026-07-21 (Windows + WSL2): `docker compose config` against
  `infra/deploy/compose.yml` with disposable `.env` (image tag `v0.3.0`); mount boundary
  confirmed via compose config + `docker inspect` (`mailer` ACS `:ro`, `mailer-acs-admin`
  ACS + platform-sender read-write, `mailer-migrate` without those mounts); host dirs `1654:1654`
  mode `0700` on a Linux-native path; `check-acs-preflight` / interactive PTY `register-acs`
  `SUCCESS` using local branch image `amane-mailer:pr206-verify` (published `v0.3.0` predates
  the CLI). Synthetic secret files removed afterward; see register-acs runbook section 9 (#206).

### Documentation

- Add v0.4.0 release evidence draft with placeholders for tag, NuGet, GHCR, and
  public smoke artifacts.

### Breaking / Migration

- No breaking public HTTP contract change. The public
  `POST /internal/mail-requests` HTTP contract shape and acceptance semantics
  are unchanged.
- **Staging/Production operators** upgrading from v0.3.0 must register the ACS
  connection string with `admin provider register-acs` and use
  `ACS_CONNECTION_STRING_FILE` instead of bare `ACS_CONNECTION_STRING` in compose.
  Local development and the ACS drill compose override are unchanged.
- No manual database migration is required for this release.

## [0.3.0] - 2026-07-04

### Added

- Zero-Admin first-mail quickstart for a fresh local Mailpit setup (#145).
- Bash and PowerShell local first-mail smoke scripts for the first-success path
  (#146, #147).
- `payload_hash` request verifier and mismatch troubleshooting guidance for
  Consumer self-diagnosis (#148, #149).
- Tenant config / environment preflight and troubleshooting matrix for common
  startup misconfiguration paths (#150, #151).
- Runnable .NET, Python, and Node.js Consumer POST examples that compute
  `payload_hash` and handle accepted, idempotent retry, and conflict outcomes
  (#152, #153, #154).

### Changed

- Align public release image defaults and smoke guidance on `v0.3.0` (#173).
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `0.3.0` (#173).

### Documentation

- Update README links for first-mail, payload hash, tenant troubleshooting, and
  Consumer sample onboarding paths (#145-#155).
- Add v0.3.0 release evidence draft with placeholders for tag, NuGet, GHCR, and
  public smoke artifacts (#174).

### Breaking / Migration

- No breaking public HTTP contract change. The public
  `POST /internal/mail-requests` HTTP contract shape and acceptance semantics
  are unchanged.
- No manual database migration is required for this release.

## [0.2.0] - 2026-07-03

### Added

- Admin tenant-scope authorization for multi-user operators (#91).
- Admin manual retry and manual cancel for queued, processing, failed, and
  dead-lettered mail requests per ADR 0015 (#100, #101).
- `Cancelled` worker delivery status (internal DB value 5) with migration 007.
- `admin user` CLI for scoped and break-glass Admin user creation (#131).
- Release-critical gates on the publish-image workflow (#120).

### Security

- Exclude tenant secrets and backup paths from the Docker build context (#117).
- Mask subject and reply-to PII consistently in Admin masked mode (#118).
- Sync README, SECURITY, and Admin runbooks with current Admin security posture
  (#119).

### Changed

- Reduce `linux/arm64` Docker smoke frequency to `main` push and
  `workflow_dispatch` only.
- Update Dependabot GitHub Actions dependencies.
- Document `develop` branch protection policy.

### Documentation

- Clarify Admin tenant-scope operational boundaries (#121).
- Document metadata value secret policy in OpenAPI, Contracts README, and
  service spec (#122).
- Expand public release evidence and restore-verification guidance.

### Breaking / Migration

- **DB migration 007** expands `mail_requests.status` to allow `Cancelled` (5).
  Existing SQLite databases apply this automatically at startup. Operators
  upgrading from v0.1.1 should allow a normal restart; no manual SQL is
  required.
- **Admin-only behavior**: manual retry/cancel and tenant scope apply to the
  experimental Admin UI and CLI only. The public `POST /internal/mail-requests`
  HTTP contract shape and acceptance semantics are unchanged; OpenAPI
  `info.version` bumps with the service release.

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
