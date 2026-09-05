#!/usr/bin/env bash
# Test double only: emulate the Compose ps query used by the cold-backup preflight.
set -Eeuo pipefail

if [ "${1:-}" = "compose" ] && [ "${BACKUP_TEST_DOCKER_RUNNING:-false}" = "true" ]; then
  for argument in "$@"; do
    if [ "$argument" = "ps" ]; then
      printf '%s\n' mailer
      exit 0
    fi
  done
fi

exit 0
