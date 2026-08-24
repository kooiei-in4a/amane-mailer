# Changelog

All notable changes to Amane Mailer will be documented in this file.

This project follows [semantic versioning](https://semver.org/). During the 0.x
series, breaking changes could still occur and were called out in release notes
and migration guidance. Starting with **1.0.0**, semantic versioning
backward-compatibility guarantees apply to the public HTTP contract and the
`Amane.Mailer.Contracts` package.

Service release versions, Docker image tags (`vX.Y.Z` + immutable `sha-<git-sha>`),
NuGet package versions (`Amane.Mailer.Contracts`), and OpenAPI `info.version` are
kept in sync under the same `X.Y.Z`. See the Versioning Policy section in
`docs/service-spec.md` for details.

## [Unreleased]

## [1.3.4] - 2026-08-24

Patch release that restores one synchronized **full service release** identity
after the v1.3.2 and v1.3.3 OCI-only Release Engineering publications. Relative
to the v1.3.1 service source, there is no product feature, runtime semantic,
database migration, or public HTTP contract shape change.

### Changed

- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `1.3.4` for the synchronized full release.
- Establish `docs/agent-workflows/release.md` as the durable AI-oriented release
  runbook, including source freeze, collision guards, exactly-once publication,
  Human approval boundaries, recovery, and final cross-artifact verification.
- Harden the normal GHCR publication path with machine-readable publication and
  public-consumer verification evidence, plus a read-only verify-only recovery
  path for already-published images.
- Preserve the normal publication order `GHCR -> Git tag -> NuGet -> GitHub
  Release` so the current-main-coupled image workflow binds the release source
  before later immutable artifacts are created.

### Compatibility

- No product feature, runtime semantic, database migration, or public HTTP
  contract shape change relative to v1.3.1.
- The migration inventory remains `001` through `018`.
- `Amane.Mailer.Contracts` advances to `1.3.4` for release identity alignment;
  its public DTO / constant / payload-hash behavior is unchanged by this patch.

### Release history

- `1.3.2` and `1.3.3` were intentionally OCI-only Release Engineering
  publications. They did **not** advance the synchronized Contracts/OpenAPI/NuGet
  / Git-tag / GitHub-Release service identity.
- `1.3.4` resumes the repository versioning policy by publishing the applicable
  Git tag, GHCR image, Contracts NuGet package, OpenAPI version, CHANGELOG,
  release record, and GitHub Release from one frozen source identity.

## [1.3.3] - 2026-08-24

OCI-only Release Engineering publication used to benchmark the established
normal GHCR release path. It was not a synchronized full service release and did
not publish a `1.3.3` Contracts NuGet package, service Git tag, or GitHub Release.

### Release Engineering

- Published the `linux/amd64` GHCR image once from frozen `main` source
  `c4819ea495e2f11cc6440fe499437cfa03aa2e5f`.
- Completed primary public verification and machine-readable evidence generation
  without recovery; the publish workflow completed in 8m15s from dispatch.
- No `latest` tag, same-version republish, or existing-tag overwrite was used.

## [1.3.2] - 2026-08-23

OCI-only Release Engineering publication. It was not a synchronized full service
release and did not advance the Contracts/OpenAPI/NuGet/Git-tag/GitHub-Release
service identity beyond `1.3.1`.

### Release Engineering

- Exercised the hardened GHCR publication/public-verification path.
- Completed read-only verify-only recovery verification for the existing public
  image without rebuilding or republishing the version.
- Kept same-version republish, existing-tag overwrite, and `latest` publication
  prohibited.

## [1.3.1] - 2026-08-22

Patch release focused on Release Engineering hardening. There is no product
feature, runtime semantics, database migration, or public HTTP contract delta.

### Changed

- Establish the #622 Shared Qualification Artifact Contract so Git and OCI
  promotion use the same production-shape preparation path.
- Require exact producer identity and exact artifact file-set validation, reject
  symlinks, and create a sealed-only byte-identical view without relaxing the
  existing strict sealed validator.
- Explicitly reuse the v1.3.0 / Issue #583 qualification authority as the
  patch-compatible authority for v1.3.1; the checked-in v1.3.0 scope authority
  remains unchanged.
- Evaluate Git promotion signature policy against the approved ruleset and policy
  fingerprint authority.
- Align the servicing baseline on .NET `10.0.11` and SDK `10.0.303`.

### Compatibility

- No product feature, runtime semantic, database migration, or public HTTP
  contract change. The migration inventory remains `001` through `018`.

## [1.3.0] - 2026-08-07

Minor release. Adds multiple `To` / `CC` / `BCC` recipients and bounded file
attachments while preserving existing single-To / attachment-free request
compatibility. Provider submission evidence now protects both plain and attachment
sends from unsafe automatic re-invocation when provider acceptance is ambiguous.
The release includes SQLite migrations 014-018. No public endpoint or existing
request field is removed or renamed.

### Added

- Multiple recipient roles for public mail requests: multiple `To`, `CC`, and
  `BCC`, including Cc-only, Bcc-only, and mixed-role requests, with role/aggregate
  limits, duplicate detection, canonical recipient persistence, and provider
  projection (ADR 0023).
- Bounded attachments with validated filename/type/digest/length, short-lived
  spool storage, provider mapping, submission evidence, and attachment-aware
  payload-hash canonicalization (ADR 0022).
- Canonical per-recipient delivery-state persistence and provider delivery-event
  correlation without storing raw BCC in recipient-event evidence.
- Python and TypeScript SDK support for `cc` / `bcc`, optional / `null` / empty
  `to`, recipient limits, and baseline + v1.3 payload-hash vectors (#542, #564).
- Admin BCC reveal capability with tenant/request/role/ordinal authorization,
  durable audit, no-store responses, and default-deny session capability handling.

### Changed

- Plain and attachment provider invocation now use durable submission evidence.
  Ambiguous provider acceptance converges to terminal `delivery_unknown` and is
  not automatically or manually resent solely because a lease expires or the
  process restarts.
- Provider send jobs are built from canonical `To` / `CC` / `BCC` recipients;
  SMTP includes BCC in the envelope but omits a MIME `Bcc` header.
- Suppression precheck covers every canonical recipient before provider
  invocation and fails the whole request without a provider call when any
  recipient is suppressed.
- Payload hash canonicalization projects recipient roles and attachment metadata
  while retaining byte-identical baseline behavior for the existing single-To,
  no-attachment vectors.
- Admin recipient display reads canonical recipient persistence; BCC stays masked
  by default and persisted diagnostic payloads redact recipient / attachment
  sensitive data.
- Native Docker build paths cover `linux/amd64` and `linux/arm64`; release-candidate
  packaging retains Native AOT host bundles for Windows and Linux.
- `Amane.Mailer.Contracts` package version and OpenAPI `info.version` align on
  `1.3.0`.

### Security

- BCC addresses are excluded from SMTP message headers, remain masked in normal
  Admin views, and use a fixed redacted legacy shadow for Bcc-only compatibility.
- BCC reveal is explicit, capability-gated, tenant-scoped, audited, HTML-encoded,
  and returned with no-store caching semantics.
- Attachment filenames/content and recipient addresses remain outside normal
  logs; submission evidence stores only the data required for delivery safety and
  correlation.

### Documentation

- Add pre-publication release record `docs/releases/v1.3.0.md` with explicit
  `PENDING` / `NOT YET PUBLISHED` public artifact identities and the exact
  candidate qualification gates.
- Published-image README / SECURITY / release-smoke defaults intentionally remain
  on `v1.2.0` until v1.3.0 is actually promoted and published.

### Breaking / Migration

- **Public HTTP compatibility**: the change is additive. Existing single-To
  requests remain valid. `to` may now be omitted, `null`, or empty when `cc` or
  `bcc` supplies at least one valid recipient; all three roles empty / omitted is
  invalid.
- **Status consumers**: `delivery_unknown` is a terminal public delivery state for
  an ambiguous provider invocation. Consumers with exhaustive status enums must
  handle it and must not infer that retrying the same delivery is safe.
- **Database**: v1.2.0 databases apply, in order,
  `014_mail_request_delivery_unknown_status.sql`,
  `015_attachment_spool_and_submission_evidence.sql`,
  `016_recipient_persistence_and_plain_submission_evidence.sql`,
  `017_recipient_delivery_events.sql`, and
  `018_admin_user_capabilities.sql`.
- **Upgrade**: take a verified pre-upgrade backup. `/readyz` remains fail-closed
  until the current migration set is applied with matching checksums. Reverse
  migration to v1.2.0 is not guaranteed; rollback across migrations 014-018 uses
  the pre-upgrade backup and a compatible runtime rather than an assumed database
  downgrade.

## [1.2.0] - 2026-08-03
Minor release. Ships Easy Setup (modes 1–4) for first-time Mailpit / ACS
configuration, keeps Manual / Hardened paths, and **INCLUDEs** SQLite migrations
`012_provider_event_inbox_details.sql` / `013_provider_queue_dead_letters.sql`
(bounce durability / poison queue isolation). Mode 5 remains Manual only.
Public HTTP request / response schemas and Contracts DTO shapes are unchanged;
package and OpenAPI `info.version` move to `1.2.0`.

Published identities (tag target `c173db1…`, OCI index digest, NuGet, GitHub
Release assets) are recorded in [docs/releases/v1.2.0.md](docs/releases/v1.2.0.md)
and <https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.2.0>. Publish
path: **P-OCI-PROMOTE** / `EXTERNAL_PROVENANCE` (no registry attestation
manifests). Qualification: #456 attempt 13 sealed / `GO_ELIGIBLE` + `APPROVE`.

### Added

- Easy Setup host assistant (Web + terminal / non-interactive) for modes 1–4
  under ADR 0021 (#446–#453, #459).
- Admin read-only `/admin/setup-status` and typed Admin bootstrap ownership
  (#454, #459).
- Setup release-candidate packaging workflow and bundle tooling (#455).
- Qualification evidence for Easy Setup release (#456 attempt 13 sealed).

### Changed

- `Amane.Mailer.Contracts` and OpenAPI `info.version` aligned on `1.2.0`.
- Release execution and publish for v1.2.0 (#458).
- Single setup entry docs retain Manual / Hardened routes (#457).

### Fixed

- Migration `012_provider_event_inbox_details.sql`: sanitized ACS delivery-report
  fields (`status_message`, `occurred_at`) on `provider_event_inbox` (#460).
- Migration `013_provider_queue_dead_letters.sql`: PII-free poison Storage Queue
  envelope isolation via `provider_queue_dead_letters` (#461).

### Security

- Easy Setup remains localhost-bound for the Web assistant; no Docker socket
  passthrough; no new public HTTP setup routes on the Mailer runtime (ADR 0021).
- Migration 013 intentionally omits raw queue body / recipient / provider raw
  error columns.

### Documentation

- Release record `docs/releases/v1.2.0.md` (published identities + dual-arch
  public smoke).
- Setup vs upgrade: Easy Setup targets fresh / managed setup; existing Manual
  deployments upgrade via normal image / migration apply — not a silent
  re-bootstrap of Admin.

## [1.1.0] - 2026-07-28

Minor release. Ships ACS Delivery Report bounce ingestion (Storage Queue Pull),
tenant suppressions, setup entry / diagnostic CLIs, and production deploy wiring
for mode 4 / mode 5. **Additive HTTP contract change:** consumers may observe the
new delivery error code `RECIPIENT_SUPPRESSED`; request schemas are unchanged.
Requires SQLite migration `011_bounce_ingestion.sql` (not present in 1.0.x).

### Added

- Bounce ingestion schema (migration 011): `provider_event_inbox`, `bounce_events`,
  and `mail_suppressions` (#301).
- Bounce ingestion worker: parse ACS Delivery Reports, correlate by
  `provider_message_id`, record bounce history, and upsert suppressions (#302).
- Send-time suppression: block suppressed recipients before provider send with
  `RECIPIENT_SUPPRESSED` and metric `mail_suppressed_sends_total` (#303).
- Azure Storage Queue Pull transport for ACS Delivery Reports
  (`MAILER_BOUNCE_INGESTION=queue`, queue name + file-secret connection) (#305).
- Admin bounce visibility on mail-request detail, suppressions list (view-only),
  and bounce ops runbooks (#306).
- Ops CLI `db suppressions remove` with audited physical removal (#400).
- Setup entry guide (JA/EN) with mode taxonomy and PASS/FAIL/WARN/ACTION
  semantics (#424).
- Read-only `setup doctor` preflight diagnostics (#425).
- Staging-only `admin provider test-acs-send` CLI (#426).
- Read-only `setup check-event-grid` for Event Grid → Storage Queue wiring (#427).
- Staging-only `setup verify-delivery-report` E2E (peek/correlate; no queue
  mutate) (#428).
- Production-confirmed `register-acs` so setup mode 4 (production ACS) is
  Available (#435).
- Deploy compose / `.env.example` bounce Queue wiring so mode 5 is Available
  (#436).

### Changed

- Contracts / OpenAPI: document additive delivery error code
  `RECIPIENT_SUPPRESSED` (#303).
- Event Grid Push webhook (`MAILER_BOUNCE_INGESTION=webhook`) remains out of
  scope for v1.1.0 (#304); startup rejects that mode.
- Consumer “bounced” notification remains deferred (ADR 0020 D-11).

### Fixed

- Admin session touch is monotonic and interval-throttled; cookie uses absolute
  lifetime; idle expiry is enforced from `admin_sessions` only (#391).
- Suppressions remove CLI: harden transaction / post-commit I/O and map DB
  open/BEGIN failures to exit 1 (#400).
- Admin suppressions: tenant-scoped audit, PII opt-in / capability gates, and
  unmasked nav with tenant selection (#306).
- Bounce queue poller retains mixed unparseable batches and avoids leaking
  provider error text (#305).
- Suppressed-send metric counted on finalize success, not enqueue (#303).

### Security

- Suppressions list defaults to masked PII with capability-gated unmask and
  tenant-scoped `mail_suppressions` audit (#306 / ADR 0013).
- Bounce and setup CLIs use redact-safe diagnostics; provider raw errors stay
  out of logs (#305, #425–#427).
- Admin idle lifetime is enforced server-side in DB rather than via cookie
  reissue on touch (#391).

### Documentation

- Bounce ingestion runbooks and metrics/alerts guidance (#306).
- Setup guide plus doctor / test-acs-send / check-event-grid /
  verify-delivery-report runbooks (#424–#428).
- `setup-entry-guide` marked `implemented` after modes 4 / 5 Available (#437).
- ADR 0020 (recorded as design in 1.0.1) is implemented for the v1.1.0 runtime
  slice.

## [1.0.1] - 2026-07-26

Patch release. Fixes four webhook-delivery defects that could stall notifications,
burn CPU, or make a committed admin operation look like a failure. No HTTP contract
change: `docs/api/openapi.yaml` schemas and `Amane.Mailer.Contracts` types are
unchanged, so upgrading requires no consumer action.

### Fixed

- Converge webhook delivery events whose lease expires on the final attempt into
  `DeadLettered` (#388). Such rows were unreachable — both the claim path and the
  pending-work query require `attempt_count < max_attempts` for the expired branch —
  so they stayed `Delivering` forever, never re-delivered and permanently counted in
  `mail_webhook_events_pending`.
- Isolate `WebhookDeliveryWorker` failures per event (#389). A malformed
  `payload_json` or an unclassified claim / deliver / finalize exception could fault
  the whole `BackgroundService` and stop all subsequent webhook delivery. Invalid
  JSON now converges to `WEBHOOK_PAYLOAD_INVALID` as a terminal failure.
- Stop the webhook worker's idle hot-spin and wake explicitly for scheduled retries
  (#402). The capacity-1 work signal was never drained, so once signalled the claim
  loop span on SQLite write transactions. The wait is now bounded by the configured
  initial retry delay, which also keeps a persistent database fault from repeating
  at 1 Hz.
- Keep a committed admin cancel from failing on its post-commit webhook enqueue
  (#390). After the cancel and its success audit had committed, an enqueue fault or
  a client disconnect still propagated out of the handler, so users saw `500` while
  the row was already `Cancelled`. Consumer and admin cancel now share one
  post-commit helper that takes no caller token; missing events are recreated by the
  existing reconciliation.

### Security

- Keep unclassified exception text out of webhook worker logs (#389). Logging
  providers render exceptions via `ToString()`, so a deliver-stage exception could
  carry the webhook URL or payload fragments into logs. The exception object now
  reaches the logger only for SQLite faults; everything else records the exception
  type name. The same rule applies to the new post-commit enqueue warning (#390).

### Documentation

- Add [ADR 0020](docs/adr/0020-bounce-ingestion-and-suppression.md): bounce ingestion
  and suppression design for v1.1.0 (#300). Records the transport decision, the
  correlation key, and the PII handling for provider delivery reports. No runtime
  behavior in this release depends on it; the feature is registered as `planned` in
  `docs/implementation-status.json`.

## [1.0.0] - 2026-07-25

First stable release. Declares the public HTTP contract and Contracts package
stable under semver. This release packages the post-v0.9.2 hardening and
stabilization work already on `develop` (no additional feature wait).

### Added

- Centralize Mailer startup options validation through a shared catalog and
  host validator (#351).
- CI field-inventory gate for `MailRequestCreateRequest` drift across Contracts /
  OpenAPI / SDKs (#352).
- CI formatter and staged analyzer quality gates (#359).
- Python and TypeScript SDK builders gain `scheduled_at` support aligned with
  calendar / `Z` validation (#346).
- ADR 0016–0019: stronger internal typing scope, webhook first-wins delivery
  cycle, sequential webhook concurrency deferral, and SQLite single-process
  boundaries (#360, #362, #361, #363).

### Changed

- Reject invalid UTF-8 request bodies with `400 INVALID_REQUEST` instead of
  replacement characters (#343). Documented in OpenAPI for create / reschedule.
- Unify strict boolean and port parsing for configuration (#358).
- Validate Mailpit SMTP host/port at startup when Mailpit is in use (#356).
- Rename webhook reconcile search size to `Mailer:Webhook:ReconcileBatchSize`
  (#353). Legacy `Mailer:Webhook:BatchClaimSize` remains a deprecated alias
  (new key wins; warning logged).
- Require tenant scope on Admin mail attempt listing (#357).
- Reject Admin `ALLOW_HTTP` outside Development (#341).
- Await Admin credential sync before Admin route mapping (#350).
- Split Admin extensions and Mail request endpoints into focused modules
  (#349, #348).
- Replace Worker `InflightTracker` polling with an async signal (#354).
- Propagate Ctrl+C cancellation to long-running CLI commands (#347).
- Judge webhook delivery success from response headers without buffering the
  body (#370).
- Classify schema probe failures separately from `schema_not_ready` (#342).
- Dispose SQLite connections on open / PRAGMA init failure (#344).
- Align public release image defaults and smoke guidance on `v1.0.0`.
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `1.0.0`.

### Fixed

- Register webhook `HttpClient` when the worker is disabled but Admin needs it
  (#341).

### Documentation

- Document webhook first-wins and deferred delivery-cycle extension (#362).
- Document deferred webhook delivery concurrency pending HOL measurement (#361).
- Document SQLite / single-process start gates for any future PG / Worker split
  (#363).
- Add v1.0.0 release evidence draft.

### Breaking / Migration

- **Public HTTP**: Invalid UTF-8 bodies are rejected with `400` (`INVALID_REQUEST`).
  Well-formed UTF-8 JSON clients are unaffected. No new endpoints; no DTO field
  removals in `Amane.Mailer.Contracts`.
- **Operators**: Prefer `Mailer__Webhook__ReconcileBatchSize`. Legacy
  `Mailer__Webhook__BatchClaimSize` still works as a deprecated alias.
- **Operators**: Strict boolean / port / Mailpit SMTP misconfiguration fails
  startup instead of silent fallback when those settings are in use.
- **Admin**: `ALLOW_HTTP` is rejected outside Development. Mail attempt listing
  requires tenant scope.
- **Database**: No new SQL migration in this release. Databases already on
  migration 010 (from v0.9.2) need no manual SQL.
- **Follow-ups (1.0.x, not blockers)**: webhook max-attempt DeadLetter
  convergence (#388), per-event webhook failure isolation (#389), Admin cancel
  webhook enqueue isolation (#390).

## [0.9.2] - 2026-07-24

### Added

- Record a fixed primary `/readyz` failure reason via transition-only logs and
  Prometheus gauges `mail_ready` / `mail_readiness_failure` without changing the
  public HTTP contract (`200`/`503` + `{"ready":...}` only) (#330).

### Changed

- Reject Worker / Webhook / Sweep / Retention / Healthcheck operational
  misconfiguration at startup instead of silently clamping with `Math.Max` /
  `Math.Clamp` (#329). Unset keys keep existing defaults; empty string,
  non-integer, zero/negative, and over-max values fail fast with the setting
  key and allowed range (no secrets). Cross-field lease and healthcheck
  checks are unchanged when Worker is enabled. Admin audit retention days,
  sweep interval (hours/seconds), and batch size use the same strict range
  parsing, including empty-string rejection.
- Require `Mailer:Metrics:BearerToken` (or `MAILER_METRICS_BEARER_TOKEN`) at
  startup when metrics stay enabled outside Development (#283). Development /
  local keep optional bearer under internal-network isolation; disable metrics
  with `Mailer:Metrics:Enabled=false` when scrape is unused.
- Include structured `RequestId` (internal id) and `TenantId` on Worker and
  ExpiredProcessingReaper request logs so the same `mail_request_id` can be
  distinguished across tenants without logging mail-payload PII (#285).
- Enforce Admin PBKDF2 password-hash parameter bounds (iterations
  600,000–10,000,000; salt 16–64 bytes; hash 32–64 bytes) at startup and
  `admin user create`, rejecting legacy weaker hashes (#281). Regenerate with
  `admin hash-password` if an older hash is rejected.
- Parse Admin boolean and positive-integer environment variables strictly so
  typos fail at options `Load` / startup instead of silently falling back to
  defaults (#280). `AMANE_ADMIN_ENABLED` is always strict; other Admin UI
  settings are enforced only when Admin is enabled so mail delivery is not
  aborted by unused Admin typos. Audit retention numerics remain always-strict
  because the worker sweep reads them. Unset variables keep existing defaults.
- Map provider delivery failures to a stable `error_code` taxonomy
  (`MailDeliveryErrorCodes` / `ProviderErrorClassifier`) instead of library
  exception type names (#279). Unknown exceptions become `PROVIDER_UNKNOWN`
  with `retryable: false`, so the worker marks the request `Failed` on that
  attempt without further retries. Existing attempt rows are left unchanged.
- Align public release image defaults and smoke guidance on `v0.9.2`.
- Align `Amane.Mailer.Contracts` package version and OpenAPI `info.version` on
  `0.9.2`.

### Documentation

- Document `/readyz` internal readiness gauges, fixed failure reasons, and
  `MailNotReady` alert example (#330).
- Document Worker / Webhook / Sweep / Retention / Healthcheck strict numeric
  ranges and startup fail-fast troubleshooting (#329).
- Document Production metrics bearer startup enforcement and Development /
  local optional-bearer + internal-network policy (#283).
- Document Admin boolean (`true`/`false`) and positive-integer allowed values
  in the local Mailer Docker runbooks (#280).
- Clarify delivery-result webhook first-wins: only the first terminal state is
  notified; Admin manual retry that later reaches `delivered` does not enqueue a
  second webhook (#273).
- Add v0.9.2 release evidence draft.

### Breaking / Migration

- No breaking public HTTP contract change. Existing POST acceptance behavior is
  unchanged.
- **DB migration 010** adds nullable `admin_audit_events.tenant_id` (and index)
  so scoped Admin audit list/get can survive independent mail-request retention
  (#282). Existing SQLite databases apply this automatically via `db migrate` /
  normal startup; no manual SQL is required. Auth / session / db_ops audit rows
  remain `tenant_id` NULL (service-wide).
- Operators enabling metrics outside Development must set
  `Mailer:Metrics:BearerToken` (or `MAILER_METRICS_BEARER_TOKEN`), or disable
  metrics with `Mailer:Metrics:Enabled=false`.
- Operators with Admin enabled must ensure Admin boolean / positive-integer env
  values and PBKDF2 password-hash parameters are within the documented bounds;
  legacy weaker hashes are rejected at startup / `admin user create`.
- Worker / Webhook / Sweep / Retention / Healthcheck numeric misconfiguration
  now fails fast at startup instead of silent clamping.

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
