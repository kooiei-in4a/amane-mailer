#!/usr/bin/env bash
# Cold backup of the managed Mailer instance state: SQLite, the registered provider
# secret, and committed attachment spool. This is deliberately separate from
# backup-mailer.sh, which remains the online SQLite-only backup command.
set -Eeuo pipefail
umask 077

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is not installed: $1"
}

_parse_env() {
  local line key val
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      '#'*|'') continue ;;
      MAILER_BACKUP_*=*|MAILER_DATA_PATH=*|MAILER_COMPOSE_FILE=*)
        key="${line%%=*}"
        val="${line#*=}"
        export "$key=$val"
        ;;
    esac
  done < "$1"
}

COMPOSE_DIR="${MAILER_COMPOSE_DIR:-/opt/amane-mailer}"
[ -d "$COMPOSE_DIR" ] || fail "compose directory does not exist"
COMPOSE_DIR="$(cd -P -- "$COMPOSE_DIR" && pwd -P)"

ENV_FILE="${MAILER_ENV_FILE:-$COMPOSE_DIR/.env}"
case "$ENV_FILE" in
  /*) ;;
  *) ENV_FILE="$COMPOSE_DIR/$ENV_FILE" ;;
esac
[ -f "$ENV_FILE" ] || fail "Compose env file does not exist"
_parse_env "$ENV_FILE"

: "${MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY:?MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY is not set in .env}"
MAILER_BACKUP_REQUIRE_OFFSITE="${MAILER_BACKUP_REQUIRE_OFFSITE:-true}"
MAILER_BACKUP_RCLONE_REMOTE="${MAILER_BACKUP_RCLONE_REMOTE:-}"
MAILER_BACKUP_RCLONE_CONFIG_PATH="${MAILER_BACKUP_RCLONE_CONFIG_PATH:-./rclone/rclone.conf}"
MAILER_BACKUP_PING_URL="${MAILER_BACKUP_PING_URL:-}"

case "$MAILER_BACKUP_REQUIRE_OFFSITE" in
  true|false) ;;
  *) fail "MAILER_BACKUP_REQUIRE_OFFSITE must be true or false" ;;
esac

if [ "$MAILER_BACKUP_REQUIRE_OFFSITE" = "true" ] && [ -z "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  fail "MAILER_BACKUP_REQUIRE_OFFSITE=true but MAILER_BACKUP_RCLONE_REMOTE is not set"
fi

COMPOSE_FILE_VALUE="${MAILER_COMPOSE_FILE:-${COMPOSE_FILE:-compose.yml}}"
IFS=: read -r -a COMPOSE_FILE_NAMES <<< "$COMPOSE_FILE_VALUE"
COMPOSE_ARGS=()
for compose_file in "${COMPOSE_FILE_NAMES[@]}"; do
  [ -n "$compose_file" ] || fail "MAILER_COMPOSE_FILE contains an empty path"
  case "$compose_file" in
    /*) resolved_compose_file="$compose_file" ;;
    *) resolved_compose_file="$COMPOSE_DIR/$compose_file" ;;
  esac
  [ -f "$resolved_compose_file" ] || fail "Compose file does not exist"
  COMPOSE_ARGS+=(-f "$resolved_compose_file")
done

DATA_PATH="${MAILER_DATA_PATH:-./data}"
case "$DATA_PATH" in
  /*) DATA_CANDIDATE="$DATA_PATH" ;;
  *) DATA_CANDIDATE="$COMPOSE_DIR/$DATA_PATH" ;;
esac
[ -d "$DATA_CANDIDATE" ] || fail "MAILER_DATA_PATH does not exist or is not a directory"
DATA_DIR="$(cd -P -- "$DATA_CANDIDATE" && pwd -P)"

DB_PATH="$DATA_DIR/mailer.db"
ACS_SECRET_PATH="$DATA_DIR/secrets/acs/acs_connection_string"
ACS_SECRET_DIR="$(dirname -- "$ACS_SECRET_PATH")"
COMMITTED_SPOOL_PATH="$DATA_DIR/attachment-spool/committed"
BACKUP_DIR="$DATA_DIR/backups"

require_command docker
require_command age
require_command tar
require_command stat
require_command find
require_command grep
require_command mktemp
if [ -n "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  require_command rclone
fi
if [ -n "$MAILER_BACKUP_PING_URL" ]; then
  require_command curl
fi

if [ -L "$BACKUP_DIR" ]; then
  fail "backup directory must not be a symlink"
fi
mkdir -p -- "$BACKUP_DIR"
chmod 700 -- "$BACKUP_DIR"

TEMP_DIR=""
PLAINTEXT=""
ENCRYPTED_PARTIAL=""
ENCRYPTED=""
DONE=0

cleanup() {
  local status=$?
  set +e
  if [ -n "${PLAINTEXT:-}" ]; then
    rm -f -- "$PLAINTEXT"
  fi
  if [ -n "${ENCRYPTED_PARTIAL:-}" ]; then
    rm -f -- "$ENCRYPTED_PARTIAL"
  fi
  if [ "${DONE:-0}" -eq 0 ] && [ -n "${ENCRYPTED:-}" ]; then
    rm -f -- "$ENCRYPTED"
    if [ -n "${MAILER_BACKUP_PING_URL:-}" ]; then
      curl -fsS --max-time 10 "${MAILER_BACKUP_PING_URL}/fail" >/dev/null 2>&1 || true
    fi
  fi
  if [ -n "${TEMP_DIR:-}" ]; then
    rm -rf -- "$TEMP_DIR"
  fi
  trap - EXIT
  exit "$status"
}
trap cleanup EXIT

verify_mailer_stopped() {
  local running_services service
  running_services="$(
    cd -- "$COMPOSE_DIR"
    docker compose --env-file "$ENV_FILE" "${COMPOSE_ARGS[@]}" \
      ps --status running --services 2>/dev/null
  )" || fail "could not inspect Compose service state"

  while IFS= read -r service; do
    case "$service" in
      mailer|mailer-migrate|mailer-acs-admin)
        fail "$service is still running; stop Mailer and migration/admin mutators before a cold backup"
        ;;
    esac
  done <<< "$running_services"
}

require_regular_file() {
  local path="$1"
  [ ! -L "$path" ] || fail "required state must not be a symlink"
  [ -f "$path" ] || fail "required state file is missing"
}

require_directory() {
  local path="$1"
  [ ! -L "$path" ] || fail "required state directory must not be a symlink"
  [ -d "$path" ] || fail "required state directory is missing"
}

require_owner_only() {
  local path="$1" mode mode_value
  mode="$(stat -c '%a' -- "$path")" || fail "could not inspect state permissions"
  mode_value=$((8#$mode))
  (( (mode_value & 077) == 0 )) || fail "provider secret state is not owner-only"
}

validate_provider_secret() {
  require_regular_file "$ACS_SECRET_PATH"
  require_owner_only "$ACS_SECRET_PATH"
  [ -s "$ACS_SECRET_PATH" ] || fail "provider secret state is empty"
  grep -Eiq '(^|[;[:space:]])endpoint=[^;[:space:]]+' "$ACS_SECRET_PATH" \
    || fail "provider secret state does not contain an endpoint"
  grep -Eiq '(^|[;[:space:]])accesskey=[^;[:space:]]+' "$ACS_SECRET_PATH" \
    || fail "provider secret state does not contain an access key"
  require_owner_only "$ACS_SECRET_DIR"
}

validate_committed_spool() {
  local bad_child request_dir request_id bad_entry file_path file_name
  require_directory "$COMMITTED_SPOOL_PATH"

  bad_child="$(find -P "$COMMITTED_SPOOL_PATH" -mindepth 1 -maxdepth 1 ! -type d -print -quit)"
  [ -z "$bad_child" ] || fail "committed attachment spool contains an unexpected top-level entry"

  while IFS= read -r -d '' request_dir; do
    request_id="${request_dir##*/}"
    [[ "$request_id" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$ ]] \
      || fail "committed attachment spool contains an unexpected request directory"
    bad_entry="$(find -P "$request_dir" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
    [ -z "$bad_entry" ] || fail "committed attachment spool contains an unexpected nested entry"
    while IFS= read -r -d '' file_path; do
      file_name="${file_path##*/}"
      [[ "$file_name" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.bin$ ]] \
        || fail "committed attachment spool contains an unexpected file"
    done < <(find -P "$request_dir" -mindepth 1 -maxdepth 1 -type f -print0)
  done < <(find -P "$COMMITTED_SPOOL_PATH" -mindepth 1 -maxdepth 1 -type d -print0)

  for sidecar in "$DB_PATH-wal" "$DB_PATH-shm" "$DB_PATH-journal"; do
    if [ -e "$sidecar" ] || [ -L "$sidecar" ]; then
      fail "SQLite sidecar exists; cold backup requires a clean stopped database"
    fi
  done
}

