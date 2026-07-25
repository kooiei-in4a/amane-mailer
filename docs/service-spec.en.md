[日本語](service-spec.md)

# Amane Mailer Service — Service Specification (SQLite + Native AOT)

- **Role:** General-purpose mail delivery microservice
- **HTTP contract source of truth:** `src/Amane.Mailer.Contracts/` (ADR 0012 D-01)
- **Public HTTP reference:** [openapi.yaml](api/openapi.yaml) (public schema synchronized with Contracts / runtime)
- **Related:** [ADR 0012](adr/0012-mail-via-mailer-microservice.md) (Mailer microservice extraction)
- **Runtime:** Native AOT single binary (`Amane.Mailer`) + chiseled container. PostgreSQL is not used.

---

## 1. What This Service Does

> Receives mail assembled by the caller (App), **persists it → delivers asynchronously via ACS/Mailpit** as a "delivery-only" service.
> It does not hold templates; recipient, subject, and body are supplied by the caller in the payload.

```
App ──HTTP(Bearer)──▶ POST /internal/mail-requests
                          │  Accept · idempotency check · SQLite persist → 202
                          ▼
                     /app/data/mailer.db (source of truth for send requests)
                          │
                     Worker (same process) starts via Channel + Sweep
                          ▼
                  ┌── provider selection ──┐
        live_sending=false?                │
          ├ acs  → Azure Communication Services
          └ mailpit → Mailpit(SMTP)
```

- API, Worker, Retention, and Sweep **coexist in one process (one container)**.
- Connects to App via **HTTP API only**. No cross-references between App DB and Mailer DB.
- Only this service knows about ACS.
- Database is **SQLite (WAL mode)**. Persistence uses a host-side `./data` → container `/app/data` volume mount.

---

## 2. Interface (HTTP)

The code-level source of truth for the HTTP contract is `src/Amane.Mailer.Contracts/`. The Mailer runtime references the same DTOs / constants, and [openapi.yaml](api/openapi.yaml) is the Consumer-facing HTTP reference / public schema synchronized with Contracts / runtime. Summary:

| Method | Path | Purpose | Auth |
|---|---|---|---|
| `POST` | `/internal/mail-requests` | Accept send request (optional `scheduled_at`) | Tenant Bearer |
| `GET` | `/internal/mail-requests/{mail_request_id}` | Query delivery status (`tenant_id` / `source_service` as query params) | Tenant Bearer |
| `POST` | `/internal/mail-requests/{mail_request_id}/cancel` | Pre-send cancel (`queued`; already `cancelled` is idempotent) | Tenant Bearer |
| `POST` | `/internal/mail-requests/{mail_request_id}/reschedule` | Change schedule (`queued` and `attempt_count=0`) | Tenant Bearer |
| `GET` | `/healthz` | Liveness check | None |
| `GET` | `/readyz` | Readiness (current migration schema + Worker/Sweep running + heartbeat freshness; provider / ACS config checks are startup-only and not included) | None |
| `GET` | `/metrics` | Prometheus metrics (ops; see [metrics-and-alerts.en.md](ops/metrics-and-alerts.en.md)) | Development: optional bearer (internal NW assumed). Non-Development: bearer required when Enabled (startup-enforced) |

### Contract Sync and Drift Review

When changing the contract, review drift across `src/Amane.Mailer.Contracts/`, the runtime implementation, [openapi.yaml](api/openapi.yaml), and related tests in the same change. The review covers Request/Response DTO property names, required / nullable fields, `MailerErrorCodes`, `MailRequestAcceptanceStatus`, `MailRequestStatus`, payload hash fields, and JSON unknown / duplicate property behavior.

CI validates OpenAPI structure with `scripts/validate-openapi.mjs` and runs Contracts / runtime / OpenAPI drift-specific assertions with `scripts/check-contract-drift.mjs`. In addition, `scripts/check-mail-request-field-inventory.mjs` compares the `MailRequestCreateRequest` JSON field set across Contracts, OpenAPI, payload_hash classification, and Python / TypeScript SDK builders and validation. The drift check treats Contracts DTOs / constants as the source of truth and verifies OpenAPI schemas / enums, payload hash fields, runtime source-generated JSON usage, and runtime/test coverage hooks for JSON unknown / duplicate property behavior.

When intentionally changing the contract, update the DTOs / constants / payload hash contract in `src/Amane.Mailer.Contracts/` first, then synchronize runtime behavior, [openapi.yaml](api/openapi.yaml), related tests, and the Python / TypeScript SDK builder, validation, and payload_hash field inventories in the same change. There is no separate generated snapshot to refresh today; the drift check derives expected DTO / constant shape from source. Recompute the OpenAPI example `payload_hash` when it changes, and update `tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json` when canonicalization fixtures change. Validate locally with `node scripts/validate-openapi.mjs docs/api/openapi.yaml`, `node scripts/check-contract-drift.mjs`, and `node scripts/check-mail-request-field-inventory.mjs`. See the "Versioning Policy" section for Contracts package / API versioning policy.

### Acceptance Responses

| Situation | HTTP | code / status |
|---|---|---|
| First acceptance | 202 | `status: accepted` |
| Retry of same request | 202 | `status: already_accepted` |
| Invalid JSON / empty body / unknown property / duplicate property / invalid UTF-8 | 400 | `INVALID_REQUEST` |
| Token / tenant mismatch | 401 | `UNAUTHORIZED_TENANT` |
| source_service not allowed | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| Same ID, different content | 409 | `IDEMPOTENCY_CONFLICT` |
| Body > 256,000 byte | 413 | `REQUEST_TOO_LARGE` |
| Multiple recipients / metadata / hash mismatch | 422 | `TOO_MANY_RECIPIENTS` / `INVALID_METADATA` / `INVALID_PAYLOAD_HASH` / `INVALID_REQUEST` |
| Past `scheduled_at` / beyond max schedule horizon | 422 | `SCHEDULED_AT_IN_PAST` / `SCHEDULED_AT_TOO_FAR` |
| Transient DB failure (busy/locked, etc.) | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk full (SQLITE_FULL) | 503 | `STORAGE_FULL` (`retryable: false`) |

