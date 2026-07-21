[Japanese](metrics-and-alerts.md)

# Prometheus metrics and alerts runbook

The Mailer `/metrics` endpoint exposes queue backlog, delivery outcomes, and worker heartbeats in Prometheus text format. Gauges for queue, dead letters, and heartbeats come from the same `MailerDbStatsReader` used by Admin `/admin/ops` and CLI `db stats`. Counters and histograms are kept in-process since startup.

## Endpoint

| Item | Value |
|------|-------|
| Path | `GET /metrics` |
| Content-Type | `text/plain; version=0.0.4; charset=utf-8` |
| Default | Enabled (`Mailer:Metrics:Enabled=true`) |
| Auth | None by default (internal network isolation). When `Mailer:Metrics:BearerToken` is set, `Authorization: Bearer <token>` is required |
| Disabled | `Mailer:Metrics:Enabled=false` → **404** |
| DB not migrated | **503** |

### Configuration example

```bash
# Optional scrape bearer
export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# To disable
export Mailer__Metrics__Enabled=false
```

Publish the Mailer HTTP port on an **internal network only** and scrape from the same network or VPN. Like `/healthz` and `/readyz`, direct internet exposure is not intended.

## Exported metrics

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `mail_requests_accepted_total` | counter | none | Mail requests accepted since process start (resets on restart) |
| `mail_deliveries_total` | counter | `result`, `provider` | Completed attempts since process start. `result` is `delivered`, `failed`, or `dead_lettered` |
| `mail_delivery_duration_seconds` | histogram | `provider` | Attempt duration in seconds since process start (resets on restart) |
| `mail_queue_ready_count` | gauge | none | Requests ready for immediate delivery (all tenants) |
| `mail_queue_oldest_age_seconds` | gauge | none | Age in seconds of the oldest ready queued request |
| `mail_retries_total` | counter | none | Retry attempts since process start (`attempt_number > 1` on completed attempts) |
| `mail_dead_letters_total` | gauge | none | Current dead-lettered request count |
| `mail_worker_heartbeat_age_seconds` | gauge | `component` | Heartbeat age for `worker` or `sweep`. Series omitted when row missing |

**Forbidden labels:** `recipient_email`, `subject`, `mail_request_id`, `tenant_id`, `source_service`

### Relation to Admin / CLI

- **Gauges (queue / dead letter / heartbeat):** Same service-wide aggregation as CLI `db stats` without `--tenant-id` and break-glass Admin ops.
- **Counters / histogram:** Process lifetime events only. History inserted directly into the DB is not included. Counters and histograms restart from zero after Mailer restart.

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

## Recommended alert thresholds

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
```

Because `mail_deliveries_total` is an in-process counter, `rate()` can be briefly unstable right after a Mailer restart. Treat queue and heartbeat alerts as primary and delivery rate as secondary.

## Security notes

- Do not expose `/metrics` directly on the public internet.
- Response must not include recipient, subject, mail_request_id, or tenant_id.
- Rotate bearer tokens with the same secret-management boundary as scrape config.
- Admin UI (`/admin/ops`) is a separate path with session auth and tenant scope; metrics are service-wide ops data.

## Local check

```bash
curl -fsS http://127.0.0.1:5280/metrics | head
```

With bearer configured:

```bash
curl -fsS -H "Authorization: Bearer replace-with-scrape-token" http://127.0.0.1:5280/metrics | head
```
