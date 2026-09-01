#!/usr/bin/env bash
# Live Docker isolation self-test for release smoke (issue #506).
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"

CANARY_PROJECT='amane-mailer-release-smoke-canary-506'
CANARY_VOLUME="${CANARY_PROJECT}_canary-data"
CANARY_CONTAINER='release-smoke-canary-506'
SMOKE_PROJECT='amane-mailer-release-smoke-isolation-test'
PASS_COUNT=0
FAIL_COUNT=0

pass() { echo "[PASS] $1"; PASS_COUNT=$((PASS_COUNT + 1)); }
fail() { echo "[FAIL] $1" >&2; FAIL_COUNT=$((FAIL_COUNT + 1)); }

require_docker() {
  command -v docker >/dev/null 2>&1 || { echo '[error] docker required'; exit 2; }
  docker compose version >/dev/null 2>&1 || { echo '[error] docker compose required'; exit 2; }
}

snapshot_canary() {
  CANARY_CONTAINER_ID="$(docker ps -aq -f "name=^/${CANARY_CONTAINER}$" | head -1)"
  CANARY_VOLUME_EXISTS=0
  if docker volume inspect "$CANARY_VOLUME" >/dev/null 2>&1; then
    CANARY_VOLUME_EXISTS=1
    CANARY_VOLUME_CREATED="$(docker volume inspect -f '{{.CreatedAt}}' "$CANARY_VOLUME")"
  fi
}

assert_canary_unchanged() {
  local label="$1"
  local after_id after_created
  after_id="$(docker ps -aq -f "name=^/${CANARY_CONTAINER}$" | head -1)"
  if [[ "${CANARY_CONTAINER_ID:-}" != "${after_id:-}" ]]; then
    fail "${label}: canary container changed"
    return
  fi
  if [[ "${CANARY_VOLUME_EXISTS:-0}" -eq 1 ]]; then
    if ! docker volume inspect "$CANARY_VOLUME" >/dev/null 2>&1; then
      fail "${label}: canary volume removed"
      return
    fi
    after_created="$(docker volume inspect -f '{{.CreatedAt}}' "$CANARY_VOLUME")"
    if [[ "${CANARY_VOLUME_CREATED}" != "${after_created}" ]]; then
      fail "${label}: canary volume recreated"
      return
    fi
  fi
  pass "${label}"
}

cleanup_all() {
  docker rm -f "$CANARY_CONTAINER" >/dev/null 2>&1 || true
  docker volume rm "$CANARY_VOLUME" >/dev/null 2>&1 || true
  RELEASE_SMOKE_PROJECT="$SMOKE_PROJECT" docker compose -p "$SMOKE_PROJECT" \
    -f "$REPO_ROOT/infra/docker/docker-compose.release-smoke.yml" \
    down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup_all EXIT

require_docker

docker rm -f "$CANARY_CONTAINER" >/dev/null 2>&1 || true
docker volume rm "$CANARY_VOLUME" >/dev/null 2>&1 || true
docker volume create "$CANARY_VOLUME" >/dev/null
docker run -d --name "$CANARY_CONTAINER" \
  --mount "source=${CANARY_VOLUME},target=/data" \
  busybox:1.37 sleep 3600 >/dev/null

snapshot_canary

# Success path
if MAILER_IMAGE_TAG='v1.3.6' \
  RELEASE_SMOKE_PROJECT="$SMOKE_PROJECT" \
  MAILER_HTTP_PORT='15281' \
  MAILPIT_HTTP_PORT='18026' \
  bash "$REPO_ROOT/scripts/release-smoke.sh"; then
  pass 'live smoke success path'
else
  fail 'live smoke success path'
fi
assert_canary_unchanged 'N4 canary preserved on success'

# Failure path: image not present locally with pull_policy never
docker compose -p "$SMOKE_PROJECT" -f "$REPO_ROOT/infra/docker/docker-compose.release-smoke.yml" \
  down -v --remove-orphans >/dev/null 2>&1 || true
snapshot_canary
if MAILER_IMAGE_TAG='v1.3.6-nonexistent-smoke-tag-506' \
  MAILER_PULL_POLICY='never' \
  RELEASE_SMOKE_PROJECT="$SMOKE_PROJECT" \
  MAILER_HTTP_PORT='15282' \
  MAILPIT_HTTP_PORT='18027' \
  bash "$REPO_ROOT/scripts/release-smoke.sh" >/dev/null 2>&1; then
  fail 'expected failure path smoke to fail'
else
  pass 'failure path smoke failed as expected'
fi
assert_canary_unchanged 'N5 canary preserved on failure'

if docker ps -aq -f "label=com.docker.compose.project=${SMOKE_PROJECT}" | grep -q .; then
  fail 'release-smoke project containers remain after cleanup'
else
  pass 'release-smoke project cleaned after failure'
fi

if [[ "$FAIL_COUNT" -ne 0 ]]; then
  echo "[info] release-smoke-isolation-self-test failures: ${FAIL_COUNT}" >&2
  exit 1
fi

echo "[info] release-smoke-isolation-self-test passed (passes=${PASS_COUNT})"
