[日本語](backup-operations.md)

# Backup Operations

This runbook covers backup operations for a self-hosted Amane Mailer instance.
It is intentionally limited to Mailer-owned data and portable examples. Host
package installation, real rclone remotes, credentials, age identities, cron
ownership, and provider-specific bucket policies belong to the operator's
private infrastructure notes.

## Scope Boundary

Amane Mailer documents:

- which Mailer files must be backed up
- how to create an online SQLite backup with the Mailer CLI
- how `backup-mailer.sh` encrypts and optionally uploads that backup
- how to verify that a backup can be restored
- example rclone and scheduler shapes that operators can adapt

Amane Mailer does not own:

- installing rclone on a specific deploy host or base image
- real rclone remote names, endpoints, access keys, or bucket names
- real age identities or key vault placement
- production retention policy for a specific organization
- host-level cron or systemd timer ownership

Keep those host-specific decisions outside this repository. If an issue tracks
host-specific work, link back to this runbook but do not paste secrets or provider
details into the issue.

## Backup Inventory

Back up these Mailer-owned items:

| Item | Default location | Notes |
| --- | --- | --- |
| SQLite database | `./data/mailer.db` mounted at `/app/data/mailer.db` | Covered by `backup-mailer.sh`. Use `Amane.Mailer db backup`; do not copy a live WAL database file directly. |
| Tenant configuration | `./tenants.json` | Manual operator backup. Contains routing and token env names. It may include operational metadata and should be reviewed before restore. |
| Compose env | `./.env` | Manual operator backup. Contains secrets or secret references. Store only in a private secret manager or host backup, never in Git. |
| Deploy template | `compose.yml` plus image tag in `.env` | Manual operator backup for host-local state. The checked-in template is reusable; the active image tag is host state. |
| Encrypted backup artifacts | `./data/backups/mailer-*.db.age` | These are safe to upload only after encryption and access-policy review. |

Do not store `ACS_CONNECTION_STRING`, tenant bearer tokens, admin password
hashes, rclone credentials, age identities, or real backup remote details in the
repository, public logs, PR descriptions, or GitHub issues.

## Safety Principles

- Take Mailer database backups through `./Amane.Mailer db backup`, which uses
  SQLite's online backup API from inside the running service container.
- Encrypt the plaintext `.db` backup before any offsite transfer.
- Delete plaintext `.db` backup files immediately after encryption.
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
  .env
  tenants.json
  backup-mailer.sh
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
7. Run a manual backup.
8. Confirm no plaintext `.db` file remains in `data/backups/`.
9. Confirm the encrypted `.db.age` file exists locally and in the offsite
   destination.
10. Run restore verification before relying on the schedule.

Do not switch a real host to `MAILER_BACKUP_REQUIRE_OFFSITE=true` until the
offsite destination, credential, and rclone configuration are in place. The
failure mode is fail-secure, but scheduled backups will fail until configuration
is complete.

## Manual Backup

Copy `infra/deploy/backup-mailer.sh` to the Mailer compose directory and run it
from that directory (set `MAILER_COMPOSE_DIR` or run from the directory directly).

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
- unexpected plaintext `.db` files in `data/backups/`

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
