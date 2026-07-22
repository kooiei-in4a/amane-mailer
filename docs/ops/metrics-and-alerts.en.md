[日本語](metrics-and-alerts.md)

# Prometheus metrics and alerts runbook

Amane Mailer's `/metrics` endpoint exposes queue backlog, delivery results, and
Worker heartbeat in Prometheus text format. Gauge series for queue / dead letter
/ heartbeat reuse the same `MailerDbStatsReader` aggregation as Admin
`/admin/ops` and CLI `db stats`. Counters and histograms are held in-memory for
the process lifetime.

## Endpoint

| Item | Value |
|------|-------|
| Path | `GET /metrics` |
| Content-Type | `text/plain; version=0.0.4; charset=utf-8` |
| Default | Enabled (`Mailer:Metrics:Enabled=true`) |
| Auth | None by default (assumes internal-network isolation). When `Mailer:Metrics:BearerToken` is set, `Authorization: Bearer <token>` is required |
| Disable | `Mailer:Metrics:Enabled=false` → **404** |
| DB not migrated | **503** |

### Configuration examples

```bash
# Optional: scrape bearer
export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# To disable
export Mailer__Metrics__Enabled=false
```

Publish the Mailer HTTP port on an **internal network only** (Compose / systemd).
Scrape from the same network or VPN. Like `/healthz` and `/readyz`, direct
internet exposure is not intended.

## Published metrics

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `mail_requests_accepted_total` | counter | none | Mail requests accepted since process start (resets on restart) |
| `mail_deliveries_total` | counter | `result`, `provider` | Completed attempts since process start. `result` is `delivered` / `failed` / `dead_lettered` |
| `mail_delivery_duration_seconds` | histogram | `provider` | Attempt duration in seconds since process start (resets on restart) |
| `mail_queue_ready_count` | gauge | none | Immediately deliverable queued count (all tenants) |
| `mail_queue_oldest_age_seconds` | gauge | none | Age of oldest `updated_at` in the ready backlog |
| `mail_retries_total` | counter | none | Retry attempts since process start (`attempt_number > 1` completed attempts) |
| `mail_finalize_skipped_total` | counter | none | Delivered finalize attempts where strict lease fencing (`lock_expires_at`) failed. Includes delayed completion under the same lock and superseded/terminal races |
| `mail_dead_letters_total` | gauge | none | Current dead_lettered request count |
| `mail_worker_heartbeat_age_seconds` | gauge | `component` | Heartbeat age for `worker` / `sweep`. No series when the row is missing |

**Forbidden labels (must not include):** `recipient_email`, `subject`,
`mail_request_id`, `tenant_id`, `source_service`

### Relationship to Admin / CLI

- **Gauges (queue / dead letter / heartbeat):** Same service-wide aggregation as
  CLI `db stats` (no tenant filter) and break-glass Admin ops.
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
```

Because `mail_deliveries_total` is an in-process counter, `rate()` can be briefly
unstable right after a Mailer restart. Prefer queue / heartbeat alerts as
primary signals and treat delivery rate as secondary. Use
`mail_finalize_skipped_total` to detect strict lease fencing failures; when it
increases, check for delivery evidence, Delivered convergence, and DeadLetter races.

## Security notes

- Do not expose `/metrics` directly to the public internet.
- Responses must not include recipient / subject / mail_request_id / tenant_id.
- Rotate bearer tokens in the same secret boundary as scrape config.
- Separate path from Admin UI (`/admin/ops`). Admin uses session auth + tenant
  scope; metrics are ops-oriented and service-wide.

## Local check

```bash
curl -fsS http://127.0.0.1:5280/metrics | head
```

With bearer:

```bash
curl -fsS -H "Authorization: Bearer replace-with-scrape-token" http://127.0.0.1:5280/metrics | head
```
