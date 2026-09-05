[日本語](restore-verification.md)

# Restore Verification

After the first offsite backup, after backup-script changes, after significant
migrations, and on the operator's chosen cadence, restore a full instance
archive in a disposable environment. This drill does not send through real ACS,
use a real recipient, or expose a real provider secret. The automated fixture
uses fake SQLite, a fake secret, and a fake committed spool:

~~~bash
bash /path/to/amane-mailer/scripts/backup-instance-state-self-test.sh
~~~

The fixture uses age/rclone/docker test doubles and checks the encrypt/decrypt
path, byte-for-byte content, exclusion boundary, missing-state RED cases, and
non-empty-target refusal. The test double is not a production encryption
replacement.

## Drill Safety Boundary

- Do not use the production MAILER_DATA_PATH, Compose project, or Caddy named
  volumes.
- Do not run docker compose down -v, volume-prune commands, or deletion/overwrite
  against an existing data directory.
- The restore helper accepts only a fresh or empty target. Confirm that a
  restore against a directory containing a sentinel fails and leaves the
  sentinel unchanged.
- Keep the age identity in a temporary owner-only (600) path. Do not put real
  secrets, recipients, bearer tokens, or addresses in logs or issues.
- Keep bounce ingestion disabled and do not connect to a real ACS endpoint.
  Readiness checks are limited to health endpoints and DB/filesystem state.
- Do not restore Caddy caddy_data / caddy_config or mix them into Mailer state.

## Prepare Disposable Compose

In these examples, /path/to/amane-mailer is the checkout and /path/to/mailer
is the host Compose directory. If the VPS overlay is not used, remove
compose.vps-dogfood.yml from each command:

~~~bash
set -Eeuo pipefail
export COMPOSE_PROJECT_NAME=amane-mailer-restore-check
cd /path/to/mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet

mkdir -p ./restore ./keys ./secrets/acs
chmod 700 ./restore ./keys ./secrets ./secrets/acs
chmod 600 ./keys/backup-age-key.txt
~~~

To fetch the encrypted full archive from a private remote:

~~~bash
MAILER_BACKUP_FILE=mailer-state-YYYYMMDDTHHmmssZ.tar.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
~~~

Skip rclone when an approved archive is already in ./restore.

## Restore and Check Archive Contents

Confirm that Mailer and migration/admin mutators are stopped. Create a fresh
target and run the helper. Replace the example UID/GID with values confirmed
from the image/runtime; do not blindly use 1654:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

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

For the successful target, confirm:

- mailer.db exists and was restored from the same archive as the provider
  secret.
- secrets/acs/acs_connection_string exists under owner-only directories with
  file mode 600.
- attachment-spool/committed/ and its opaque request/spool files exist and
  match the source fixture or private backup-time inventory byte-for-byte.
- attachment-spool/staging, bootstrap, logs, and data/backups were not created.
- Neither the age identity nor a plaintext tar appeared in the target or data
  volume.
- The non-empty-target refusal test preserved its sentinel.

If sqlite3 is available, perform an additional DB integrity check:

~~~bash
sqlite3 "$RESTORE_TARGET/mailer.db" 'PRAGMA integrity_check;'
~~~

The expected result is ok. This does not replace migration.

## Migration, Startup, and Readiness

Point only the verification project at the target; do not change the original
./data. The external ACS bind in the VPS overlay is read-only compatibility
state. Do not duplicate the managed v2 provider authority there:

~~~bash
export MAILER_DATA_PATH="$RESTORE_TARGET"
export MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d mailer

curl -fsS https://mailer.example.invalid/healthz
curl -fsS https://mailer.example.invalid/readyz
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec -T mailer /app/Amane.Mailer db stats
~~~

Record migration exit status, health/readiness HTTP status, DB stats, committed
spool count, elapsed time, archive name, runtime image tag, and ownership/mode
in private operations notes. A real provider-send result is not part of this
drill.

For a negative check, temporarily remove or corrupt the provider secret in the
disposable target and restart. Expect:

- /readyz returns HTTP 503 with JSON reason provider_secret_missing.
- /setup returns HTTP 404.
- Adding bare ACS_CONNECTION_STRING does not produce a fallback.
- A setup token cannot reinitialize the instance.

After the negative check, discard the target without changing the source archive
or production data.

## Complete and Clean Up

Stop the Mailer and remove the verification project. Save evidence in private
operations notes. After audit and incident records are complete, delete only
the explicitly named disposable target, downloaded archive, and temporary
identity. Keep Caddy named volumes and the key-vault recovery copy:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer
rm -f -- "./restore/$MAILER_BACKUP_FILE" ./keys/backup-age-key.txt
rm -rf -- "$RESTORE_TARGET"
~~~