API times are **UTC**. `scheduled_at` is the first-dispatch schedule and is independent of `next_attempt_at` (retry backoff). Omit or null means immediate. Max schedule horizon is **30 days** from accept / reschedule time (`MailRequestScheduleLimits.MaxScheduledAhead`). `scheduled_at` is excluded from payload_hash.

### Delivery Status Query (GET)

`GET /internal/mail-requests/{mail_request_id}?tenant_id={uuid}&source_service={name}`

| Situation | HTTP | code / status |
|---|---|---|
| Existing request for authorized tenant + allowed source_service | 200 | Worker delivery `status` (`queued` / `processing` / `delivered` / `failed` / `dead_lettered` / `cancelled`) |
| Invalid or missing mail_request_id / tenant_id / source_service | 400 | `INVALID_REQUEST` |
| Token / tenant mismatch | 401 | `UNAUTHORIZED_TENANT` |
| source_service not allowed | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| Not found, or belongs to another tenant | 404 | `NOT_FOUND` (does not leak existence) |
| Transient DB failure (busy/locked, etc.) | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk full (SQLITE_FULL) | 503 | `STORAGE_FULL` (`retryable: false`) |

The response JSON is a PII-free minimal set (`mail_request_id`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `scheduled_at`, `accepted_at`, `delivered_at`, `last_error_code`). `last_error_code` is from the stable delivery taxonomy (`MailDeliveryErrorCodes` / `ProviderErrorClassifier`) only — not library exception type names. See Provider Error Sanitization in [SECURITY.md](../SECURITY.md).

### Pre-send cancel (POST cancel)

`POST /internal/mail-requests/{mail_request_id}/cancel?tenant_id={uuid}&source_service={name}`

| Situation | HTTP | code / status |
|---|---|---|
| Cancel a `queued` request | 200 | `status: cancelled` (status JSON) |
| Already `cancelled` (same-key re-cancel) | 200 | `status: cancelled` (idempotent) |
| Invalid query | 400 | `INVALID_REQUEST` |
| Token / tenant mismatch | 401 | `UNAUTHORIZED_TENANT` |
| source_service not allowed | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| Missing / other tenant | 404 | `NOT_FOUND` |
| Neither `queued` nor `cancelled` | 422 | `INVALID_STATE` |
| Transient DB failure (busy/locked, etc.) | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk full (SQLITE_FULL) | 503 | `STORAGE_FULL` (`retryable: false`) |

After a successful cancel DB update (`Cancelled` commit), transient webhook enqueue or status
re-read failures must not return an HTTP failure that implies the request was not cancelled.
Enqueue is best-effort; gaps may be filled by reconcile.

### Reschedule (POST reschedule)

`POST /internal/mail-requests/{mail_request_id}/reschedule?tenant_id={uuid}&source_service={name}`

Body: `{ "scheduled_at": "<UTC date-time>|null" }` (null clears the schedule gate = immediate).

| Situation | HTTP | code / status |
|---|---|---|
| Updated while `queued` and `attempt_count=0` | 200 | Updated status JSON |
| Invalid query / invalid JSON body / invalid UTF-8 | 400 | `INVALID_REQUEST` |
| Token / tenant mismatch | 401 | `UNAUTHORIZED_TENANT` |
| source_service not allowed | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| Missing / other tenant | 404 | `NOT_FOUND` |
| Body > 256,000 byte | 413 | `REQUEST_TOO_LARGE` |
| Past time / beyond 30 days | 422 | `SCHEDULED_AT_IN_PAST` / `SCHEDULED_AT_TOO_FAR` |
| Disallowed state | 422 | `INVALID_STATE` |
| Transient DB failure (busy/locked, etc.) | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk full (SQLITE_FULL) | 503 | `STORAGE_FULL` (`retryable: false`) |

After a successful reschedule DB update (`scheduled_at` commit), transient status
re-read failures must not return an HTTP failure that implies the request was not
rescheduled. The success response uses the committed snapshot obtained inside the
update transaction.

### Metadata secret policy (docs-first)

`metadata` validation inspects **key names only**; **values are not scanned** (docs-first policy).

| Check | Behavior |
|---|---|
| Key name | Keys containing `token`, `password`, `secret`, or `url` return 422 `INVALID_METADATA` |
| Value | Stored verbatim in `metadata_json` (no secret scrubbing) |
| Size | Exceeding tenant `metadata_max_bytes` (default 4096) returns 422 `INVALID_METADATA` |

Consumers must not place secrets, bearer tokens, passwords, or reset-link query secrets in metadata **values** even when the key name is allowed (for example `"link": "https://example.test/reset?token=..."` is accepted but unsafe). Accepted metadata is persisted in SQLite and backups and may appear in the Admin UI. Like `subject`, body fields, and `reply_to`, metadata may contain PII.

This policy is synchronized with OpenAPI, the Contracts README, and `SECURITY.md`. Value-level enforcement (for example URL query secret patterns) is out of scope for this policy.

### Idempotency

- Unique key is **`(tenant_id, source_service, mail_request_id)`**.
- Retries with the same key return 202 `already_accepted`; differing content (`payload_hash`) returns 409.
- `mail_request_id` is generated by the caller (UUIDv7 recommended).

