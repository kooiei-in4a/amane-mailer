#!/usr/bin/env bash
# Verify local compose can write SQLite on a fresh data/mailer bind mount (issue #52).
#
# On Linux/macOS, Docker auto-creates a missing bind-mount directory as root-owned
# (mode 755). The Mailer image runs as a non-root user, so db migrate fails with
# "SQLite Error 14: unable to open database file" unless data-init runs first.
#
# Dependencies: bash, docker (with the compose plugin).
#
# Config via environment (all optional):
#   LOCAL_COMPOSE_INIT_IMAGE  default busybox:1.37
#   MAILER_DATA_PATH          default <repo>/data/mailer
#   LOCAL_FRESH_DATA_KEEP     set to 1 to keep data/mailer after the check
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
COMPOSE_FILE="$REPO_ROOT/infra/docker/docker-compose.local.yml"
DATA_DIR="${MAILER_DATA_PATH:-$REPO_ROOT/data/mailer}"

export LOCAL_COMPOSE_INIT_IMAGE="${LOCAL_COMPOSE_INIT_IMAGE:-busybox:1.37}"
export MAILER_DATA_PATH="$DATA_DIR"
export MAILER_TENANTS_PATH="${MAILER_TENANTS_PATH:-/app/config/mailer/tenants.example.json}"
export MAIL_SERVICE_TOKEN="${MAIL_SERVICE_TOKEN:-local-mail-service-token}"

COMPOSE=(docker compose -f "$COMPOSE_FILE")

pass_count=0
fail_count=0

pass() {
  pass_count=$((pass_count + 1))
  printf '[PASS] %s\n' "$1"
}

fail() {
  fail_count=$((fail_count + 1))
  printf '[FAIL] %s\n' "$1" >&2
}

cleanup() {
  if [[ "${LOCAL_FRESH_DATA_KEEP:-}" == "1" ]]; then
    return
  fi

  rm -rf "$DATA_DIR"
}

trap cleanup EXIT

if [[ -e "$DATA_DIR" ]]; then
  rm -rf "$DATA_DIR"
fi

"${COMPOSE[@]}" build mailer-migrate

if "${COMPOSE[@]}" run --rm mailer-migrate; then
  pass "mailer-migrate completed on fresh bind mount"
else
  fail "mailer-migrate failed on fresh bind mount"
fi

if [[ -f "$DATA_DIR/mailer.db" ]]; then
  pass "data/mailer/mailer.db exists"
else
  fail "data/mailer/mailer.db missing after migrate"
fi

printf '\nFresh data check: %d passed, %d failed\n' "$pass_count" "$fail_count"

if [[ "$fail_count" -gt 0 ]]; then
  exit 1
fi
