[日本語](backup-operations.md)

# Backup Operations

This runbook covers backup operations for a self-hosted Amane Mailer instance.
It is intentionally limited to Mailer-owned data and portable examples. Host
package installation, real rclone remotes, credentials, age identities, cron
ownership, and provider-specific bucket policies belong to the operator's
private infrastructure notes.

There are two backup paths. The existing `backup-mailer.sh` is an online,
SQLite-only database snapshot. A v2 managed instance needs the coordinated cold
path, `backup-instance-state.sh`, for disaster recovery. Do not treat a
database-only artifact as a complete instance backup.

## Scope Boundary

Amane Mailer documents:

- which Mailer files must be backed up
- how to create an online SQLite backup with the Mailer CLI
- how `backup-mailer.sh` encrypts and optionally uploads that backup
- how `backup-instance-state.sh` verifies a stopped runtime and backs up the managed instance state
- how `restore-instance-state.sh` restores only into an empty target directory
- how to verify that a backup can be restored
- example rclone and scheduler shapes that operators can adapt

Amane Mailer does not own:

- installing rclone on a specific deploy host or base image
- real rclone remote names, endpoints, access keys, or bucket names
- real age identities or key vault placement
- production retention policy for a specific organization
- host-level cron or systemd timer ownership
- ownership of Caddy certificate, configuration, and data-volume backups

Keep those host-specific decisions outside this repository. If an issue tracks
host-specific work, link back to this runbook but do not paste secrets or provider
details into the issue.

## Backup Inventory

Back up these Mailer-owned items:

| Item | Default location | Notes |
| --- | --- | --- |
| SQLite database | `./data/mailer.db` mounted at `/app/data/mailer.db` | The online database-only path is `backup-mailer.sh`. Use `Amane.Mailer db backup`; do not copy a live WAL database file directly. The admin audit log (`admin_audit_events`) lives in the same database and is preserved by backup/restore together with mail data. |
| Managed provider secret | `MAILER_DATA_PATH/secrets/acs/acs_connection_string` (container: `/app/data/secrets/acs/acs_connection_string`) | The protected file referenced by initialized v2 SQLite state. The full instance archive includes it with the database. The `MAILER_ACS_SECRET_HOST_PATH` `/run/secrets/acs` mount is a read-only compatibility/manual-registration path, not a second authority. |
| Committed attachment spool | `MAILER_DATA_PATH/attachment-spool/committed` (container: `/app/data/attachment-spool/committed`) | Durable spool required by accepted requests that are still in delivery. The full instance archive includes the opaque request/spool paths. |
| Transient attachment staging | `MAILER_DATA_PATH/attachment-spool/staging` | Excluded from the full archive. Startup reconciliation cleans orphan staging, so it is not durable restore state. |
| Bootstrap token / logs / backup staging | `MAILER_DATA_PATH/bootstrap`, `logs`, and `backups` | Excluded from the full archive. The bootstrap token is not the initialized authority; logs and old artifacts are not restore input. |
| Tenant configuration | `./tenants.json` | Manual operator backup. Contains routing and token env names. It may include operational metadata and should be reviewed before restore. |
| Compose env | `./.env` | Manual operator backup. Contains secrets or secret references. Store only in a private secret manager or host backup, never in Git. |
| Deploy template | `compose.yml` plus image tag in `.env` | Manual operator backup for host-local state. The checked-in template is reusable; the active image tag is host state. |
| Online DB-only artifact | `./data/backups/mailer-*.db.age` | Created by `backup-mailer.sh`; it is not a full instance archive. |
| Full instance encrypted artifact | `./data/backups/mailer-state-*.tar.age` | Created by `backup-instance-state.sh`. The plaintext tar exists only in a private temporary directory and is removed after age encryption. |
| Caddy state | Compose named volumes `caddy_data` (`/data`) and `caddy_config` (`/config`) | Keep it separate from the Mailer archive. The edge operator decides whether to preserve certificates/configuration or reissue them during recovery. |

