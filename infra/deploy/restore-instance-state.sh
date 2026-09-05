#!/usr/bin/env bash
# Restore a cold managed Mailer archive into a fresh, empty data directory.
# This script never stops/starts Compose services and never overwrites a non-empty target.
set -Eeuo pipefail
umask 077

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is not installed: $1"
}

usage() {
  cat >&2 <<'EOF'
Usage: restore-instance-state.sh --archive PATH --identity PATH --target ABSOLUTE_PATH \
  --runtime-uid UID --runtime-gid GID

The target must be a fresh or empty directory. The script does not overwrite it,
run migrations, or start services.
EOF
}

ARCHIVE=""
IDENTITY=""
TARGET=""
RUNTIME_UID="${MAILER_RUNTIME_UID:-}"
RUNTIME_GID="${MAILER_RUNTIME_GID:-}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --archive)
      [ "$#" -ge 2 ] || fail "--archive requires a path"
      ARCHIVE="$2"
      shift 2
      ;;
    --identity)
      [ "$#" -ge 2 ] || fail "--identity requires a path"
      IDENTITY="$2"
      shift 2
      ;;
    --target)
      [ "$#" -ge 2 ] || fail "--target requires a path"
      TARGET="$2"
      shift 2
      ;;
    --runtime-uid)
      [ "$#" -ge 2 ] || fail "--runtime-uid requires a numeric uid"
      RUNTIME_UID="$2"
      shift 2
      ;;
    --runtime-gid)
      [ "$#" -ge 2 ] || fail "--runtime-gid requires a numeric gid"
      RUNTIME_GID="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      usage
      fail "unknown argument: $1"
      ;;
  esac
done

