#!/usr/bin/env bash
# Shared Mailer SQLite backup: backup → encrypt (age) → upload (rclone) → cleanup → ping
# Run from the Mailer compose directory or set MAILER_COMPOSE_DIR.
set -Eeuo pipefail

COMPOSE_DIR="${MAILER_COMPOSE_DIR:-/opt/amane-mailer}"

# Parse only the vars we need from the Compose env file.
# Full `source` would fail on Compose-specific syntax (e.g. "Data Source=..." values).
_parse_env() {
  local key val
  while IFS= read -r line; do
    case "$line" in
      '#'*|'') continue ;;
      MAILER_BACKUP_*=*|MAILER_DATA_PATH=*)
        key="${line%%=*}"
        val="${line#*=}"
        export "$key=$val"
        ;;
    esac
  done < "$1"
}
[ -f "$COMPOSE_DIR/.env" ] && _parse_env "$COMPOSE_DIR/.env"

_DONE=0
PLAINTEXT=""
ENCRYPTED=""
_PLAINTEXT_OWNED=0
_ENCRYPTED_OWNED=0
_STATUS_DIR=""
_STATUS_READY=0
_STATUS_STARTED_AT=""
_STATUS_STAGE="preflight"
_STATUS_OFFSITE="not-attempted"
_STATUS_ARTIFACT=""

_utc_now() {
  date -u +'%Y-%m-%dT%H:%M:%SZ'
}

_write_status_receipt() {
  local file_name="$1" record_type="$2" status="$3" started_at="$4"
  local completed_at="$5" stage="$6" offsite_status="$7" artifact_name="$8"
  [ "${_STATUS_READY:-0}" -eq 1 ] || return 0
  [ ! -L "$_STATUS_DIR" ] && [ -d "$_STATUS_DIR" ] || return 0

  local completed_json=null artifact_json=null temp_path
  [ -z "$completed_at" ] || completed_json="\"$completed_at\""
  [ -z "$artifact_name" ] || artifact_json="\"$artifact_name\""
  temp_path="$_STATUS_DIR/.${file_name}.tmp.$$"
  if ! {
    printf '{"schemaVersion":1,"backupType":"database-only","recordType":"%s","status":"%s","startedAtUtc":"%s","completedAtUtc":%s,"stage":"%s","offsiteStatus":"%s","artifactName":%s}\n' \
      "$record_type" "$status" "$started_at" "$completed_json" "$stage" "$offsite_status" "$artifact_json" > "$temp_path"
    chmod 644 -- "$temp_path"
    mv -f -- "$temp_path" "$_STATUS_DIR/$file_name"
  } 2>/dev/null; then
    rm -f -- "$temp_path" 2>/dev/null || true
    echo "WARNING: backup status receipt could not be updated" >&2
  fi
}

_write_attempt_receipt() {
  _write_status_receipt \
    "db-only.attempt.json" "attempt" "$1" "$_STATUS_STARTED_AT" "$2" \
    "$_STATUS_STAGE" "$_STATUS_OFFSITE" "$_STATUS_ARTIFACT"
}

_cleanup() {
  local status=$?
  if [ "${_PLAINTEXT_OWNED:-0}" -eq 1 ] && [ -n "${PLAINTEXT:-}" ]; then
    rm -f -- "$PLAINTEXT" || true
  fi
  if [ "${_DONE}" -eq 0 ]; then
    if [ "${_ENCRYPTED_OWNED:-0}" -eq 1 ] && [ -n "${ENCRYPTED:-}" ]; then
      rm -f -- "$ENCRYPTED" || true
    fi
    if [ "${_STATUS_READY:-0}" -eq 1 ]; then
      if [ "$_STATUS_OFFSITE" = "pending" ]; then
        _STATUS_OFFSITE="failed"
      fi
      _STATUS_ARTIFACT=""
      _write_attempt_receipt "failed" "$(_utc_now)" || true
    fi
    if [ -n "${MAILER_BACKUP_PING_URL:-}" ]; then
      curl -fsS --max-time 10 "${MAILER_BACKUP_PING_URL}/fail" > /dev/null 2>&1 || true
    fi
  fi
  return "$status"
}
trap _cleanup EXIT

MAILER_BACKUP_REQUIRE_OFFSITE="${MAILER_BACKUP_REQUIRE_OFFSITE:-true}"
MAILER_BACKUP_RCLONE_REMOTE="${MAILER_BACKUP_RCLONE_REMOTE:-}"
MAILER_BACKUP_RCLONE_CONFIG_PATH="${MAILER_BACKUP_RCLONE_CONFIG_PATH:-./rclone/rclone.conf}"
MAILER_BACKUP_PING_URL="${MAILER_BACKUP_PING_URL:-}"

