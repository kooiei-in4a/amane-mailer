[日本語](metrics-and-alerts.md)

# Prometheus metrics and alerts runbook

Amane Mailer's `/metrics` endpoint exposes queue backlog, delivery results,
Webhook outbox backlog, and Worker heartbeat in Prometheus text format. Gauge
series for queue / dead letter / heartbeat reuse the same `MailerDbStatsReader`
aggregation as Admin `/admin/ops` and CLI `db stats`. Webhook pending /
dead-letter gauges reuse the same `DeliveryEventRepository.CountOperationalAsync`
aggregation. Counters and histograms are held in-memory for the process lifetime.

## Endpoint

| Item | Value |
|------|-------|
| Path | `GET /metrics` |
| Content-Type | `text/plain; version=0.0.4; charset=utf-8` |
| Default | Enabled (`Mailer:Metrics:Enabled=true`) |
| Auth | **Production / Staging (non-Development):** when `Mailer:Metrics:Enabled=true`, `Mailer:Metrics:BearerToken` (or `MAILER_METRICS_BEARER_TOKEN`) is **required at startup** (fail-closed if missing). Requests need `Authorization: Bearer <token>`.<br>**Development / local:** bearer is optional. When unset, anonymous scrape is allowed (**internal-network isolation assumed**). When set, Bearer is required |
| Disable | `Mailer:Metrics:Enabled=false` → **404** (no bearer required even outside Development) |
| DB not migrated | **503** |

### Configuration examples

```bash
# Development / local: optional (anonymous scrape OK on an internal network)
# export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# Production / Staging: required when Enabled=true (startup fails if unset)
export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# Disable when scrape is unused (no bearer needed outside Development)
export Mailer__Metrics__Enabled=false
```

Publish the Mailer HTTP port on an **internal network only** (Compose / systemd).
Scrape from the same network or VPN. Like `/healthz` and `/readyz`, direct
internet exposure is not intended. Staging/Production `infra/deploy/compose.yml`
passes `MAILER_METRICS_BEARER_TOKEN`. Leaving it empty while metrics stay enabled
prevents the process from starting.

`ASPNETCORE_ENVIRONMENT=Testing` is reserved for automated test hosts such as
WebApplicationFactory. Do not use it as a real deploy environment name (it keeps
the optional-bearer path).

## Published metrics

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `mail_requests_accepted_total` | counter | none | Mail requests accepted since process start (resets on restart) |
| `mail_deliveries_total` | counter | `result`, `provider` | Completed attempts since process start. `result` is `delivered` / `failed` / `dead_lettered` |
| `mail_delivery_duration_seconds` | histogram | `provider` | Attempt duration in seconds since process start (resets on restart) |
| `mail_queue_ready_count` | gauge | none | Immediately deliverable queued count (all tenants) |
| `mail_queue_oldest_age_seconds` | gauge | none | Age of oldest `updated_at` in the ready backlog |
| `mail_retries_total` | counter | none | Retry attempts since process start (`attempt_number > 1` completed attempts) |
| `mail_finalize_skipped_total` | counter | none | Delivered finalize attempts where strict lease fencing (`lock_expires_at`) failed. Includes delayed completion under the same lock and superseded/terminal races (**mail-request only**; webhook uses a separate counter) |
| `mail_webhook_finalize_skipped_total` | counter | none | Webhook `delivery_events` finalize attempts where strict lease fencing (`lock_expires_at` / lock token) failed. Includes normal delivery outcomes and terminal failure paths such as missing webhook config/secret or invalid payload |
| `mail_dead_letters_total` | gauge | none | Current dead_lettered request count |
| `mail_webhook_events_pending` | gauge | none | Webhook outbox pending / delivering count (same aggregation as CLI `webhook_events_pending`) |
| `mail_webhook_events_dead_lettered` | gauge | none | Webhook outbox dead_lettered count (same aggregation as CLI `webhook_events_dead_lettered`) |
| `mail_worker_heartbeat_age_seconds` | gauge | `component` | Heartbeat age for `worker` / `sweep`. No series when the row is missing |
| `mail_ready` | gauge | none | Last `/readyz` evaluation (1 ready, 0 not ready). No series until the first evaluation |
| `mail_readiness_failure` | gauge | `reason` | Primary failure reason from the last `/readyz`. Fixed values only (`schema_not_ready` / `worker_not_running` / `sweep_not_running` / `heartbeat_missing` / `heartbeat_stale` / `database_error` / `unexpected_error`). Only the active reason is 1; others are 0. All 0 when ready. No series until the first evaluation |
| `mail_bounce_events_total` | counter | none | Correlated bounce facts written to `bounce_events` since process start (resets on restart) |
| `mail_bounce_unmatched_total` | counter | none | Events that failed `provider_message_id` correlation since process start. Early signal of correlation design breakage |
| `mail_bounce_recipient_mismatch_total` | counter | none | Events discarded because event-declared recipient did not match DB recipient |
| `mail_suppressed_sends_total` | counter | none | Sends blocked by the pre-send suppression list since process start |
| `mail_provider_queue_poll_failed_total` | counter | none | ACS Storage Queue operational failures since process start (connect/receive/inbox insert/delete/dead-letter insert). Does not include payload corruption |
| `mail_provider_queue_payload_invalid_total` | counter | none | Decode/parse-invalid Queue messages since process start (includes sub-threshold redelivery) |
| `mail_provider_queue_poisoned_total` | counter | none | Poison envelopes newly recorded to `provider_queue_dead_letters` since process start |
| `mail_provider_events_pending` | gauge | none | `provider_event_inbox` pending / processing count (same as CLI `provider_events_pending`) |
| `mail_provider_events_dead_lettered` | gauge | none | `provider_event_inbox` dead_lettered count (same as CLI `provider_events_dead_lettered`) |

