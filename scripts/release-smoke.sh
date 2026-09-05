#!/usr/bin/env bash
# Clean-state release smoke for the published Mailer image (issue #11, #506).
#
# Pulls a explicitly supplied Mailer release artifact, starts Mailer + Mailpit
# from a clean compose project and named volume, and exercises the public release
# runtime path end to end.
#
# Each check prints [PASS]/[FAIL] with the failing detail, and the compose
# project + volume are removed on exit (including on failure).
#
# Dependencies: bash, curl, docker (with the compose plugin).
#
# Required environment (exactly one image selector):
#   MAILER_IMAGE_TAG         e.g. v1.3.6 or sha-<40hex>
#   MAILER_IMAGE_DIGEST      e.g. sha256:<64-lowercase-hex>
#
# Required authentication:
#   MAILER_API_KEY           managed API key for a Sender already provisioned
#                            in the target data volume (do not bootstrap here)
#
# Optional environment:
#   MAILER_IMAGE_REPOSITORY  default ghcr.io/kooiei-in4a/amane-mailer
#   MAILER_IMAGE_PLATFORM    default linux/amd64
#   MAILER_PULL_POLICY       default always
#   MAILPIT_IMAGE            default axllent/mailpit:latest
#   MAILER_HTTP_PORT         default 15280
#   MAILPIT_HTTP_PORT        default 18025
#   RELEASE_SMOKE_PROJECT    default amane-mailer-release-smoke
#   RELEASE_SMOKE_KEEP       set to 1 to skip cleanup (debugging only)
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
# shellcheck source=lib/release-smoke-preflight.sh
source "$SCRIPT_DIR/lib/release-smoke-preflight.sh"

export MAILER_IMAGE_PLATFORM="${MAILER_IMAGE_PLATFORM:-linux/amd64}"
export MAILER_PULL_POLICY="${MAILER_PULL_POLICY:-always}"
export MAILPIT_IMAGE="${MAILPIT_IMAGE:-axllent/mailpit:latest}"
export MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-15280}"
export MAILPIT_HTTP_PORT="${MAILPIT_HTTP_PORT:-18025}"

if [ -z "${MAILER_API_KEY:-}" ]; then
  printf '%s\n' "[error] MAILER_API_KEY is required." >&2
  printf '%s\n' "Provide a managed API key for the Sender used by this smoke test." >&2
  exit 2
fi

release_smoke_preflight_run "$REPO_ROOT"
COMPOSE=(docker compose -p "$RELEASE_SMOKE_PROJECT" -f "$RELEASE_SMOKE_COMPOSE_FILE")

MAILER_URL="http://127.0.0.1:${MAILER_HTTP_PORT}"
MAILPIT_URL="http://127.0.0.1:${MAILPIT_HTTP_PORT}"

TO_EMAIL="release-smoke@example.invalid"
PURPOSE="ReleaseSmoke"
TEXT_BODY="Amane release smoke. Mailpit delivery only."
SUBJECT_OK="Amane release smoke"
SUBJECT_CONFLICT="Amane release smoke (conflict)"
REQUEST_ID_OK="00000000-0000-0000-0000-000000000201"
REQUEST_ID_401="00000000-0000-0000-0000-000000000202"

PASS_COUNT=0
FAIL_COUNT=0

log()  { printf '%s\n' "$*"; }
pass() { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); printf '[FAIL] %s -- %s\n' "$1" "$2"; }

