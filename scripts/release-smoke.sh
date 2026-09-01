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
# Dependencies: bash, curl, sha256sum, docker (with the compose plugin).
#
# Required environment (exactly one):
#   MAILER_IMAGE_TAG         e.g. v1.3.6 or sha-<40hex>
#   MAILER_IMAGE_DIGEST      e.g. sha256:<64-lowercase-hex>
#
# Optional environment:
#   MAILER_IMAGE_REPOSITORY  default ghcr.io/kooiei-in4a/amane-mailer
#   MAILER_IMAGE_PLATFORM    default linux/amd64
#   MAILER_PULL_POLICY       default always
#   MAILPIT_IMAGE            default axllent/mailpit:latest
#   MAILER_HTTP_PORT         default 15280
#   MAILPIT_HTTP_PORT        default 18025
#   MAIL_SERVICE_TOKEN       default local-mail-service-token
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
export MAIL_SERVICE_TOKEN="${MAIL_SERVICE_TOKEN:-local-mail-service-token}"

release_smoke_preflight_run "$REPO_ROOT"
COMPOSE=(docker compose -p "$RELEASE_SMOKE_PROJECT" -f "$RELEASE_SMOKE_COMPOSE_FILE")

MAILER_URL="http://127.0.0.1:${MAILER_HTTP_PORT}"
MAILPIT_URL="http://127.0.0.1:${MAILPIT_HTTP_PORT}"

TENANT_ID="00000000-0000-0000-0000-000000000101"
SOURCE_SERVICE="example-service"
TO_EMAIL="release-smoke@example.invalid"
PURPOSE="ReleaseSmoke"
TEXT_BODY="Amane release smoke. Mailpit delivery only."
SUBJECT_OK="Amane release smoke"
SUBJECT_CONFLICT="Amane release smoke (conflict)"
REQUEST_ID_OK="00000000-0000-0000-0000-000000000201"
REQUEST_ID_401="00000000-0000-0000-0000-000000000202"
REQUEST_ID_403="00000000-0000-0000-0000-000000000203"

PASS_COUNT=0
FAIL_COUNT=0

log()  { printf '%s\n' "$*"; }
pass() { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); printf '[FAIL] %s -- %s\n' "$1" "$2"; }

SHA256_CMD=""
detect_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then SHA256_CMD="sha256sum"
  elif command -v shasum >/dev/null 2>&1; then SHA256_CMD="shasum -a 256"
  elif command -v openssl >/dev/null 2>&1; then SHA256_CMD="openssl"
  else return 1
  fi
}

require_hash_tool() {
  detect_sha256 || {
    log "[error] missing required tools: sha256sum|shasum|openssl"
    exit 2
  }
}

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

canonical_payload() {
  printf '{"purpose":"%s","source_service":"%s","subject":"%s","text_body":"%s","to":[{"email":"%s"}]}' \
    "$PURPOSE" "$2" "$1" "$TEXT_BODY" "$TO_EMAIL"
}

payload_hash() {
  if [ "$SHA256_CMD" = "openssl" ]; then
    printf '%s' "$1" | openssl dgst -sha256 | awk '{print $NF}'
  else
    printf '%s' "$1" | $SHA256_CMD | awk '{print $1}'
  fi
}

request_json() {
  printf '{"tenant_id":"%s","source_service":"%s","mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s","payload_hash":"%s"}' \
    "$TENANT_ID" "$2" "$1" "$PURPOSE" "$TO_EMAIL" "$3" "$TEXT_BODY" "$4"
}

post_mail_request() {
  local token="$1" json="$2" raw
  raw="$(curl -sS -m 30 -o - -w $'\n__STATUS__%{http_code}' \
    -X POST "$MAILER_URL/internal/mail-requests" \
    -H "Authorization: Bearer $token" \
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

require_hash_tool

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

CANON_OK="$(canonical_payload "$SUBJECT_OK" "$SOURCE_SERVICE")"
HASH_OK="$(payload_hash "$CANON_OK")"
JSON_OK="$(request_json "$REQUEST_ID_OK" "$SOURCE_SERVICE" "$SUBJECT_OK" "$HASH_OK")"
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"accepted"'; then
  pass "POST /internal/mail-requests -> 202 accepted"
else
  fail "POST /internal/mail-requests" "expected 202 accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

if mailpit_received_subject "$SUBJECT_OK"; then
  pass "Mailpit received '$SUBJECT_OK'"
else
  fail "Mailpit delivery" "message '$SUBJECT_OK' not found in Mailpit within timeout"
fi

post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"already_accepted"'; then
  pass "Repost same id+payload -> 202 already_accepted"
else
  fail "Repost same id+payload" "expected 202 already_accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

CANON_CONFLICT="$(canonical_payload "$SUBJECT_CONFLICT" "$SOURCE_SERVICE")"
HASH_CONFLICT="$(payload_hash "$CANON_CONFLICT")"
JSON_CONFLICT="$(request_json "$REQUEST_ID_OK" "$SOURCE_SERVICE" "$SUBJECT_CONFLICT" "$HASH_CONFLICT")"
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_CONFLICT"
if [ "$HTTP_STATUS" = "409" ] && printf '%s' "$RESP_BODY" | grep -q 'IDEMPOTENCY_CONFLICT'; then
  pass "Repost same id+different payload -> 409 IDEMPOTENCY_CONFLICT"
else
  fail "Repost same id+different payload" "expected 409 IDEMPOTENCY_CONFLICT, got $HTTP_STATUS body=$RESP_BODY"
fi

JSON_401="$(request_json "$REQUEST_ID_401" "$SOURCE_SERVICE" "$SUBJECT_OK" "$HASH_OK")"
post_mail_request "invalid-release-smoke-token" "$JSON_401"
if [ "$HTTP_STATUS" = "401" ] && printf '%s' "$RESP_BODY" | grep -q 'UNAUTHORIZED_TENANT'; then
  pass "Invalid token -> 401 UNAUTHORIZED_TENANT"
else
  fail "Invalid token" "expected 401 UNAUTHORIZED_TENANT, got $HTTP_STATUS body=$RESP_BODY"
fi

UNKNOWN_SERVICE="unknown-service"
CANON_403="$(canonical_payload "$SUBJECT_OK" "$UNKNOWN_SERVICE")"
HASH_403="$(payload_hash "$CANON_403")"
JSON_403="$(request_json "$REQUEST_ID_403" "$UNKNOWN_SERVICE" "$SUBJECT_OK" "$HASH_403")"
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_403"
if [ "$HTTP_STATUS" = "403" ] && printf '%s' "$RESP_BODY" | grep -q 'SOURCE_SERVICE_NOT_ALLOWED'; then
  pass "Unknown source_service -> 403 SOURCE_SERVICE_NOT_ALLOWED"
else
  fail "Unknown source_service" "expected 403 SOURCE_SERVICE_NOT_ALLOWED, got $HTTP_STATUS body=$RESP_BODY"
fi

log ""
log "Smoke result: ${PASS_COUNT} passed, ${FAIL_COUNT} failed"
[ "$FAIL_COUNT" -eq 0 ]
