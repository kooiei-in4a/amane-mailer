[日本語](bounce-ingestion-runbook.md)

# Bounce ingestion runbook (Pull / Storage Queue)

> Scope: ACS Email Delivery Report → Event Grid → Storage Queue → Mailer Pull ingestion (ADR 0020 / #305).
> Admin visibility: #306. Suppression removal CLI: #400. Push (Event Grid Webhook) is out of v1.1.0 scope (#304).

## 1. Purpose

Ingest hard bounces (ACS `status = Bounced`), register tenant-scoped
`mail_suppressions`, and stop further sends. Visibility is Admin UI + Prometheus
metrics only (Consumer `bounced` notification is deferred to v1.2.0+).

## 2. Adopted transport (Pull)

v1.1.0 uses **Storage Queue polling only**. No public HTTPS ingress is added.

| Setting | Example |
|---------|---------|
| Mode | `MAILER_BOUNCE_INGESTION=queue` (or `Mailer:BounceIngestion:Mode=queue`) |
| Connection string | `MAILER_BOUNCE_QUEUE_CONNECTION_STRING` or `MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE` |
| Queue name | `MAILER_BOUNCE_QUEUE_NAME` |
| Poll interval | `Mailer:BounceIngestion:Queue:PollIntervalSeconds` (default 30) |

Never log or expose the connection string or queue name in metrics.

### ACS / Event Grid setup notes

1. Subscribe ACS Email Delivery Reports via Event Grid.
2. Point Event Grid at a **Storage Queue** (not a Push webhook).
3. Separate ACS resources and queues per environment (dev / staging / production).
   Mixing environments can mis-correlate `provider_message_id` values.
4. Queue message bodies are raw JSON (not Base64).

## 3. Admin checks

| Page | Content |
|------|---------|
| `/admin/mail-requests/{id}` | Request detail "bounce history" from `bounce_events`. No FK — empty is valid (not ingested yet / purged). |
| `/admin/suppressions` | Tenant-scoped suppression list (view-only). Recipients are masked by default. Unmask requires explicit `MAILER_ADMIN_PII_LIST_MODE=visible`. In visible mode, viewing and audit are per-tenant; without a tenant selection the UI asks for one (single allowed tenant redirects automatically). |

Scoped admins see only their tenants. Break-glass may see all tenants.

When `MAILER_ADMIN_PII_LIST_MODE=visible`, unmasked suppressions list and its audit event require an explicit tenant. The side-nav link opens tenant selection (or redirects when exactly one tenant is allowed).

## 4. Metrics and backlog thresholds

See [metrics-and-alerts.en.md](metrics-and-alerts.en.md) for alert examples.

| Metric | Watch for |
|--------|-----------|
| `mail_bounce_events_total` | Ingestion progress |
| `mail_bounce_unmatched_total` | Rising correlation failures |
| `mail_bounce_recipient_mismatch_total` | Recipient mismatch discards |
| `mail_suppressed_sends_total` | Pre-send blocks |
| `mail_provider_events_pending` | Inbox backlog (example: >50 for 15m) |
| `mail_provider_events_dead_lettered` | Inbox dead letters (warn when >0) |
| `mail_provider_queue_poll_failed_total` | Queue poll failures |

Do not attach `tenant_id` / recipient labels (ADR 0020 D-10).

CLI `db stats` `provider_events_pending` / `provider_events_dead_lettered` use the
same inbox aggregation.

## 5. Triage when unmatched spikes

1. Confirm `increase(mail_bounce_unmatched_total[30m])` is rising.
2. Confirm ACS → Event Grid → Queue subscription targets the correct environment.
3. Confirm send-side `mail_attempts.provider_message_id` matches ACS `data.messageId`
   exactly (no normalization).
4. Check for leftover queue messages from another environment.
5. Check `mail_provider_events_dead_lettered` for inbox processing failures.

Do not dump raw event JSON or plain recipient addresses into logs, Admin, or DB
beyond existing sanitized fields.

## 6. Suppression removal (#400 CLI)

If a recipient is blocked by a false positive, **do not treat direct SQL against
production SQLite as the normal recovery path**.

### Identify the target

1. Use Admin `/admin/suppressions` with a tenant selected to find the recipient
   (masked by default; unmask requires `MAILER_ADMIN_PII_LIST_MODE=visible`).
2. For counts only, use `db stats [--tenant-id <uuid>]` and read
   `mail_suppressions_count` (recipients are not printed).

### Remove command

```bash
Amane.Mailer db suppressions remove \
  --tenant-id <tenant-guid> \
  --recipient <email>
```

Exit codes:

| Code | Meaning |
|------|---------|
| 0 | Removed one row |
| 1 | Schema unavailable (`mail_suppressions` not migrated, etc.) |
| 2 | Usage error (missing args, invalid UUID, etc.) |
| 3 | No matching entry for the tenant (never silent success) |
| 130 | Cooperative cancel via Ctrl+C |

Notes:

- Recipient normalization must match store (#301) / lookup (#303) / remove (#400)
  via the same `RecipientEmailNormalizer` (`Trim` + `ToLowerInvariant`).
- `--tenant-id` is required so another tenant's identical address is not removed.
- Do not print the recipient to stdout or stderr (ADR 0013); success reports tenant ID only.
- The operation writes `mail_suppressions.removed` to `admin_audit_events`
  (actor=`cli`; recipient is never stored in the audit row).
- Admin UI removal remains out of scope.

## 7. Push (#304)

Event Grid Webhook Push is **not adopted**. Public endpoint / HTTPS termination /
AzureEventGrid Service Tag guidance belongs to #304 design docs, not this runbook.

## 8. Related

- ADR: [docs/adr/0020-bounce-ingestion-and-suppression.md](../adr/0020-bounce-ingestion-and-suppression.md)
- Admin PII: [docs/adr/0013-admin-threat-model-and-pii-policy.md](../adr/0013-admin-threat-model-and-pii-policy.md)
- Metrics: [metrics-and-alerts.en.md](metrics-and-alerts.en.md)