# Resolve the status sidecar beside the data root. It contains only fixed-schema,
# non-secret execution metadata and is outside the encrypted backup boundary.
_data_path="${MAILER_DATA_PATH:-./data}"
case "$_data_path" in
  /*) HOST_DATA_DIR="$_data_path" ;;
  *)  HOST_DATA_DIR="$COMPOSE_DIR/$_data_path" ;;
esac
_STATUS_DIR="$HOST_DATA_DIR/.mailer-backup-status"
if [ ! -L "$HOST_DATA_DIR" ] && mkdir -p -- "$_STATUS_DIR" 2>/dev/null; then
  if chmod 755 -- "$_STATUS_DIR" 2>/dev/null \
    && [ ! -L "$_STATUS_DIR" ] \
    && [ -d "$_STATUS_DIR" ]; then
    _STATUS_READY=1
    _STATUS_STARTED_AT="$(_utc_now)"
    _write_attempt_receipt "running" ""
  fi
fi

: "${MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY:?MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY is not set in .env}"

if [ "$MAILER_BACKUP_REQUIRE_OFFSITE" = "true" ] && [ -z "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  echo "ERROR: MAILER_BACKUP_REQUIRE_OFFSITE=true but MAILER_BACKUP_RCLONE_REMOTE is not set" >&2
  exit 1
fi

BACKUP_DIR="$HOST_DATA_DIR/backups"

case "$MAILER_BACKUP_RCLONE_CONFIG_PATH" in
  /*) ;;
  *)  MAILER_BACKUP_RCLONE_CONFIG_PATH="$COMPOSE_DIR/$MAILER_BACKUP_RCLONE_CONFIG_PATH" ;;
esac

TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BASENAME="mailer-${TIMESTAMP}.db"
ENCRYPTED_BASENAME="mailer-${TIMESTAMP}.db.age"
PLAINTEXT="$BACKUP_DIR/$BASENAME"
ENCRYPTED="$BACKUP_DIR/$ENCRYPTED_BASENAME"

mkdir -p "$BACKUP_DIR"
[ ! -e "$PLAINTEXT" ] && [ ! -L "$PLAINTEXT" ] || {
  _STATUS_STAGE="preflight"
  echo "ERROR: plaintext backup destination already exists" >&2
  exit 1
}
[ ! -e "$ENCRYPTED" ] && [ ! -L "$ENCRYPTED" ] || {
  _STATUS_STAGE="preflight"
  echo "ERROR: encrypted backup destination already exists" >&2
  exit 1
}

_STATUS_STAGE="database"
_write_attempt_receipt "running" ""
echo "[1/5] Taking SQLite backup..."
_PLAINTEXT_OWNED=1
docker compose --env-file "$COMPOSE_DIR/.env" -f "$COMPOSE_DIR/compose.yml" \
  exec -T mailer \
  ./Amane.Mailer db backup "/app/data/backups/$BASENAME"

_STATUS_STAGE="encrypt"
_write_attempt_receipt "running" ""
echo "[2/5] Encrypting with age..."
_ENCRYPTED_OWNED=1
age --encrypt \
  --recipient "$MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY" \
  --output "$ENCRYPTED" \
  "$PLAINTEXT"

echo "[3/5] Validating encrypted file..."
if [ ! -s "$ENCRYPTED" ]; then
  echo "ERROR: encrypted file is missing or empty: $ENCRYPTED" >&2
  exit 1
fi
_STATUS_ARTIFACT="$ENCRYPTED_BASENAME"

_STATUS_STAGE="validate-local"
_write_attempt_receipt "running" ""
echo "[4/5] Removing plaintext backup..."
rm -f "$PLAINTEXT"
_PLAINTEXT_OWNED=0

_STATUS_STAGE="upload"
echo "[5/5] Uploading to offsite storage..."
if [ -n "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  _STATUS_OFFSITE="pending"
  _write_attempt_receipt "running" ""
  if [ -f "$MAILER_BACKUP_RCLONE_CONFIG_PATH" ]; then
    rclone copy --config "$MAILER_BACKUP_RCLONE_CONFIG_PATH" "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
  else
    rclone copy "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
  fi
  _STATUS_OFFSITE="succeeded"
  _write_status_receipt \
    "db-only.offsite-success.json" "offsite-success" "succeeded" \
    "$_STATUS_STARTED_AT" "$(_utc_now)" "upload" "succeeded" "$ENCRYPTED_BASENAME"
else
  echo "Skipping upload (MAILER_BACKUP_RCLONE_REMOTE not set, MAILER_BACKUP_REQUIRE_OFFSITE=false)"
  _STATUS_OFFSITE="skipped"
fi

_DONE=1
_STATUS_STAGE="complete"
_STATUS_ARTIFACT="$ENCRYPTED_BASENAME"
_completed_at="$(_utc_now)"
_write_status_receipt \
  "db-only.success.json" "success" "succeeded" \
  "$_STATUS_STARTED_AT" "$_completed_at" "complete" "$_STATUS_OFFSITE" "$ENCRYPTED_BASENAME"
_write_attempt_receipt "succeeded" "$_completed_at"
echo "Backup complete: $ENCRYPTED_BASENAME"

if [ -n "$MAILER_BACKUP_PING_URL" ]; then
  curl -fsS --max-time 10 "$MAILER_BACKUP_PING_URL" > /dev/null || true
fi
