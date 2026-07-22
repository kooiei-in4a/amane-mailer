[日本語](sqlite-disk-and-retention.md)

# SQLite disk / WAL / retention runbook

Diagnosis and remediation for SQLite disk exhaustion (`SQLITE_FULL`) and capacity
pressure from WAL growth or insufficient retention.
On the HTTP API this surfaces as `STORAGE_FULL` (503, `retryable: false`),
distinct from transient busy/locked `MAILER_TEMPORARILY_UNAVAILABLE`
(503, `retryable: true`).

For general scrape and alert setup see
[metrics-and-alerts.en.md](metrics-and-alerts.en.md).

## Symptoms

| Observation | Meaning |
|-------------|---------|
| Consumer API returns 503 / `STORAGE_FULL` / `retryable: false` | Accept/update path hit SQLITE_FULL; brief retries will not help |
| Worker / Sweep / Retention logs mention `SQLITE_FULL` | Background writers cannot persist |
| `mail_queue_oldest_age_seconds` stays high | Ready backlog is not draining; disk full is one possible cause among Worker/provider issues |
| Admin `/admin/ops` Database storage shows large DB / WAL | Capacity pressure warning |

## Diagnosis

1. **Check the HTTP error code**
   - `STORAGE_FULL` → investigate disk / volume / inodes / retention (this runbook)
   - `MAILER_TEMPORARILY_UNAVAILABLE` → short-lived busy/locked; backoff retry is appropriate
2. **Check free space** on the volume that holds the Mailer SQLite files
3. **Inspect DB / WAL size** via Admin ops or CLI
   - Admin: `/admin/ops` → Database storage
   - CLI: `db stats` alongside queue / dead letter / heartbeat
4. **Check metrics**
   - `mail_queue_oldest_age_seconds`
   - `mail_queue_ready_count`
   - `mail_worker_heartbeat_age_seconds{component="worker|sweep"}`
5. **Check logs** (never log recipient / subject / body / connection strings)
   - Worker / Sweep / Retention messages that include `SQLite storage full (SQLITE_FULL)`

## Remediation

In order:

1. **Free or expand volume capacity**
2. **Review and tighten retention if needed**
   - mail request retention: `Mailer:Retention:*` / related env
   - admin audit retention: `MAILER_ADMIN_AUDIT_RETENTION_DAYS` (default 180 days)
   - explicit purge: `db admin-audit purge --older-than-days <days>`
3. **Shrink WAL** during a maintenance window
   - Re-check size after process-stop checkpoint (`MailerWalCheckpointShutdownService`)
   - Preferred ops command: `db checkpoint` (runs `PRAGMA wal_checkpoint(TRUNCATE)` internally)
   - Manual `PRAGMA wal_checkpoint(TRUNCATE);` only under your backup/maintenance policy
4. **Confirm recovery**
   - `/readyz` returns 200
   - new accepts return 202 again
   - `mail_queue_oldest_age_seconds` starts declining

## Suggested alerts (early disk-pressure signal)

There is no dedicated disk-full gauge today. Use **oldest ready-queue age** as the
primary early signal.

```yaml
groups:
  - name: amane-mailer-storage
    rules:
      - alert: MailQueueOldestAgeHigh
        expr: mail_queue_oldest_age_seconds > 300
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready queue oldest item is older than 5 minutes
          description: >-
            May indicate Worker stall, provider outage, or SQLite disk pressure
            (STORAGE_FULL). Check volume free space, WAL size, and retention.

      - alert: MailQueueOldestAgeCritical
        expr: mail_queue_oldest_age_seconds > 900
        for: 10m
        labels:
          severity: critical
        annotations:
          summary: Ready queue oldest item is older than 15 minutes
          description: >-
            Prolonged backlog. Investigate disk/WAL/retention and Worker health
            before relying on Consumer retries.
```

Notes:

- `mail_queue_oldest_age_seconds` alone does not prove disk exhaustion. Correlate
  with `STORAGE_FULL` logs/HTTP codes and volume free space.
- Consumer SDKs auto-retry `retryable: true`. `STORAGE_FULL` is `retryable: false`,
  so accepts stay failing until disk pressure is cleared.

## Security

- Do not emit recipient / subject / body / metadata values / connection strings /
  tokens in logs, Admin, or metrics.
- Keep `/metrics` and Admin on an internal network boundary.