**Forbidden labels (must not include):** `recipient_email`, `subject`,
`mail_request_id`, `tenant_id`, `source_service`

### Relationship to Admin / CLI

- **Gauges (queue / dead letter / heartbeat):** Same service-wide aggregation as
  CLI `db stats` (no tenant filter) and break-glass Admin ops.
- **Gauges (webhook pending / dead-letter):** Same service-wide
  `CountOperationalAsync` aggregation as CLI `db stats` (no tenant filter) and
  Admin ops' **service-wide** webhook counts (not the tenant-scoped dead-letter
  count).
- **Counters / histograms:** Process-lifetime events only. Rows inserted directly
  into the DB are not included. After restart, counters and histograms start at 0.

## Prometheus scrape example

```yaml
scrape_configs:
  - job_name: amane-mailer
    scrape_interval: 30s
    metrics_path: /metrics
    static_configs:
      - targets:
          - mailer.internal:5280
    # When bearer is configured:
    # authorization:
    #   type: Bearer
    #   credentials: replace-with-scrape-token
```

## Suggested alert thresholds

```yaml
groups:
  - name: amane-mailer
    rules:
      - alert: MailNotReady
        expr: mail_ready == 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Mailer /readyz is not ready; see mail_readiness_failure for primary reason

      - alert: MailQueueOldestAgeHigh
        expr: mail_queue_oldest_age_seconds > 300
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready queue oldest item is older than 5 minutes

      - alert: MailWorkerHeartbeatStale
        expr: mail_worker_heartbeat_age_seconds{component="worker"} > 120
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Worker heartbeat is stale

      - alert: MailQueueReadyBacklogSpike
        expr: deriv(mail_queue_ready_count[10m]) > 10
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready backlog is growing quickly

      - alert: MailDeliveryFailureRateHigh
        expr: rate(mail_deliveries_total{result="failed"}[5m]) > 0.1
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Failed delivery attempt rate is elevated

      - alert: MailFinalizeSkipped
        expr: increase(mail_finalize_skipped_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Delivered finalize hit strict lease fencing failure (delayed complete or superseded/terminal race)

      - alert: MailWebhookFinalizeSkipped
        expr: increase(mail_webhook_finalize_skipped_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Webhook delivery-event finalize hit strict lease fencing failure (may re-POST; consumers must dedupe by event_id)

      - alert: MailWebhookBacklogHigh
        expr: mail_webhook_events_pending > 100
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Delivery-result webhook outbox backlog is elevated

      - alert: MailWebhookDeadLettersPresent
        expr: mail_webhook_events_dead_lettered > 0
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Delivery-result webhook outbox has dead-lettered events

      - alert: MailBounceUnmatchedRising
        expr: increase(mail_bounce_unmatched_total[30m]) > 5
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Bounce events failed provider_message_id correlation; check ACS Event Grid mapping and mail_attempts.provider_message_id

      - alert: MailProviderEventsPendingHigh
        expr: mail_provider_events_pending > 50
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Bounce provider-event inbox backlog is elevated

      - alert: MailProviderEventsDeadLettersPresent
        expr: mail_provider_events_dead_lettered > 0
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Bounce provider-event inbox has dead-lettered rows

      - alert: MailProviderQueuePollFailed
        expr: increase(mail_provider_queue_poll_failed_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: ACS Storage Queue bounce poll failed; check queue credentials and network
```

