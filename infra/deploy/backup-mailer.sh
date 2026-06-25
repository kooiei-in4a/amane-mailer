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

# Set trap immediately after env is loaded so all subsequent exits — including
# configuration errors below — send the /fail ping and clean up any temp files.
_DONE=0
_cleanup() {
  rm -f "${PLAINTEXT:-}"
  if [ "${_DONE}" -eq 0 ]; then
    rm -f "${ENCRYPTED:-}"
    if [ -n "${MAILER_BACKUP_PING_URL:-}" ]; then
      curl -fsS --max-time 10 "${MAILER_BACKUP_PING_URL}/fail" > /dev/null 2>&1 || true
    fi
  fi
}
trap _cleanup EXIT

: "${MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY:?MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY is not set in .env}"

MAILER_BACKUP_REQUIRE_OFFSITE="${MAILER_BACKUP_REQUIRE_OFFSITE:-true}"
MAILER_BACKUP_RCLONE_REMOTE="${MAILER_BACKUP_RCLONE_REMOTE:-}"
MAILER_BACKUP_RCLONE_CONFIG_PATH="${MAILER_BACKUP_RCLONE_CONFIG_PATH:-./rclone/rclone.conf}"
MAILER_BACKUP_PING_URL="${MAILER_BACKUP_PING_URL:-}"

if [ "$MAILER_BACKUP_REQUIRE_OFFSITE" = "true" ] && [ -z "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  echo "ERROR: MAILER_BACKUP_REQUIRE_OFFSITE=true but MAILER_BACKUP_RCLONE_REMOTE is not set" >&2
  exit 1
fi

# Normalize relative paths to COMPOSE_DIR so the script is cwd-independent.
_data_path="${MAILER_DATA_PATH:-./data}"
case "$_data_path" in
  /*) HOST_DATA_DIR="$_data_path" ;;
  *)  HOST_DATA_DIR="$COMPOSE_DIR/$_data_path" ;;
esac
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

echo "[1/5] Taking SQLite backup..."
docker compose --env-file "$COMPOSE_DIR/.env" -f "$COMPOSE_DIR/compose.yml" \
  exec -T mailer \
  ./Amane.Mailer db backup "/app/data/backups/$BASENAME"

echo "[2/5] Encrypting with age..."
age --encrypt \
  --recipient "$MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY" \
  --output "$ENCRYPTED" \
  "$PLAINTEXT"

echo "[3/5] Validating encrypted file..."
if [ ! -s "$ENCRYPTED" ]; then
  echo "ERROR: encrypted file is missing or empty: $ENCRYPTED" >&2
  exit 1
fi

echo "[4/5] Removing plaintext backup..."
rm -f "$PLAINTEXT"

echo "[5/5] Uploading to offsite storage..."
if [ -n "$MAILER_BACKUP_RCLONE_REMOTE" ]; then
  if [ -f "$MAILER_BACKUP_RCLONE_CONFIG_PATH" ]; then
    rclone copy --config "$MAILER_BACKUP_RCLONE_CONFIG_PATH" "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
  else
    rclone copy "$ENCRYPTED" "$MAILER_BACKUP_RCLONE_REMOTE"
  fi
else
  echo "Skipping upload (MAILER_BACKUP_RCLONE_REMOTE not set, MAILER_BACKUP_REQUIRE_OFFSITE=false)"
fi

_DONE=1
echo "Backup complete: $ENCRYPTED_BASENAME"

if [ -n "$MAILER_BACKUP_PING_URL" ]; then
  curl -fsS --max-time 10 "$MAILER_BACKUP_PING_URL" > /dev/null || true
fi