Do not store `ACS_CONNECTION_STRING`, tenant bearer tokens, admin password
hashes, rclone credentials, age identities, or real backup remote details in the
repository, public logs, PR descriptions, or GitHub issues.

## Full Instance-State Boundary

`backup-instance-state.sh` is not a generic backup framework. It handles the
fixed minimum restore unit for a v2 managed instance:

- `mailer.db`
- `secrets/acs/acs_connection_string`
- `attachment-spool/committed/` and Mailer-generated opaque spool files below it

The input is the `MAILER_DATA_PATH` shared by the stopped service and migration
container. A legacy/manual deployment whose database `provider_secret_ref`
points outside that data root must be reconciled before a full backup; this path
does not silently discover a second secret authority. The script checks the
canonical data-root ACS secret, owner-only permissions, and the committed spool
shape before creating the archive.

The cold point requires the operator to stop `mailer`, `mailer-migrate`, and
`mailer-acs-admin` first. The script does not stop or start services; it checks
the Compose running-service list and fails if any of those mutators remain
running. It also fails when SQLite `-wal`, `-shm`, or `-journal` sidecars remain.
This keeps the database, provider secret, and committed spool at one stopped
point in time.

The archive excludes `attachment-spool/staging`, bootstrap tokens, logs, old
backup artifacts, tenant JSON, `.env`, `platform-sender.json`, and external
bounce-queue secrets. If bounce ingestion is enabled, its external secret is a
separate operator-owned backup unit that must be made available before restore.
Caddy `caddy_data` and `caddy_config` are also separate and must not be mixed
into this archive.

## Safety Principles

- Take Mailer database backups through `./Amane.Mailer db backup`, which uses
  SQLite's online backup API from inside the running service container.
- Take a full instance backup only after the coordinated cold stop and preflight;
  it contains the database, canonical provider secret, and committed spool.
- The full instance script archives only explicit state paths; it does not scan
  or recursively copy every volume.
- Encrypt the plaintext `.db` backup before any offsite transfer.
- Encrypt the plaintext full-instance `.tar` before any offsite transfer; it
  must never remain in the data volume or backup bucket.
- Delete plaintext `.db` and `.tar` backup files immediately after encryption.
- Never put an age private identity in the archive, repository, or logs. Only the
  public recipient is supplied through `MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY`.
- Keep `MAILER_BACKUP_REQUIRE_OFFSITE=true` for real operations unless an
  operator deliberately accepts a local encrypted backup during an incident.
- Treat `./data/backups/` as a staging directory, not durable backup storage.
- Run a restore verification after the first offsite backup, after backup script
  changes, after significant migrations, and on the operator's chosen cadence.
- If an operator temporarily sets `MAILER_BACKUP_REQUIRE_OFFSITE=false` during
  an offsite outage, record the reason, time, operator, and follow-up action in
  private operations notes, then restore the fail-secure setting as soon as the
  offsite destination is healthy.

## Age Key Management

Generate the age identity on an approved operator machine or the target host:

```bash
mkdir -p ./keys
chmod 700 ./keys
age-keygen -o ./keys/backup-age-key.txt
chmod 600 ./keys/backup-age-key.txt
age-keygen -y ./keys/backup-age-key.txt
```

Set `MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY` in the host `.env` to the recipient
printed by `age-keygen -y`. Store the identity file in the operator's password
manager or key vault, and keep at least one separate recovery copy outside the
repository and outside the backup bucket.

For key rotation, generate a new identity, update
`MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY`, run a fresh offsite backup, and complete
a restore verification with the new identity. Keep old identities until every
backup encrypted for them has expired or has been deliberately discarded.

## Rclone Example

`backup-mailer.sh` can upload encrypted `.db.age` files with rclone, but this
repository only provides the integration point. The operator decides whether
rclone is installed system-wide, under the deploy user, or supplied by another
host-management layer.

Example host state:

```text
/path/to/mailer/
  compose.yml
  compose.vps-dogfood.yml       # VPS managed-v2, when applicable
  .env
  tenants.json
  backup-mailer.sh
  backup-instance-state.sh
  data/
  rclone/
    rclone.conf        # private; do not commit
```

