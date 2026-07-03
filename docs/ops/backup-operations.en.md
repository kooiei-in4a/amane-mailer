# Backup operations

Operational guidance for Mailer SQLite database backup, retention, and encryption.

## Capture methods

| Method | Use case |
|--------|----------|
| `dotnet Amane.Mailer.dll db backup <absolute-path>` | CLI backup from container exec / cron (existing path) |
| Admin UI `/admin/ops` | Only when `AMANE_ADMIN_DB_OPS_ENABLED=true` and the operator is break-glass or holds all effective tenant scopes |

Both paths use the **online SQLite Backup API** against the live database.

## Admin backup cautions

- **Disabled by default.** `AMANE_ADMIN_ENABLED=true` alone does not enable DB operations. Set `AMANE_ADMIN_DB_OPS_ENABLED=true` explicitly (fallback: `MAILER_ADMIN_DB_OPS_ENABLED`).
- Backup is a **service-wide** operation. It includes all tenants' mail payloads (recipients, subjects, bodies, metadata, etc.). Treat output as **plaintext PII** and protect storage access, encryption, and transfer paths per your runbook.
- Admin backup writes only to a **fixed directory**. Operators cannot choose a path through the UI/API.
  - Default: `backups/` under the mailer database parent directory
  - Override: set `AMANE_ADMIN_DB_BACKUP_DIRECTORY` (fallback: `MAILER_ADMIN_DB_BACKUP_DIRECTORY`) to an **absolute path**
  - File name: `mailer-<UTC-timestamp>.db` (example: `mailer-20260704T045100Z.db`)
- Operations are recorded in `admin_audit_events` (`db_ops.backup_requested`, `db_ops.backup_completed`, `db_ops.backup_failed`). Audit rows store the destination **category**, not absolute paths.
- Checkpoint and backup **cannot run concurrently**. A second request returns 409 Conflict while one is in progress.
- For production, prefer `infra/deploy/backup-mailer.sh` (age encryption + offsite upload). Admin backup is intended for emergency plaintext snapshots.

## Related docs

- [restore-verification.en.md](restore-verification.en.md) — restore verification drill
- [restore-procedure.en.md](restore-procedure.en.md) — production restore
- [ADR 0013 D-09](../adr/0013-admin-threat-model-and-pii-policy.md) — Admin DB ops threat model