cleanup() {
  local status=$?
  if [ "${RELEASE_SMOKE_KEEP:-0}" = "1" ]; then
    log ""
    log "[cleanup] RELEASE_SMOKE_KEEP=1 set; leaving project '$RELEASE_SMOKE_PROJECT' running."
  else
    log ""
    log "[cleanup] removing compose project '$RELEASE_SMOKE_PROJECT' and its volume"
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

request_json() {
  printf '{"mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s"}' \
    "$1" "$PURPOSE" "$TO_EMAIL" "$2" "$TEXT_BODY"
}

post_mail_request() {
  local api_key="$1" json="$2" raw
  raw="$(curl -sS -m 30 -o - -w $'\n__STATUS__%{http_code}' \
    -X POST "$MAILER_URL/api/mail-requests" \
    -H "Authorization: Bearer $api_key" \
    -H "Content-Type: application/json" \
    --data "$json" 2>&1)" || true
  HTTP_STATUS="${raw##*__STATUS__}"
  RESP_BODY="${raw%$'\n'__STATUS__*}"
}

http_get_status() {
  curl -sS -m 15 -o /dev/null -w '%{http_code}' "$MAILER_URL$1" 2>/dev/null || echo "000"
}

wait_for_http() {
  local path="$1" i status
  for i in $(seq 1 30); do
    status="$(http_get_status "$path")"
    [ "$status" = "200" ] && return 0
    sleep 2
  done
  return 1
}

mailpit_received_subject() {
  local subject="$1" i body
  for i in $(seq 1 30); do
    body="$(curl -sS -m 15 "$MAILPIT_URL/api/v1/messages" 2>/dev/null || true)"
    if printf '%s' "$body" | grep -qF "$subject"; then
      return 0
    fi
    sleep 1
  done
  return 1
}

log "== Amane Mailer release smoke =="
log "image:   ${MAILER_IMAGE_REFERENCE}"
log "project: ${RELEASE_SMOKE_PROJECT}"
log "mailer:  ${MAILER_URL}"
log "mailpit: ${MAILPIT_URL}"
log ""

log "[setup] removing any previous '$RELEASE_SMOKE_PROJECT' project"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true

log "[setup] starting Mailer + Mailpit (pull policy: ${MAILER_PULL_POLICY})"
if ! "${COMPOSE[@]}" up -d --wait; then
  fail "compose up" "Mailer/Mailpit did not become healthy; recent logs follow"
  "${COMPOSE[@]}" ps || true
  "${COMPOSE[@]}" logs --no-color --tail 60 || true
  log ""
  log "Smoke result: 0 passed, ${FAIL_COUNT} failed"
  exit 1
fi

if wait_for_http "/healthz"; then
  pass "GET /healthz -> 200"
else
  fail "GET /healthz" "no 200 from $MAILER_URL/healthz within timeout"
fi

if wait_for_http "/readyz"; then
  pass "GET /readyz -> 200"
else
  fail "GET /readyz" "no 200 from $MAILER_URL/readyz within timeout"
fi

JSON_OK="$(request_json "$REQUEST_ID_OK" "$SUBJECT_OK")"
post_mail_request "$MAILER_API_KEY" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"accepted"'; then
  pass "POST /api/mail-requests -> 202 accepted"
else
  fail "POST /api/mail-requests" "expected 202 accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

if mailpit_received_subject "$SUBJECT_OK"; then
  pass "Mailpit received '$SUBJECT_OK'"
else
  fail "Mailpit delivery" "message '$SUBJECT_OK' not found in Mailpit within timeout"
fi

post_mail_request "$MAILER_API_KEY" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"already_accepted"'; then
  pass "Repost same id+payload -> 202 already_accepted"
else
  fail "Repost same id+payload" "expected 202 already_accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

JSON_CONFLICT="$(request_json "$REQUEST_ID_OK" "$SUBJECT_CONFLICT")"
post_mail_request "$MAILER_API_KEY" "$JSON_CONFLICT"
if [ "$HTTP_STATUS" = "409" ] && printf '%s' "$RESP_BODY" | grep -q 'IDEMPOTENCY_CONFLICT'; then
  pass "Repost same id+different payload -> 409 IDEMPOTENCY_CONFLICT"
else
  fail "Repost same id+different payload" "expected 409 IDEMPOTENCY_CONFLICT, got $HTTP_STATUS body=$RESP_BODY"
fi

JSON_401="$(request_json "$REQUEST_ID_401" "$SUBJECT_OK")"
post_mail_request "invalid-release-smoke-api-key" "$JSON_401"
if [ "$HTTP_STATUS" = "401" ] && printf '%s' "$RESP_BODY" | grep -q 'UNAUTHORIZED'; then
  pass "Invalid API key -> 401 UNAUTHORIZED"
else
  fail "Invalid API key" "expected 401 UNAUTHORIZED, got $HTTP_STATUS body=$RESP_BODY"
fi

log ""
log "Smoke result: ${PASS_COUNT} passed, ${FAIL_COUNT} failed"
[ "$FAIL_COUNT" -eq 0 ]