Example `.env` values:

```dotenv
MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY=replace-with-age-recipient-public-key
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
MAILER_BACKUP_RCLONE_CONFIG_PATH=./rclone/rclone.conf
MAILER_BACKUP_REQUIRE_OFFSITE=true
MAILER_BACKUP_PING_URL=
# Put this in .env or set it at invocation time when using the VPS overlay.
MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml
```

`MAILER_BACKUP_RCLONE_REMOTE` and the contents of `rclone.conf` are examples of
private infrastructure state. Use placeholder names in public docs and issues.
Rclone environment-variable configuration is also acceptable if secret values
remain outside Git.

Recommended object-storage controls:

- private bucket or private prefix dedicated to Mailer backups
- public access disabled
- provider-side encryption enabled when available
- upload credential scoped to the minimum actions needed for `rclone copy`
- lifecycle expiry managed by the storage provider
- separate restore/read credential or break-glass operator access

Use bucket lifecycle for offsite retention instead of giving the daily upload
credential broad delete power.

## Provisioning Order

Use this order for a self-hosted host:

1. Create or approve the private offsite destination and lifecycle policy.
2. Create the minimum upload credential needed for `rclone copy`.
3. Decide how rclone is installed and managed on the host.
4. Place the private rclone configuration on the host, or configure approved
   `RCLONE_CONFIG_*` environment variables outside Git.
5. Set the `MAILER_BACKUP_*` values in the host `.env`.
6. Run `docker compose --env-file .env -f compose.yml config --quiet`.
7. Run the online `backup-mailer.sh` only when a DB-only snapshot is intended.
8. Stop Mailer and run `backup-instance-state.sh` for disaster-recovery state.
9. Confirm no plaintext `.db` or `.tar` file remains in `data/backups/`.
10. Confirm the encrypted `.age` file exists locally and in the offsite
    destination.
11. Run full-instance restore verification before relying on a schedule.

Do not switch a real host to `MAILER_BACKUP_REQUIRE_OFFSITE=true` until the
offsite destination, credential, and rclone configuration are in place. The
failure mode is fail-secure, but scheduled backups will fail until configuration
is complete.

## Manual Backup

Copy `infra/deploy/backup-mailer.sh` to the Mailer compose directory and run it
from that directory (set `MAILER_COMPOSE_DIR` or run from the directory directly).
This is the online database-only path.

```bash
cd /path/to/mailer
docker compose --env-file .env -f compose.yml config --quiet
bash backup-mailer.sh 2>&1 | tee /tmp/mailer-backup-manual.log
```

Expected result:

- `mailer-YYYYMMDDTHHmmssZ.db.age` is written to `data/backups/`
- no plaintext `mailer-YYYYMMDDTHHmmssZ.db` remains after the script exits
- the backup is taken online through SQLite's backup API
- `rclone copy` uploads the encrypted file when
  `MAILER_BACKUP_RCLONE_REMOTE` is set
- the script exits non-zero when `MAILER_BACKUP_REQUIRE_OFFSITE=true` and the
  remote is missing or upload fails
- logs do not print secrets

If a plaintext `.db` backup is found outside an active backup operation, remove
it from the host and record the incident in the operator's private notes.

## Full Instance Backup (PR3)

A full instance backup is a coordinated cold snapshot. The script does not
stop or start services, so run it in a maintenance window after the operator
has stopped the runtime.

```bash
cd /path/to/mailer
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer mailer-migrate mailer-acs-admin 2>/dev/null || true

MAILER_COMPOSE_DIR="$PWD" \
MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml \
  bash /path/to/amane-mailer/infra/deploy/backup-instance-state.sh
```

The command exits non-zero if `mailer`, `mailer-migrate`, or
`mailer-acs-admin` is still running, or if a SQLite `-wal`, `-shm`, or
`-journal` sidecar remains. There is no environment variable or `--force` flag
that bypasses the stopped-state check. If the Compose project does not define a
one-shot service named in the stop command, verify the final `docker compose
... ps` state explicitly before invoking the script.