[ -n "$ARCHIVE" ] || fail "--archive is required"
[ -n "$IDENTITY" ] || fail "--identity is required"
[ -n "$TARGET" ] || fail "--target is required"
case "$TARGET" in
  /*) ;;
  *) fail "--target must be an absolute path" ;;
esac
[ -n "$RUNTIME_UID" ] && [ -n "$RUNTIME_GID" ] \
  || fail "--runtime-uid and --runtime-gid are required"
[[ "$RUNTIME_UID" =~ ^[0-9]+$ ]] || fail "runtime uid must be numeric"
[[ "$RUNTIME_GID" =~ ^[0-9]+$ ]] || fail "runtime gid must be numeric"

require_command age
require_command tar
require_command stat
require_command find
require_command mktemp
require_command chmod
require_command chown

[ ! -L "$ARCHIVE" ] && [ -f "$ARCHIVE" ] || fail "encrypted archive is missing or unsafe"
[ ! -L "$IDENTITY" ] && [ -f "$IDENTITY" ] || fail "age identity is missing or unsafe"

identity_mode="$(stat -c '%a' -- "$IDENTITY")" || fail "could not inspect age identity permissions"
identity_mode_value=$((8#$identity_mode))
(( (identity_mode_value & 077) == 0 )) || fail "age identity must be owner-only"

case "$(basename -- "$ARCHIVE")" in
  mailer-state-*.tar.age) ;;
  *) fail "archive name must match mailer-state-*.tar.age" ;;
esac

if [ -e "$TARGET" ] || [ -L "$TARGET" ]; then
  [ ! -L "$TARGET" ] || fail "restore target must not be a symlink"
  [ -d "$TARGET" ] || fail "restore target exists but is not a directory"
  existing_entry="$(find -P "$TARGET" -mindepth 1 -print -quit)"
  [ -z "$existing_entry" ] || fail "restore target must be empty; refusing to overwrite existing state"
else
  mkdir -p -- "$TARGET"
fi
chmod 700 -- "$TARGET"

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/amane-mailer-instance-restore.XXXXXX")"
chmod 700 -- "$TEMP_DIR"
PLAINTEXT="$TEMP_DIR/instance.tar"
ENTRY_LIST="$TEMP_DIR/entries.txt"
VERBOSE_LIST="$TEMP_DIR/entries.verbose.txt"

cleanup() {
  local status=$?
  set +e
  rm -f -- "${PLAINTEXT:-}" "${ENTRY_LIST:-}" "${VERBOSE_LIST:-}"
  rm -rf -- "${TEMP_DIR:-}"
  trap - EXIT
  exit "$status"
}
trap cleanup EXIT

echo "[1/4] Decrypting instance archive..."
age --decrypt --identity "$IDENTITY" --output "$PLAINTEXT" "$ARCHIVE"
[ -s "$PLAINTEXT" ] || fail "decryption produced an empty archive"

echo "[2/4] Validating archive boundary..."
tar --list --file "$PLAINTEXT" > "$ENTRY_LIST" \
  || fail "archive listing could not be read"
tar --list --verbose --file "$PLAINTEXT" > "$VERBOSE_LIST" \
  || fail "archive entry metadata could not be read"

declare -A seen_entries=()
while IFS= read -r entry; do
  entry="${entry%/}"
  [ -n "$entry" ] || fail "archive contains an empty entry"
  case "$entry" in
    /*|.|./*|..|../*|*/../*|*/..|*\\*)
      fail "archive contains an unsafe path"
      ;;
  esac

  case "$entry" in
    mailer.db|secrets/acs/acs_connection_string|attachment-spool/committed)
      ;;
    attachment-spool/committed/*)
      spool_entry="${entry#attachment-spool/committed/}"
      if [[ "$spool_entry" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$ ]]; then
        :
      elif [[ "$spool_entry" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\.bin$ ]]; then
        :
      else
        fail "archive contains an unexpected attachment spool path"
      fi
      ;;
    *)
      fail "archive contains a path outside the managed Mailer state boundary"
      ;;
  esac

  [ -z "${seen_entries[$entry]+set}" ] || fail "archive contains a duplicate entry"
  seen_entries["$entry"]=1
done < "$ENTRY_LIST"

for required_entry in mailer.db secrets/acs/acs_connection_string attachment-spool/committed; do
  [ -n "${seen_entries[$required_entry]+set}" ] || fail "archive is missing required Mailer state"
done

while IFS= read -r verbose_entry; do
  case "${verbose_entry:0:1}" in
    d|-) ;;
    *) fail "archive contains a non-file/non-directory entry" ;;
  esac
done < "$VERBOSE_LIST"

echo "[3/4] Extracting into the empty target..."
tar --extract --file "$PLAINTEXT" --directory "$TARGET" \
  --no-same-owner --no-same-permissions --keep-old-files \
  || fail "archive extraction failed"

[ ! -L "$TARGET/mailer.db" ] && [ -f "$TARGET/mailer.db" ] \
  || fail "restored SQLite database is missing or unsafe"
[ ! -L "$TARGET/secrets/acs/acs_connection_string" ] \
  && [ -f "$TARGET/secrets/acs/acs_connection_string" ] \
  || fail "restored provider secret is missing or unsafe"
[ ! -L "$TARGET/attachment-spool/committed" ] \
  && [ -d "$TARGET/attachment-spool/committed" ] \
  || fail "restored committed attachment spool is missing or unsafe"
[ ! -e "$TARGET/attachment-spool/staging" ] && [ ! -L "$TARGET/attachment-spool/staging" ] \
  || fail "archive unexpectedly restored transient attachment staging"

chmod 600 -- "$TARGET/mailer.db" "$TARGET/secrets/acs/acs_connection_string"
chmod 700 -- "$TARGET/secrets" "$TARGET/secrets/acs" "$TARGET/attachment-spool" "$TARGET/attachment-spool/committed"
while IFS= read -r -d '' directory; do
  chmod 700 -- "$directory"
done < <(find -P "$TARGET/attachment-spool/committed" -type d -print0)
while IFS= read -r -d '' file_path; do
  [ ! -L "$file_path" ] || fail "restored attachment spool contains a symlink"
  chmod 600 -- "$file_path"
done < <(find -P "$TARGET/attachment-spool/committed" -type f -print0)

echo "[4/4] Applying runtime ownership..."
if [ "$RUNTIME_UID" != "$(id -u)" ] || [ "$RUNTIME_GID" != "$(id -g)" ]; then
  chown -R -- "$RUNTIME_UID:$RUNTIME_GID" "$TARGET"
fi

echo "Instance restore complete: $TARGET"
