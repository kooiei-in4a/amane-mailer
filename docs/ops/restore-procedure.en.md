[日本語](restore-procedure.md)

# Restore Procedure

This runbook restores a v2 managed self-hosted Amane Mailer coordinated/cold
instance-state archive into a disposable or newly empty data directory. A
database-only mailer-*.db.age artifact is an online SQLite snapshot; it does
not contain the provider secret or committed attachment spool and is not a
full-restore substitute.

Switching a production deployment can be destructive. Restore into a new
verification target first, check startup and readiness, and only then make the
operator-owned path switch. Neither this runbook nor
restore-instance-state.sh has a --force option or silently overwrites state.

## Preconditions

- Restore verification has already succeeded for the selected
  mailer-state-YYYYMMDDTHHmmssZ.tar.age.
- The matching age identity is available from private key management and is
  outside the repository and backup remote. Keep its mode owner-only (600).
- The checkout has compose.yml, compose.vps-dogfood.yml for VPS managed-v2, and
  the host .env.
- Do not use an existing MAILER_DATA_PATH directory as the restore target. The
  target must be a fresh or empty absolute path.
- Confirm the container runtime UID/GID from the image or private deployment
  metadata. Do not assume a Dockerfile number; compare docker image inspect
  Config.User with the actual runtime identity and pass
  --runtime-uid / --runtime-gid.
- Mailer and migration/admin mutators are stopped. The helper does not stop or
  start services.
- Keep Caddy caddy_data / caddy_config separate from the Mailer archive.

## Restored authority

The full archive has this fixed restore unit:

- mailer.db, including managed provider/sender state, admin credential epoch,
  request state, and submission evidence
- secrets/acs/acs_connection_string (container:
  /app/data/secrets/acs/acs_connection_string)
- attachment-spool/committed/ and its opaque files for accepted requests still
  requiring delivery

The archive excludes attachment-spool/staging, bootstrap tokens, logs,
data/backups, tenant JSON, .env, platform-sender.json, the age private key,
external bounce-queue secrets, and Caddy volumes. External secrets used by a
deployment are a separate operator-owned backup unit and must be supplied at
the same reference before restore.

For an initialized database, SQLite provider_secret_ref is the authority. If
the canonical provider secret is missing or corrupt, Mailer returns
503 provider_secret_missing from /readyz and keeps /setup at 404. It must not
fall back to bare ACS_CONNECTION_STRING or re-enter setup.

## Restore into an empty target

In these examples, /path/to/amane-mailer is the checkout and /path/to/mailer
is the Compose directory. Replace placeholders with private operational values;
do not put secret contents in commands or logs.

1. Validate Compose configuration and stop Mailer mutators:

~~~bash
set -Eeuo pipefail
cd /path/to/mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer mailer-migrate mailer-acs-admin 2>/dev/null || true

mkdir -p ./restore ./keys ./secrets/acs
chmod 700 ./restore ./keys ./secrets/acs
chmod 700 ./secrets
~~~

After stop, run ps and confirm that mailer, mailer-migrate, and
mailer-acs-admin are not running. caddy owns edge state and may remain up when
it does not interfere with the Mailer cold point:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps
~~~

2. Copy the identity temporarily from private key management and fix its
   permissions. If the encrypted archive exists only in the remote, use
   rclone copy; skip it when the file is already in ./restore:

~~~bash
chmod 600 ./keys/backup-age-key.txt
MAILER_BACKUP_FILE=mailer-state-YYYYMMDDTHHmmssZ.tar.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
~~~

3. Create a new empty target and pass the runtime UID/GID to the helper. An
   existing ./data target is rejected:

~~~bash
RESTORE_TARGET="$(mktemp -d "$PWD/restore-mailer-data.XXXXXX")"
MAILER_RUNTIME_UID=1654
MAILER_RUNTIME_GID=1654

bash /path/to/amane-mailer/infra/deploy/restore-instance-state.sh \
  --archive "$PWD/restore/$MAILER_BACKUP_FILE" \
  --identity "$PWD/keys/backup-age-key.txt" \
  --target "$RESTORE_TARGET" \
  --runtime-uid "$MAILER_RUNTIME_UID" \
  --runtime-gid "$MAILER_RUNTIME_GID"
~~~

The 1654 values are placeholders only. Replace them with the UID/GID confirmed
from the image/runtime. The helper decrypts into a private temporary path,
checks the archive against the fixed boundary, extracts it, and applies mode
600 to the database and provider secret plus owner-only directory modes. It
does not run migrations, start services, or operate Caddy.

## Migration and readiness

Temporarily point a verification Compose project at the new target. Leave the
original .env and ./data unchanged; override MAILER_DATA_PATH in the shell.
The VPS overlay /run/secrets/acs bind is read-only compatibility state. Managed
v2 authority is the restored /app/data/secrets/acs/acs_connection_string, so do
not create a second provider-secret authority there.

~~~bash
export MAILER_DATA_PATH="$RESTORE_TARGET"
export MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d mailer
~~~

If migration fails, do not start Mailer; discard the target and use another
verified archive. Before restoring callers, check /healthz, /readyz, and DB
stats:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood exec -T mailer \
  /app/Amane.Mailer healthcheck
curl -fsS https://mailer.example.invalid/healthz
curl -fsS https://mailer.example.invalid/readyz
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood exec -T mailer \
  /app/Amane.Mailer db stats
~~~

Also confirm that /setup is 404. In the missing/corrupt-provider-secret
regression check, expect /readyz to return 503 with JSON reason
provider_secret_missing and /setup to return 404. Do not use a setup token to
reinitialize that state or try a bare environment fallback. This is the same
fail-safe contract expected during an incident.

A security-sensitive point-in-time restore also restores the historical API-key
hashes, credential epoch, and revocation/session state in the database. Review
administrator credentials, API keys, unwanted sessions/revocations, and
external secrets after restore; rotate or revoke them through the approved
operator procedure when necessary.

## Switch, rollback, and cleanup

Do not change the original MAILER_DATA_PATH or edge until verification passes.
During the switch, make the target the intentional data path in the
maintenance procedure and re-check Compose config, ownership, and readiness. If
verification fails, stop Mailer, point MAILER_DATA_PATH back to the original
state, and leave the original data intact. Because the helper never modifies an
existing target, a database-only restore/previous overwrite is not needed for
rollback.

After the drill, once audit and incident notes are complete, delete only the
explicit disposable target, downloaded .tar.age, and temporary identity. Keep
the original data volume, Caddy named volumes, and key-vault recovery copy:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer
rm -f -- "./restore/$MAILER_BACKUP_FILE" ./keys/backup-age-key.txt
rm -rf -- "$RESTORE_TARGET"
~~~