Because `mail_deliveries_total` is an in-process counter, `rate()` can be briefly
unstable right after a Mailer restart. Prefer queue / heartbeat / webhook backlog
alerts as primary signals and treat delivery rate as secondary. Bounce metrics
are covered in [bounce-ingestion-runbook.en.md](bounce-ingestion-runbook.en.md).
Use
`mail_finalize_skipped_total` for **mail-request** strict lease fencing failures;
when it increases, check for delivery evidence, Delivered convergence, and
DeadLetter races. Use `mail_webhook_finalize_skipped_total` for **webhook outbox**
finalize fencing failures. Webhook delivery remains at-least-once, so a skip can
be followed by another POST with the same `event_id`. When the counter rises,
inspect Warning logs for `EventId` / `TenantId` / `MailRequestId` /
`FinalizeOutcome` / `FinalizeSkipReason` and the webhook backlog, and confirm
consumers still deduplicate by `event_id`. Metrics and logs must not include raw
lock tokens, webhook URLs/secrets, payload bodies, or recipient PII. Webhook
backlog gauges help detect cases where mail delivery succeeds but consumer
notifications stall. Admin manual retry is an audited explicit action that
resets `attempt_count` and marks prior-cycle Delivered attempt evidence as
ineligible for worker prior-success convergence (#268). Requeueing a DeadLetter
that already has delivered attempt evidence therefore performs a real resend in
the new dispatch cycle. Same-cycle #238 prior-success convergence is unchanged.
Check Admin attempt history for `provider_message_id` before retrying.

## Worker lease and wall-clock jumps (#276)

Mail and webhook leases are judged with **wall-clock absolute times**
(`lock_expires_at` from `TimeProvider.GetUtcNow()`). There is no monotonic
clock. See the “Worker / Webhook lease と wall clock（#276）” section in
[service-spec](../service-spec.md) for the full premise.

| Correction | Effect | Observation / mitigation |
|---|---|---|
| Large forward step | Early reclaim / reaper; strict finalize fencing can fail | Candidate cause of rising `mail_finalize_skipped_total` / `mail_webhook_finalize_skipped_total`. Mail may suppress resend and converge via #238. Webhook has no equivalent prior-success path and may re-POST |
| Large backward step | Delayed reclaim while Processing / Delivering | When interpreting `expired_processing_count`, webhook backlog, or heartbeat age, also consider host clock anomalies |

Ordinary NTP slew is rarely enough to matter. Prefer slew over large steps on
hosts. A monotonic lease redesign remains a separate ADR candidate.

## Disk exhaustion, WAL, and retention

A rising `mail_queue_oldest_age_seconds` can indicate Worker stall or provider
outage, and can also be an early signal of SQLite disk exhaustion
(HTTP `STORAGE_FULL`). For diagnosis, remediation, and an additional critical
threshold example, see
[sqlite-disk-and-retention.en.md](sqlite-disk-and-retention.en.md).

## Large-DB metrics / retention cost (#288)

`MailerDbStatsReader` gauges are full-table aggregates over `mail_requests`. For
elapsed times, EXPLAIN notes, and scrape-interval guidance on large DBs, see
[large-db-query-measurement.en.md](large-db-query-measurement.en.md).

## Security notes

- Do not expose `/metrics` directly to the public internet. Internal-network
  isolation remains required even in Development.
- Outside Development the app enforces a scrape bearer at startup. That does not
  replace network isolation.
- Responses must not include recipient / subject / mail_request_id / tenant_id.
- Rotate bearer tokens in the same secret boundary as scrape config.
- Separate path from Admin UI (`/admin/ops`). Admin uses session auth + tenant
  scope; metrics are ops-oriented and service-wide.

## Local check

With `ASPNETCORE_ENVIRONMENT=Development` (for example `dotnet run`), bearer may
be unset:

```bash
curl -fsS http://127.0.0.1:5280/metrics | head
```

Local Docker compose uses `ASPNETCORE_ENVIRONMENT=Production` and defaults
`MAILER_METRICS_BEARER_TOKEN=local-metrics-scrape-token`:

```bash
curl -fsS -H "Authorization: Bearer local-metrics-scrape-token" http://127.0.0.1:5280/metrics | head
```

With a custom bearer:

```bash
curl -fsS -H "Authorization: Bearer replace-with-scrape-token" http://127.0.0.1:5280/metrics | head
```
