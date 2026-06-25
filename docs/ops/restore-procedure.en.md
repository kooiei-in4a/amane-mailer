[日本語](restore-procedure.md)

# Restore Procedure

This runbook restores a self-hosted Amane Mailer SQLite database from an
encrypted backup. It does not cover application databases that may call Mailer;
those belong to the consuming application's own operations repository.

Actual environment restore is destructive and requires explicit operator
approval before stopping Mailer or replacing data.

## Preconditions

- A restore verification has already succeeded with the selected backup.
- The matching age identity has been retrieved from the operator's private key
  manager. See [backup-operations.en.md](backup-operations.en.md) for age key
  management.
- The target Mailer compose directory contains `compose.yml`, `.env`, and
  `tenants.json`.
- The selected encrypted backup name matches the Mailer format:
  `mailer-YYYYMMDDTHHmmssZ.db.age`.
- The target `.env` image tag and tenant configuration are the intended values
  for the restored service.

## Age Identity Handling

The age identity is the only way to decrypt backups. If it is lost, encrypted
backups are permanently unrecoverable.

During restore, copy the identity into a git-ignored temporary path such as
`./keys/backup-age-key.txt`, keep permissions restricted, and remove the
temporary copy after the incident or drill is complete. Never place the identity
in the repository or the backup bucket.

## Restore Mailer

Run these commands from the Mailer compose directory after approval. Replace the
paths and filenames with the operator's private values.

First prepare the restore workspace:

```bash
set -euo pipefail
cd /path/to/mailer
docker compose --env-file .env -f compose.yml config --quiet

mkdir -p ./data ./restore ./restore/previous ./keys
chmod 700 ./keys
```

Copy `./keys/backup-age-key.txt` from the private key manager, then continue:

```bash
set -euo pipefail
chmod 600 ./keys/backup-age-key.txt
MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"

docker compose --env-file .env -f compose.yml stop mailer
cp -a data/mailer.db "restore/previous/mailer.db.before-restore-$(date -u +%Y%m%dT%H%M%SZ)" 2>/dev/null || true
rm -f data/mailer.db data/mailer.db-wal data/mailer.db-shm data/mailer.db.restoring

age --decrypt --identity ./keys/backup-age-key.txt "./restore/$MAILER_BACKUP_FILE" \
  > data/mailer.db.restoring
[ -s data/mailer.db.restoring ] || { echo "decrypt produced empty Mailer DB" >&2; exit 1; }

if command -v sqlite3 >/dev/null 2>&1; then
  integrity_result="$(sqlite3 data/mailer.db.restoring 'PRAGMA integrity_check;')"
  [ "$integrity_result" = "ok" ] || { echo "SQLite integrity_check failed: $integrity_result" >&2; exit 1; }
fi

mv data/mailer.db.restoring data/mailer.db

chmod 600 data/mailer.db
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml up -d mailer
```

Use the same procedure for a locally copied encrypted backup; skip only the
`rclone copy` step.

## Verify

Confirm the restored service before re-enabling callers:

```bash
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer healthcheck
MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
docker compose --env-file .env -f compose.yml exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
docker compose --env-file .env -f compose.yml exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer db stats
```

If the Admin UI is enabled for the host, also verify login, mail request list
visibility, and Dead Letters page rendering through the approved reverse proxy.

## Rollback

If verification fails, keep callers disabled. Restore the previous database
copy from `restore/previous/` or restore the next known-good encrypted backup
with the same procedure. Preserve the failed backup files and container logs
until incident notes are complete.

After the incident or drill, remove temporary restore material without touching
database volumes:

```bash
MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
rm -f ./keys/backup-age-key.txt ./restore/"$MAILER_BACKUP_FILE" ./data/mailer.db.restoring
```