verify_mailer_stopped
require_regular_file "$DB_PATH"
validate_provider_secret
validate_committed_spool

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/amane-mailer-instance-backup.XXXXXX")"
chmod 700 -- "$TEMP_DIR"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
PLAINTEXT_BASENAME="mailer-state-${TIMESTAMP}.tar"
ENCRYPTED_BASENAME="${PLAINTEXT_BASENAME}.age"
PLAINTEXT="$TEMP_DIR/$PLAINTEXT_BASENAME"
ENCRYPTED_PARTIAL="$BACKUP_DIR/.${ENCRYPTED_BASENAME}.partial.$$"
ENCRYPTED="$BACKUP_DIR/$ENCRYPTED_BASENAME"
[ ! -e "$ENCRYPTED" ] || fail "encrypted backup already exists for this timestamp"
[ ! -e "$ENCRYPTED_PARTIAL" ] || fail "encrypted backup temporary path already exists"

echo "[1/4] Creating cold Mailer instance archive..."
tar --create --file "$PLAINTEXT" --directory "$DATA_DIR" \
  --format=posix --numeric-owner --owner=0 --group=0 \
  mailer.db secrets/acs/acs_connection_string attachment-spool/committed
[ -s "$PLAINTEXT" ] || fail "instance archive is missing or empty"