### Delivery uniqueness (actual send guarantees)

HTTP acceptance idempotency (the "Idempotency" section above) and **actual email delivery uniqueness via ACS/Mailpit are separate contracts**. Consumers must not assume that the same `mail_request_id` results in exactly one delivered message (exactly-once).

Mailer delivery semantics are **at-least-once** (a single accepted request may result in multiple actual sends). The table below summarizes guarantees based on the current implementation. Canonical references are [ADR 0012 D-07](adr/0012-mail-via-mailer-microservice.md) and this section.

| Scope | Guarantee | Notes |
|---|---|---|
| HTTP acceptance | at-most-once persistence | One row per `(tenant_id, source_service, mail_request_id)`. Re-POST returns `already_accepted` |
| Actual email delivery (overall) | **not exactly-once** / at-least-once | Duplicates are possible due to automatic retries, manual retry, and provider behavior |
| ACS (`provider=acs`) | **Mitigation only** via deterministic operation id (UUIDv5) | Derived from `tenant_id` + `source_service:mail_request_id` (`AcsOperationIdFactory`). This repository does not verify or guarantee ACS server-side deduplication |
| Mailpit (`provider=mailpit`) | **No idempotency** (best-effort) | Each retry may perform a new SMTP send (development / verification use). Disconnect failure after SMTP DATA acceptance alone does not schedule a retry (#275) |
| Worker automatic retry | at-least-once | Retryable failures return to `Queued` and are delivered again |
| Finalize race after lease expiry (#238) | **Resend suppression** | Successful provider sends are recorded in `mail_attempts`; reclaim skips the actual send and converges to `Delivered`. Finalize skips are observable via `mail_finalize_skipped_total` ([metrics runbook](ops/metrics-and-alerts.en.md)) |
| Wall-clock lease correction (#276) | **Absolute-time comparison** | Mail / webhook leases compare `lock_expires_at` from `TimeProvider.GetUtcNow()` to `@Now`. There is no monotonic clock. A large forward wall-clock jump can cause early reclaim / strict finalize fencing failure; a large backward jump can delay Processing / Delivering recovery (details in the next subsection) |
| Admin manual retry | **Intentional redelivery** | Moves `DeadLettered` / `Failed` back to `Queued` (resets `attempt_count` to 0). Prior-cycle Delivered evidence is not used for prior-success convergence (#268). May resend even when a provider send already succeeded but the row had not converged to `Delivered` ([ADR 0015](adr/0015-manual-retry-cancel-state-transitions.md) maintains at-least-once) |
| Delivery result webhook | **first-wins** (at most one event per mail-request generation) + `event_id` dedup for re-POSTs | **Separate contract from actual email delivery**. Only the first enqueued terminal state is notified. Admin manual retry that reaches a later terminal (e.g. `failed` → retry → `delivered`) does **not** send another webhook. Consumers must treat duplicate POSTs with the same `event_id` as idempotent ([webhook-verification.md](consumer/webhook-verification.md)) |

### Worker / Webhook lease and wall clock (#276)

Mail (`mail_requests`) and webhook (`delivery_events`) leases are **UTC absolute times** stored as `lock_expires_at` in SQLite. Worker / Sweep / reaper / finalize compare that value to `@Now` from the DI `TimeProvider` (default `TimeProvider.System`). There is no Stopwatch-based monotonic / relative lease.

| Correction | reclaim / reaper (`lock_expires_at <= @Now`) | strict finalize (`lock_expires_at > @Now`) |
|---|---|---|
| Large wall-clock **forward** jump | Lease may be treated as expired early; reclaim / reaper can run | Fencing may fail |
| Large wall-clock **backward** jump | Reclaim can lag real time; Processing / Delivering may linger | Can still succeed (lease still looks valid) |

Ordinary NTP slew is rarely enough to matter. Host step corrections and manual clock changes are the main risk.

**Current mitigations:**

- mail: #238 can still record Delivered evidence when strict fencing fails after a successful provider send, skip the actual send on reclaim, and converge to `Delivered`. This does not remove clock skew itself.
- webhook: There is no equivalent prior-success converge path. Early reclaim can re-POST HTTP delivery (consumers must rely on `event_id` idempotency). Webhook finalize fencing failures are observable via `mail_webhook_finalize_skipped_total` ([metrics runbook](ops/metrics-and-alerts.en.md)).

**Operations:** Prefer slew over large steps for OS / container time sync. A spike in `mail_finalize_skipped_total` can indicate mail fencing failures (including clock jumps). For webhook, watch `mail_webhook_finalize_skipped_total`; see the [metrics runbook](ops/metrics-and-alerts.en.md). A monotonic lease redesign remains a separate ADR candidate and is not the short-term direction of this spec.

**Consumer recommendations:**

- If duplicate notifications are unacceptable for business logic, deduplicate on the consumer side using `mail_request_id` or a custom correlation id.
- Observing `delivered` via `GET /internal/mail-requests/{mail_request_id}` does not rule out multiple messages already sent (especially with Mailpit or manual retry).
- After Admin manual retry, status GET and the webhook terminal may diverge (webhook is first-wins). Treat status GET as authoritative for the current mail-request state.

### Versioning Policy

The service release (GitHub Release tag), Docker image tag, `Amane.Mailer.Contracts` NuGet package, and OpenAPI `info.version` all use the same `X.Y.Z`. A single release keeps all four in sync.

| Artifact | Version format | Example |
|---|---|---|
| GitHub Release / Git tag | `vX.Y.Z` | `v0.1.0` |
| Docker image tag | `vX.Y.Z` (mutable) + `sha-<git-sha>` (immutable) | `v0.1.0`, `sha-abc1234` |
| NuGet package (`Amane.Mailer.Contracts`) | `X.Y.Z` | `0.1.0` |
| OpenAPI `info.version` | `X.Y.Z` | `0.1.0` |

Prefer the immutable `sha-<git-sha>` tag or digest for deployment. The `vX.Y.Z` tag serves as the human-readable release identifier.

Publish procedures: [docs/ops/ghcr-image-publish.en.md](ops/ghcr-image-publish.en.md), [`.github/workflows/publish-contracts.yml`](../.github/workflows/publish-contracts.yml)

**Contracts package target framework**

`Amane.Mailer.Contracts` targets `net8.0` for broader consumer compatibility. The Mailer runtime may target a newer framework such as `net10.0`, but release version (`X.Y.Z`) alignment and target framework are separate concerns. A newer runtime TFM does not require the Contracts package to move to the same TFM.

When raising the Contracts package TFM, document the change in CHANGELOG release notes with migration guidance. Changes that raise the minimum .NET version required by consumer applications are treated as breaking changes in the 0.x line and follow semver from 1.0.0 onward.

**0.x compatibility expectations**

Releases in the 0.x line are still stabilizing the public API and contract. Backward compatibility is not guaranteed, but breaking changes are documented in CHANGELOG release notes with migration guidance. Starting from 1.0.0, semver backward-compatibility guarantees apply.

---

## 3. Data Model (SQLite)

Canonical DDL: `src/Amane.Mailer/Data/Migrations/001_initial.sql`

### 3.1 `mail_requests` — Source of Truth for Send Requests

| Column | Type | Description |
|---|---|---|
| `id` | TEXT PK | Internal UUIDv7 |
| `tenant_id` | TEXT | Tenant UUID |
| `source_service` | TEXT | Calling service name |
| `mail_request_id` | TEXT | Caller-generated request ID |
| `purpose` | TEXT | Purpose label |
| `payload_json` | TEXT | Received JSON verbatim |
| `payload_hash` | TEXT | SHA-256 hex (64 characters) |
| `subject` / `html_body` / `text_body` / `reply_to` | TEXT | Delivery content |
| `recipient_email` / `recipient_display_name` | TEXT | Recipient (current API accepts one recipient) |
| `metadata_json` | TEXT NULL | Optional metadata |
| `status` | INTEGER | State (see table below) |
| `attempt_count` / `max_attempts` | INTEGER | Attempt counts |
| `next_attempt_at` | TEXT NULL | Next attempt time (UTC ISO8601). Retry backoff only |
| `scheduled_at` | TEXT NULL | First-dispatch schedule (UTC ISO8601). null = immediate. Independent of `next_attempt_at` |
| `lock_token` / `lock_expires_at` | TEXT NULL | Worker lease |
| `delivered_at` / `failed_at` / `completed_at` | TEXT NULL | Terminal timestamps |
| `accepted_at` / `created_at` / `updated_at` | TEXT | Audit timestamps |

**Unique constraint:** `UNIQUE (tenant_id, source_service, mail_request_id)`

**Partial indexes:**

- `idx_mail_requests_queued_due` — `status = 0` ordered by `scheduled_at`, `next_attempt_at`, `created_at`
- `idx_mail_requests_processing_expired` — `status = 1` ordered by `lock_expires_at`

### 3.2 `mail_attempts` — Send Attempt History

| Column | Type | Description |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | |
| `request_id` | TEXT FK → `mail_requests.id` | ON DELETE CASCADE |
| `attempt_number` | INTEGER | 1-based |
| `provider` | TEXT | `acs` / `mailpit` etc. |
| `status` | INTEGER | Terminal state (2/3/4 only) |
| `provider_message_id` | TEXT NULL | ACS operation id (UUIDv5 deterministic generation) |
| `error_code` / `error_message` | TEXT NULL | Failure details |
| `retryable` | INTEGER | 0/1 |
| `lock_token` | TEXT | Lease at attempt time |
| `started_at` / `completed_at` | TEXT | UTC ISO8601 |

### 3.3 `worker_heartbeats` — Worker/Sweep Liveness Signal

DDL: `src/Amane.Mailer/Data/Migrations/002_worker_heartbeats.sql`

| Column | Type | Description |
|---|---|---|
| `name` | TEXT PK | Service name (`worker` / `sweep`) |
| `last_heartbeat_at` | TEXT | Last heartbeat time (UTC ISO8601) |

Worker and Sweep BackgroundServices each UPSERT periodically. The CLI `healthcheck` and `GET /readyz` verify that the schema required by the current binary is ready (applied migration versions + checksums), and when Worker is enabled also validate the presence and freshness of both heartbeat rows. Freshness threshold is `Mailer__Healthcheck__MaxHeartbeatStalenessSeconds` (default 300 seconds). Docker HEALTHCHECK uses the CLI `healthcheck`. The `GET /readyz` HTTP response remains `{"ready":true|false}` only (200 / 503) and does not include failure details. Internally, a fixed primary reason is recorded via transition-only logs and `/metrics` gauges `mail_ready` / `mail_readiness_failure` (#330).

### 3.4 State Transitions (`mail_requests.status`)

The canonical state values and Worker automatic transitions are defined in [ADR 0015: Manual retry and cancel state transitions](adr/0015-manual-retry-cancel-state-transitions.md). The table below is a service-spec summary.

| Value | Name | Meaning |
|---|---|---|
| **0** | `Queued` | Accepted · awaiting delivery (claimable when both `scheduled_at` and `next_attempt_at` are due) |
| **1** | `Processing` | Worker holds lease · sending |
| **2** | `Delivered` | Delivery succeeded (terminal) |
| **3** | `Failed` | Non-retryable provider failure (terminal) |
| **4** | `DeadLettered` | Abandoned after max attempts etc. (terminal) |
| **5** | `Cancelled` | Operator manual cancel (terminal) |

**Worker automatic transitions:**

```
0 Queued ──claim──▶ 1 Processing ──success──▶ 2 Delivered
                         │
                         ├──retryable fail──▶ 0 Queued (next_attempt_at)
                         ├──terminal fail───▶ 3 Failed
                         └──max attempts────▶ 4 DeadLettered
```

On retryable failure the runtime returns to **`Queued` (0)** with `next_attempt_at` set — it does **not** move to `Failed` (3).

**Admin manual operations (ADR 0015 summary):**

| Operation | Allowed source states | Target state |
|---|---|---|
| Manual retry | `DeadLettered`, `Failed` | `Queued` (`attempt_count=0`, `next_attempt_at=NULL`) |
| Manual cancel | `Queued`, `Failed`, `DeadLettered`, expired `Processing` | `Cancelled` |

Manual operations are rejected from `Delivered`, `Processing` with a valid lock, and `Cancelled`. See ADR 0015 for race rules, audit events, and tenant authorization.

### 3.5 Delivery result webhooks (outbound)

With an optional `webhook` object in tenant JSON, Mailer enqueues a
`MailDeliveryEventPayload` into the `delivery_events` outbox when a request
reaches a terminal state (`delivered` / `failed` / `dead_lettered` /
`cancelled`). `WebhookDeliveryWorker` delivers an HMAC-signed HTTPS POST to the
Consumer.

- **first-wins:** At most one event per `(tenant_id, source_service, mail_request_id)`
  generation (`ON CONFLICT DO NOTHING`). Only the first enqueued terminal state remains.
  If Admin manual retry later reaches a different terminal (e.g. `failed` → `Queued` →
  `delivered`), Mailer does not insert a second event and does not update the existing
  one. Re-notifying the latest terminal is out of scope for this contract and would need
  a separate issue / ADR.
- Reconciliation only fills **missing** outbox rows for terminal requests; it never
  overwrites an existing event.
- Read the secret from the environment variable named by `webhook.secret_env`
  (never store plaintext secrets in tenant JSON).
- Payload excludes PII (recipient, subject, body, and related fields).
- Consumer deduplication contract uses `event_id` (stable across webhook retries for the
  same mail-request generation). After request retention, reusing the same `mail_request_id`
  idempotency key issues a new `event_id`.
- Failed deliveries use exponential backoff; exceeding the retry limit records a
  webhook Dead Letter.
- Lease fencing failures (`FinalizeAsync` returns false) are observable via
  `mail_webhook_finalize_skipped_total` and structured Warning logs. The delivery
  contract remains at-least-once, so consumers must keep deduplicating by `event_id`
  after a skip that may lead to a re-POST ([metrics runbook](ops/metrics-and-alerts.en.md)).
- During shutdown, `stoppingToken` stops new claims. In-flight deliveries wait up
  to `DeliveryTimeoutSeconds + FinalizeTimeoutSeconds` (same drain pattern as
  `MailRequestWorker`).
- SSRF controls: HTTPS required. Blocks IPv4 private / loopback / link-local /
  CGNAT / multicast / reserved, IPv4-mapped, IPv6 loopback / link-local /
  site-local / ULA / multicast / unspecified, deprecated IPv4-compatible IPv6
  (`::/96`, e.g. `::10.0.0.1`), and private (or otherwise blocked) IPv4
  embeddings under the NAT64 well-known prefix (`64:ff9b::/96`) and 6to4
  (`2002::/16`). Optional `allowed_host_suffixes`.
- Verification steps: [docs/consumer/webhook-verification.md](consumer/webhook-verification.md)
- OpenAPI schema: `MailDeliveryEventPayload`
- Admin / ops visibility: `/admin/webhook-dead-letters`, `db stats` webhook counts

---

## 4. Operations CLI (Native Binary)

Early branching on `argv` before Web host startup. Container `ENTRYPOINT` is `./Amane.Mailer` (`dotnet` not required).

| Subcommand | Purpose | Exit code |
|---|---|---|
| `healthcheck` | Current SQLite schema (applied migration version + checksum) + Worker/Sweep heartbeat freshness check (Docker `HEALTHCHECK`) | 0=healthy / 1=unhealthy |
| `db migrate` | Apply pending SQL migrations | 0=success |
| `db checkpoint` | Clean up `-wal` via `PRAGMA wal_checkpoint(TRUNCATE)` | 0=success |
| `db backup <absolute-path>` | Online SQLite backup (Backup API). Writes to a same-directory temp file, verifies it, then atomically replaces the destination. A mid-flight failure leaves any previous good backup intact. Prefer a timestamped path for retention | 0=success / 2=usage error |
| `db stats [--tenant-id <uuid>]` | Output `mail_requests` status counts, ready backlog, oldest queued age, stale processing, and dead-letter counts from SQLite as `key=value` | 0=success / 1=schema unavailable / 2=usage error |
| `db request-state --tenant-id <uuid> --source-service <name> --mail-request-id <uuid>` | Output one request's state, attempt count, and provider message id presence as `key=value` (does not expose secrets / recipient) | 0=success / 1=schema unavailable / 2=usage error |

### Migration Checksum Policy

`db migrate` stores the byte-level SHA-256 hex checksum for each SQL migration file in
`schema_migrations.checksum`. `schema_migrations` is runner-owned metadata, so the
runner adds and backfills the checksum column before applying normal numbered SQL
migrations instead of relying on a numbered migration for that metadata change.

- New databases record each applied migration's `version`, `applied_at`, and `checksum` in the same transaction.
- Run `db migrate` exclusively for a given database. Do not start multiple migration runners against the same DB at the same time.
- Existing databases without the checksum column, including `v0.1.0` databases, have the `checksum` column added by the first checksum-aware `db migrate`. That run backfills checksums for rows whose applied `version` matches the currently bundled migration files. Historical checksums did not exist before this point, so the first backfill anchors trust in the SQL bundled with that image.
- Later `db migrate` runs verify that every applied `version` still has a bundled SQL file and that the stored checksum matches the current file checksum. A missing file or checksum mismatch fails fast before applying pending migrations.
- Released SQL migration files are forward-only and must not be edited after release. Because the checksum is byte-level, reformatting, line-ending changes, and encoding / BOM changes can also cause checksum mismatches even when the SQL appears equivalent. Add a new numbered migration for schema changes. If a checksum mismatch occurs, restore the correct image / SQL file or choose a restore / rebuild path from backup.

**Examples (compose ops):**

```bash
docker compose --profile ops run --rm mailer-migrate          # db migrate
docker compose exec mailer ./Amane.Mailer db checkpoint
docker compose exec mailer ./Amane.Mailer db backup "/app/data/backups/mailer-$(date -u +%Y%m%dT%H%M%SZ).db"  # plaintext; use backup-mailer.sh in production. Fixed-path overwrite keeps the previous good backup on mid-flight failure, but prefer a timestamped path for retention
docker compose exec mailer ./Amane.Mailer db stats --tenant-id <tenant-uuid>
docker compose exec mailer ./Amane.Mailer db request-state --tenant-id <tenant-uuid> --source-service <source-service> --mail-request-id <request-uuid>
```

`db stats` accepts an optional `--tenant-id <uuid>` (all tenants when omitted) and
`--queued-stale-minutes` (default 30), `--failure-window-minutes` (default 60),
`--stale-processing-minutes` (default 30). Output is one key per line in
`key=value` format; host-monitor depends on the following keys.

| key | Meaning |
|---|---|
| `as_of_utc` | Aggregation reference time (UTC) |
| `tenant_id` | Target tenant UUID, or `all` |
| `status_queued` / `status_processing` / `status_delivered` / `status_failed` / `status_dead_lettered` / `status_cancelled` | Counts by `mail_requests.status` |
| `ready_backlog_count` | Count where `queued` and both `next_attempt_at` / `scheduled_at` are due (null or `<= now`) |
| `oldest_queued_age_seconds` | Seconds since oldest `updated_at` in ready backlog (0 if none) |
| `queued_stale_count` | Ready backlog items where `updated_at` is older than `--queued-stale-minutes` |
| `stale_processing_count` | `processing` items where `updated_at` is older than `--stale-processing-minutes` |
| `expired_processing_count` | `processing` items where `lock_expires_at <= now` (input for worker liveness monitoring) |
| `recent_failed_count` / `recent_dead_lettered_count` | Terminal failure counts within `--failure-window-minutes` |
| `failed_total` / `dead_lettered_total` / `terminal_total` | Cumulative terminal failure counts |
| `worker_heartbeat_age_seconds` | Seconds since Worker last heartbeat (`-1` if row missing) |
| `sweep_heartbeat_age_seconds` | Seconds since Sweep last heartbeat (`-1` if row missing) |
| `webhook_events_pending` / `webhook_events_dead_lettered` | Delivery-result webhook outbox counts |

`db request-state` is a read-only verification command for no-send / ACS deploy drills. Output keys:
`tenant_id`, `source_service`, `mail_request_id`, `found`, `status`,
`status_code`, `attempt_count`, `attempt_rows`, `last_provider`,
`last_attempt_status`, `last_attempt_status_code`,
`provider_message_id_present`, `last_error_code`. Actual recipient, provider message id
values, body, and metadata are not output.

---

## 5. Configuration

**Principle:** Secrets via environment variables; structure and policy via JSON. Priority: `env > JSON > defaults`.

### 5.1 Secrets (Environment Variables / `.env`)

| Variable | Purpose | Example / Notes |
|---|---|---|
| `ConnectionStrings__Mailer` | SQLite connection string | Default `Data Source=/app/data/mailer.db` (same when unset) |
| **`ACS_CONNECTION_STRING_FILE`** | **ACS connection string file** | **Canonical for Staging/Production deploy (`infra/deploy/compose.yml`). Points at the `acs_connection_string` file written by `admin provider register-acs`. When `MAILER_REQUIRE_ACS_SECRET_FILE=true`, there is no fallback to the bare env var** |
| `ACS_CONNECTION_STRING` | ACS connection string (environment variable) | For local Mailpit compose and the local ACS drill (`mail-05a-acs-drill.sh` compose override). Not referenced by Staging/Production `compose.yml` |
| `MAIL_SERVICE_TOKEN_*` | Tenant Bearer tokens | Specified by `token_env` in `tenants.json` |
| `MAILER_PROVIDER` | Global provider override (optional) | `acs` / `mailpit`. Unknown values **fail closed at startup** (not re-checked by `/readyz`) |
| `MAILER_TENANTS_PATH` | Location of tenants.json | e.g. `/app/config/mailer/tenants.json` |

### 5.2 Worker / Sweep / Retention (Environment Variables)

Numeric values use **strict validation** (#329). Unset keys keep defaults. Empty string, malformed numbers, zero/negative, and values above the max **fail startup** (no implicit clamp). Error messages include the setting key and allowed range; they never include secrets or connection strings. The same numeric rules apply in Development / Testing / Production. With Worker disabled, per-key `Load` range checks still apply (cross-field lease / healthcheck `Validate` remains Worker-enabled only).

| Variable | Default | Allowed range | Description |
|---|---|---|---|
| `Mailer__Worker__Enabled` | `true` | `true` / `false` | Enable Worker HostedServices |
| `Mailer__Worker__BatchClaimSize` | `4` | 1–100 | Claim limit per drain |
| `Mailer__Worker__MaxSendConcurrency` | `4` | 1–64 | Parallel send count |
| `Mailer__Worker__SendTimeoutSeconds` | `90` | 1–600 | Per-message send timeout. Raise `MAILER_STOP_GRACE_PERIOD` when increasing |
| `Mailer__Worker__LeaseDurationSeconds` | `120` | 1–86400 | Processing lease TTL. Must be `> ceil(BatchClaimSize / MaxSendConcurrency) * SendTimeoutSeconds + FinalizeTimeoutSeconds(10)` |
| `Mailer__Webhook__MaxAttempts` | `10` | 1–50 | Webhook delivery max attempts |
| `Mailer__Webhook__InitialDelaySeconds` | `10` | 1–86400 | Webhook retry initial delay (`<= MaxDelaySeconds`) |
| `Mailer__Webhook__MaxDelaySeconds` | `300` | 1–86400 | Webhook retry max delay |
| `Mailer__Webhook__BatchClaimSize` | `8` | 1–100 | Webhook claim batch size |
| `Mailer__Webhook__DeliveryTimeoutSeconds` | `30` | 1–600 | Webhook HTTP timeout |
| `Mailer__Webhook__LeaseDurationSeconds` | `60` | 1–86400 | Webhook lease TTL. Must be `> DeliveryTimeoutSeconds + FinalizeTimeoutSeconds(10)` |
| `Mailer__Sweep__IntervalSeconds` | `30` | 1–3600 | Stale sweep interval |
| `Mailer__Retention__Days` | `90` | 1–3650 | Terminal record retention days (purges `mail_requests` and matching `delivery_events` for the same idempotency key in one transaction) |
| `Mailer__Retention__SweepIntervalHours` | `24` | 1–168 | Retention purge cycle in hours when `SweepIntervalSeconds` is unset |
| `Mailer__Retention__SweepIntervalSeconds` | (unset) | 1–604800 when set | Optional; when set, overrides Hours (mainly for tests) |
| `Mailer__Retention__BatchSize` | `100` | 1–250 | Retention delete batch size (SQLite bind limit) |
| `Mailer__Healthcheck__MaxHeartbeatStalenessSeconds` | `300` | 1–86400 | Heartbeat stale threshold (seconds). When Worker enabled: `>= ceil(BatchClaimSize/MaxSendConcurrency) * SendTimeoutSeconds + FinalizeTimeoutSeconds + 30` and `> WorkerHeartbeatIntervalSeconds` and `> Sweep:IntervalSeconds` |
| `Mailer__Healthcheck__WorkerHeartbeatIntervalSeconds` | `60` | 1–3600 | Worker heartbeat update interval when idle (seconds). Sweep update interval follows `Mailer__Sweep__IntervalSeconds` |

On startup failure, check the process log exception for the key name and allowed range. The configured value and connection strings are not included in the message.

### 5.3 Structure & Policy (JSON / `tenants.json`)

Schema: [config/mailer/tenants.schema.json](../config/mailer/tenants.schema.json). Per tenant:

| Field | Meaning |
|---|---|
| `tenant_id` | Environment × product UUID |
| `name` | Display name |
| `source_services` | Allowed caller allowlist |
| `default_from` | Sender address (not overridable from App) |
| `token_env` | Environment variable name for Bearer token |
| `provider` | `acs` / `mailpit` |
| `live_sending` | Live send gate (fail-closed) |
| `metadata_max_bytes` | Metadata limit (default 4096) |
| `retry` | `max_attempts` / `initial_delay_seconds` / `max_delay_seconds` |

### 5.4 Live Send Gate (`live_sending`)

- Even with `provider=acs`, tenants with `live_sending=false` **do not send** — they fail with `LIVE_SENDING_DISABLED`.
- develop / staging should use `false` in principle; production only `true`.
- When any tenant has effective provider `acs` (including `MAILER_PROVIDER` override) and `live_sending=true`, a missing ACS connection string (`ACS_CONNECTION_STRING_FILE` / `ACS_CONNECTION_STRING`) causes **startup fail-closed**. Configurations with only `live_sending=false` do not require an ACS secret at startup (same policy as offline `scripts/validate-tenant-config.mjs`). Provider / ACS validation is startup-only and is not part of `/readyz`.

---

## 6. Deployment Layout

`infra/deploy/compose.yml` is the independent deployment unit. **Only `mailer` runs as a long-lived container** (no PostgreSQL).

| Element | Content |
|---|---|
| Image | `infra/docker/Dockerfile` — digest-pinned `sdk:10.0-noble-aot` build → digest-pinned `runtime-deps:10.0-noble-chiseled` runtime ([pinning policy](ops/container-image-pinning.en.md)) |
| Data | `./data:/app/data` (SQLite `mailer.db` + WAL) |
| Tenant config | Host-owned tenant JSON mounted read-only from `MAILER_TENANTS_HOST_PATH` to `MAILER_TENANTS_CONTAINER_PATH` (default `/app/config/mailer/tenants.json`) |
| Migrations | `profiles: ops` `mailer-migrate` (`db migrate`) |
| Health check | `HEALTHCHECK CMD ["/app/Amane.Mailer", "healthcheck"]` |
| HTTP | `ASPNETCORE_URLS=http://+:8080` |

**Bootstrap:**

```bash
mkdir -p data
docker compose --env-file .env -f compose.yml config --quiet
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml up -d mailer
```

**Backup (after PostgreSQL / pg_dump deprecation):**

`infra/deploy/backup-mailer.sh` performs SQLite backup → age encryption → rclone upload in one step.
See the [backup operations runbook](ops/backup-operations.en.md) for procedures.

---

## 7. Shutdown (Graceful Shutdown)

Operational sequence on SIGTERM:

1. Generic Host fires `ApplicationStopping`; Kestrel stops accepting new HTTP requests
2. `MailRequestWorker` stops new claims and does not start later semaphore-waiting send waves after `stoppingToken` cancels (including when `BatchClaimSize > MaxSendConcurrency`; unstarted Processing rows rely on lease reclaim). Only already-started in-flight sends wait up to `SendTimeoutSeconds + FinalizeTimeoutSeconds`. `WebhookDeliveryWorker` stops new claims and waits up to `DeliveryTimeoutSeconds + FinalizeTimeoutSeconds` for in-flight webhook deliveries. If the drain window expires with work still active, a warning is logged
3. After all HostedServices (Worker / Sweep / Retention, etc.) complete `StopAsync`, `MailerWalCheckpointShutdownService.StoppedAsync` runs `PRAGMA wal_checkpoint(TRUNCATE)`
4. Generic Host fires `ApplicationStopped`

WAL TRUNCATE is best-effort shutdown cleanup; delivery durability is guaranteed by SQLite WAL
itself. On checkpoint failure, an error log is emitted; if shutdown timeout interrupts, a
warning log is emitted.

Compose defaults to `stop_grace_period=120s`; app-side `HostOptions.ShutdownTimeout` is the larger of the mail and webhook drain requirements plus slack (15 seconds). With defaults, the mail side dominates (`SendTimeoutSeconds + 25 seconds` or more). HostedService `StopAsync` runs sequentially by default (`ServicesStopConcurrently=false`), so when both workers hold a max-duration in-flight op at SIGTERM the additive wait can exceed `max()`. Any truncation is absorbed by lease-expiry reclaim / idempotent convergence (#238); `max()` is therefore a per-side drain + slack host ceiling, not a concurrent-drain bound. When increasing `SendTimeoutSeconds` or webhook `DeliveryTimeoutSeconds`, also increase `MAILER_STOP_GRACE_PERIOD`.

---

## 8. Data Ownership

`/app/data/mailer.db` is the **source of truth for send requests** (recipient · subject · body = PII, send attempt history, ACS operation id).
Backups are taken via the **`db backup` CLI** from the same container. Retention automatically purges terminal `mail_requests` and their matching `delivery_events`.

---

## 9. Topics for Separate Repository Extraction

| ID | Topic | Current State / Direction |
|---|---|---|
| O-04 | HTTP contract source of truth | **`src/Amane.Mailer.Contracts/`** (ADR 0012 D-01) |
| O-02 | Contracts distribution | `Amane.Mailer.Contracts` NuGet. OpenAPI is the Consumer-facing HTTP reference |
| O-03 | source_service registration | tenants.json allowlist |
| O-06 | Multiple products × ACS | Currently one ACS connection per service |
| O-13 | `from` override | Not allowed |
| — | Contract versioning | Service release / Docker image / NuGet package / OpenAPI `info.version` all use the same `X.Y.Z`. See the "Versioning Policy" section for details |

---

## 10. Change History

| Date | Content |
|---|---|
| 2026-06-22 | Initial version. Derived the HTTP contract and configuration spec from implementation |
| 2026-06-23 | Followed the initial SQLite / Native AOT release shape: chiseled single container / CLI / Retention / state transition DDL |
| 2026-06-24 | Added Worker/Sweep heartbeat liveness: `worker_heartbeats` table, CLI heartbeat freshness check, `/readyz` Worker running check, `db stats` heartbeat age keys |
| 2026-06-27 | Added Versioning Policy section (#5). Fixed OpenAPI `info.version` to `0.1.0` to match release/package |
| 2026-06-27 | Prepared the `v0.1.1` patch release by updating the Contracts package and OpenAPI `info.version` to `0.1.1` |
| 2026-07-03 | Followed ADR 0015: `Cancelled` state, manual retry/cancel transitions, `Failed` definition fix |
| 2026-07-22 | Added Worker/Sweep heartbeat freshness check to `/readyz` (#241). Shares the same threshold as CLI healthcheck |
| 2026-07-22 | Added the "Delivery uniqueness (actual send guarantees)" section (#239). Consistent with #238 finalize evidence / reclaim convergence |
| 2026-07-22 | Documented `WebhookDeliveryWorker` shutdown drain (stop new claims + wait for in-flight) (#245) |
| 2026-07-23 | `/readyz` / CLI `healthcheck` require current migration version + checksum (#267) |
| 2026-07-23 | Consumer cancel is idempotent for already-cancelled; post-commit HTTP failures avoided (#269) |
| 2026-07-23 | Documented startup validation for effective provider / ACS live-sending; not part of `/readyz` (#272). Mailpit treats post-accept disconnect failure as success, not retry (#275) |
| 2026-07-23 | `MailRequestWorker` shutdown: later semaphore-waiting send waves do not start (#271) |
| 2026-07-24 | Documented delivery-result webhook first-wins (first terminal only; no re-notify after Admin manual retry) (#273) |
| 2026-07-24 | Documented that Worker / Webhook leases use wall-clock absolute time and described clock-jump effects (#276) |
| 2026-07-24 | Made webhook finalize fencing failures observable via `mail_webhook_finalize_skipped_total` (#328) |