The expected artifact is
`data/backups/mailer-state-YYYYMMDDTHHmmssZ.tar.age`. It contains only
`mailer.db`, `secrets/acs/acs_connection_string`, and
`attachment-spool/committed` (including opaque committed spool files). It does
not contain staging, bootstrap tokens, logs, old backups, age identities, tenant
JSON, `.env`, `platform-sender.json`, or external bounce-queue secrets. The
plaintext tar is created only in a private temporary directory and is removed
after age encryption. With `MAILER_BACKUP_REQUIRE_OFFSITE=true`, missing or
failed rclone upload is a failure, not a successful local-only backup.

Do not interchange `mailer-*.db.age` and `mailer-state-*.tar.age`. If the full
instance path is scheduled, the external maintenance orchestration that owns
the stop/start window remains outside this repository; never call the full path
from an always-online cron job.

## Admin UI Backup (Optional)

When `AMANE_ADMIN_DB_OPS_ENABLED=true` (fallback: `MAILER_ADMIN_DB_OPS_ENABLED`)
is set explicitly, operators with break-glass access or all effective tenant
scopes can run WAL checkpoint and online backup from Admin UI `/admin/ops`.
`AMANE_ADMIN_ENABLED=true` alone does not enable DB operations.

| Item | Policy |
|------|--------|
| Authorization | break-glass admin, or admin with all effective tenant scopes only |
| Destination | fixed directory only (no path input in UI/API). Default: `<db-parent>/backups/`. Override: `AMANE_ADMIN_DB_BACKUP_DIRECTORY` |
| File name | `mailer-<UTC-timestamp>.db` (plaintext) |
| Audit | `admin_audit_events` records `db_ops.*` without absolute paths |
| Concurrency | checkpoint and backup are exclusive (409 Conflict while running) |

**Operational cautions**

- Backup output contains **plaintext PII** at least equal to the live Mailer DB.
  Prefer `backup-mailer.sh` (age encryption + offsite) for scheduled production
  backups; treat Admin backup as an emergency snapshot path.
- Apply destination permissions, encryption, transfer controls, and deletion
  confirmation. See [ADR 0013 D-09](../adr/0013-admin-threat-model-and-pii-policy.md).
- CLI `db checkpoint` / `db backup` remain available and are not gated by Admin
  settings.

## Scheduled Backup

Install scheduling only after manual backup and restore verification pass. Keep
the schedule in one host-owned place, such as a crontab or systemd timer.

Cron example:

```cron
30 18 * * * cd /path/to/mailer && bash backup-mailer.sh 2>&1 | logger -t amane-mailer-backup
```

Systemd timer example shape:

```ini
# /etc/systemd/system/amane-mailer-backup.service
[Unit]
Description=Amane Mailer encrypted backup

[Service]
Type=oneshot
WorkingDirectory=/path/to/mailer
ExecStart=/usr/bin/bash backup-mailer.sh
```

```ini
# /etc/systemd/system/amane-mailer-backup.timer
[Unit]
Description=Run Amane Mailer encrypted backup

[Timer]
OnCalendar=*-*-* 18:30:00
Persistent=true

[Install]
WantedBy=timers.target
```

The exact unit path, user, rclone binary path, logging destination, and timezone
are private host decisions.

## Monitoring Handoff

At minimum, the operator should monitor:

- backup command exit status
- missing offsite configuration when `MAILER_BACKUP_REQUIRE_OFFSITE=true`
- absence of recent successful backup artifacts
- `/fail` or missing success pings when `MAILER_BACKUP_PING_URL` is configured
- unexpected plaintext `.db` or `.tar` files in `data/backups/`

The ping URL, alert routing, and log destination stay outside this repository.

## Restore Verification

After the first offsite backup, run
[restore-verification.en.md](restore-verification.en.md) in a disposable environment
and record the result in private operations notes:

- date and operator
- source environment
- backup filename
- restore duration
- verification checks
- corrective actions, if any