echo "[2/4] Encrypting instance archive with age..."
age --encrypt \
  --recipient "$MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY" \
  --output "$ENCRYPTED_PARTIAL" \
  "$PLAINTEXT"
[ -s "$ENCRYPTED_PARTIAL" ] || fail "encrypted backup is missing or empty"
mv -- "$ENCRYPTED_PARTIAL" "$ENCRYPTED"
ENCRYPTED_PARTIAL=""
rm -f -- "$PLAINTEXT"
PLAINTEXT=""

echo "[3/4] Uploading encrypted instance backup..."
if [ -n "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  case "$MAILER_BACKUP_RCLONE_CONFIG_PATH" in
    /*) rclone copy --config "$MAILER_BACKUP_RCLONE_CONFIG_PATH" "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE" ;;
    *)
      config_path="$COMPOSE_DIR/$MAILER_BACKUP_RCLONE_CONFIG_PATH"
      if [ -f "$config_path" ]; then
        rclone copy --config "$config_path" "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
      else
        rclone copy "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
      fi
      ;;
  esac
else
  echo "Skipping offsite upload (MAILER_BACKUP_REQUIRE_OFFSITE=false)"
fi

echo "[4/4] Confirming plaintext cleanup..."
[ ! -e "$PLAINTEXT" ] || fail "plaintext instance archive was not removed"
DONE=1
echo "Instance backup complete: $ENCRYPTED_BASENAME"

if [ -n "$MAILER_BACKUP_PING_URL" ]; then
  curl -fsS --max-time 10 "$MAILER_BACKUP_PING_URL" >/dev/null || true
fi
