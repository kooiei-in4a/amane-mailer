#!/usr/bin/env bash
# Test double only: emulate the Compose ps query used by the cold-backup preflight.
set -Eeuo pipefail

if [ "${1:-}" = "compose" ]; then
  has_exec=false
  has_ps=false
  for argument in "$@"; do
    case "$argument" in
      exec) has_exec=true ;;
      ps) has_ps=true ;;
    esac
  done

  if [ "$has_ps" = true ] && [ "${BACKUP_TEST_DOCKER_RUNNING:-false}" = "true" ]; then
    printf '%s\n' mailer
    exit 0
  fi

  if [ "$has_exec" = true ]; then
    [ "${BACKUP_TEST_DOCKER_FAIL_BACKUP:-false}" = "true" ] && exit 19
    destination="${@: -1}"
    case "$destination" in
      /app/data/backups/*) ;;
      *) exit 2 ;;
    esac
    [ -n "${BACKUP_TEST_DATA_DIR:-}" ] || exit 2
    mkdir -p -- "$BACKUP_TEST_DATA_DIR/backups"
    printf '%s\n' 'database-only-backup-fixture' > "$BACKUP_TEST_DATA_DIR/backups/${destination##*/}"
    exit 0
  fi
fi

exit 0
