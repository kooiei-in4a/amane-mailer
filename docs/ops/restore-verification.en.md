[日本語](restore-verification.md)

# Restore Verification

Run a restore verification after the first offsite backup, after backup script
changes, after significant database migrations, and on the operator's chosen
cadence. Use a disposable compose project and disposable data directory so the
drill cannot affect production volumes, ports, or reverse proxy routing.

Do not use `docker compose down -v` or Docker volume prune commands in restore
drills.

## Disposable Mailer Drill

1. Prepare an isolated checkout or copied Mailer deploy directory:

   ```bash
   set -euo pipefail
   export MAILER_CHECKOUT=/path/to/amane-mailer
   export COMPOSE_PROJECT_NAME=amane_mailer_restore_check
   export MAILER_COMPOSE_FILE="$MAILER_CHECKOUT/infra/deploy/compose.yml"
   mkdir -p ./restore-mailer-data ./restore ./keys
   chmod 700 ./keys
   RESTORE_MAILER_DATA="$(pwd)/restore-mailer-data"
   ```

   The drill `.env.mailer` should use throwaway tokens, point
   `MAILER_DATA_PATH` at the absolute path in `$RESTORE_MAILER_DATA`, and point
   `MAILER_TENANTS_HOST_PATH` at a safe drill tenant JSON. Keep
   `ACS_CONNECTION_STRING` empty unless the drill explicitly includes provider
   connectivity.

2. Retrieve the age identity from the operator's private key manager. Keep the
   durable copy outside the repository and place the temporary drill copy at:

   ```text
   ./keys/backup-age-key.txt
   ```

3. Copy the selected encrypted Mailer backup from offsite storage:

   ```bash
   set -euo pipefail
   chmod 600 ./keys/backup-age-key.txt
   MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
   MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
   rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
   ```

   If the encrypted file was copied by another approved path, place it under
   `./restore/` and skip `rclone copy`.

4. Restore the SQLite database into the disposable data directory:

   ```bash
   set -euo pipefail
   rm -f ./restore-mailer-data/mailer.db ./restore-mailer-data/mailer.db-wal ./restore-mailer-data/mailer.db-shm ./restore-mailer-data/mailer.db.restoring

   age --decrypt --identity ./keys/backup-age-key.txt "./restore/$MAILER_BACKUP_FILE" \
     > ./restore-mailer-data/mailer.db.restoring
   [ -s ./restore-mailer-data/mailer.db.restoring ] || { echo "decrypt produced empty Mailer DB" >&2; exit 1; }

   if command -v sqlite3 >/dev/null 2>&1; then
     integrity_result="$(sqlite3 ./restore-mailer-data/mailer.db.restoring 'PRAGMA integrity_check;')"
     [ "$integrity_result" = "ok" ] || { echo "SQLite integrity_check failed: $integrity_result" >&2; exit 1; }
   fi

   mv ./restore-mailer-data/mailer.db.restoring ./restore-mailer-data/mailer.db

   chmod 600 ./restore-mailer-data/mailer.db
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" --profile ops run --rm mailer-migrate
   ```

5. Start the disposable Mailer service:

   ```bash
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" up -d mailer
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer /app/Amane.Mailer healthcheck
   MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env.mailer | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
   MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer /app/Amane.Mailer db stats
   ```

6. If Admin UI is enabled in the drill `.env`, verify login, mail request list
   visibility, and Dead Letters page rendering with drill-only credentials.
   Admin tenant-scope readiness uses the larger of the `tenants.json` tenant
   count and the restored DB's historical tenant count. Even if a tenant has
   been removed from configuration, the restored DB is treated as multi-tenant
   when `mail_requests` still contains 2 or more distinct `tenant_id` values.
   Check the restored DB when needed and confirm that a scoped admin or
   break-glass admin exists:

   ```bash
   sqlite3 ./restore-mailer-data/mailer.db 'SELECT COUNT(DISTINCT tenant_id) FROM mail_requests;'
   sqlite3 ./restore-mailer-data/mailer.db 'SELECT tenant_id, COUNT(*) FROM mail_requests GROUP BY tenant_id ORDER BY tenant_id;'
   ```

7. Record the drill date, backup filename, restore duration, verification
   result, and any corrective action in private operations notes.

8. Stop and remove drill containers. Preserve `restore-mailer-data/` for
   inspection until cleanup is approved, then remove it:

   ```bash
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" stop mailer
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" rm -f mailer
   rm -f ./keys/backup-age-key.txt ./restore/"$MAILER_BACKUP_FILE"
   rm -rf ./restore-mailer-data
   unset COMPOSE_PROJECT_NAME RESTORE_MAILER_DATA
   ```

## Acceptance Checks

- The encrypted backup decrypts with the stored age identity.
- The restored file exists as `mailer.db` in the disposable data directory.
- `mailer-migrate` completes successfully.
- `/app/Amane.Mailer healthcheck` exits 0.
- `/healthz` and `/readyz` return 200 in the disposable Mailer environment.
- `db stats` succeeds and shows expected status counts for the restored data.
- Admin login, Mail Requests, and Dead Letters work when Admin UI is enabled for
  the drill.
- When Admin UI is enabled, tenant scopes or a break-glass admin cover the
  restored DB's distinct tenant history.
- The drill result is recorded before relying on the next scheduled backup.
