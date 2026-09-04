#!/usr/bin/env bash
# Zero-Admin local first-mail smoke (issue #146).
#
# Builds and starts local Mailer + Mailpit from infra/docker/docker-compose.local.yml,
# then verifies health/readiness, one accepted POST, and Mailpit delivery.
# Narrower than scripts/release-smoke.sh: no Admin UI, ACS, Dead Letter, or 401/403/409 checks.
#
# Dependencies: bash, curl, docker (with the compose plugin), uuidgen or openssl.
#
# Config via environment (all optional):
#   MAILER_HTTP_PORT       default 5280
#   MAILPIT_HTTP_PORT      default 8025
#   MAILER_API_KEY         required; managed key for a Sender already provisioned in this data volume
#   LOCAL_FIRST_MAIL_SMOKE_KEEP  reserved (compose is left running after the script exits)
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
COMPOSE_FILE="$REPO_ROOT/infra/docker/docker-compose.local.yml"

export MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-5280}"
export MAILPIT_HTTP_PORT="${MAILPIT_HTTP_PORT:-8025}"
: "${MAILER_API_KEY:?MAILER_API_KEY must contain a managed API key for a provisioned Sender}"

MAILER_URL="http://127.0.0.1:${MAILER_HTTP_PORT}"
MAILPIT_URL="http://127.0.0.1:${MAILPIT_HTTP_PORT}"
COMPOSE=(docker compose -f "$COMPOSE_FILE")

PURPOSE="FormResponseNotification"
TO_EMAIL="admin@example.com"
SUBJECT="New response"
TEXT_BODY="A new response arrived."

PASS_COUNT=0
FAIL_COUNT=0
HTTP_STATUS=""
RESP_BODY=""

log()  { printf '%s\n' "$*"; }
pass() { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); printf '[FAIL] %s -- %s\n' "$1" "$2" >&2; }

require_deps() {
  local missing=()
  command -v docker >/dev/null 2>&1 || missing+=("docker")
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  if [ "${#missing[@]}" -gt 0 ]; then
    log "[error] missing required tools: ${missing[*]}"
    exit 2
  fi
  if ! docker compose version >/dev/null 2>&1; then
    log "[error] 'docker compose' plugin is not available"
    exit 2
  fi
}

new_request_id() {
  if command -v uuidgen >/dev/null 2>&1; then
    uuidgen | tr '[:upper:]' '[:lower:]'
    return
  fi
  if [ -r /proc/sys/kernel/random/uuid ]; then
    tr '[:upper:]' '[:lower:]' </proc/sys/kernel/random/uuid
    return
  fi
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 16 | sed 's/\(........\)\(....\)\(....\)\(....\)\(............\)/\1-\2-\3-\4-\5/'
    return
  fi
  log "[error] uuidgen, /proc/sys/kernel/random/uuid, or openssl is required"
  exit 2
}

show_failure_context() {
  log ""
  log "[diagnostics] docker compose ps"
  "${COMPOSE[@]}" ps || true
  log ""
  log "[diagnostics] mailer logs (tail 50)"
  "${COMPOSE[@]}" logs mailer --no-color --tail 50 || true
  log ""
  log "[diagnostics] mailpit logs (tail 30)"
  "${COMPOSE[@]}" logs mailpit --no-color --tail 30 || true
}

finish() {
  log ""
  log "First-mail smoke: ${PASS_COUNT} passed, ${FAIL_COUNT} failed"
  if [ "$FAIL_COUNT" -gt 0 ]; then
    show_failure_context
    exit 1
  fi
}

post_mail_request() { # json_file
  local json_file="$1" raw
  raw="$(curl -sS -m 30 -o - -w $'\n__STATUS__%{http_code}' \
    -X POST "$MAILER_URL/api/mail-requests" \
    -H "Authorization: Bearer $MAILER_API_KEY" \
    -H "Content-Type: application/json" \
    --data-binary @"$json_file" 2>&1)" || true
  HTTP_STATUS="${raw##*__STATUS__}"
  RESP_BODY="${raw%$'\n'__STATUS__*}"
}

mailpit_received_subject() { # subject
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

log "== Amane Mailer local first-mail smoke =="
log "compose: $COMPOSE_FILE"
log "mailer:  $MAILER_URL"
log "mailpit: $MAILPIT_URL"
log ""

require_deps

log "[setup] starting Mailer + Mailpit (build if needed)"
if ! "${COMPOSE[@]}" up -d --build --wait mailer; then
  fail "compose up" "Mailer did not become healthy"
  finish
fi
pass "compose up --wait mailer"

curl -fsS -X DELETE "$MAILPIT_URL/api/v1/messages" >/dev/null 2>&1 || true

if curl -fsS -m 15 "$MAILER_URL/healthz" | grep -q '"healthy":true'; then
  pass "GET /healthz -> healthy"
else
  fail "GET /healthz" "expected {\"healthy\":true} from $MAILER_URL/healthz"
fi

if curl -fsS -m 15 "$MAILER_URL/readyz" | grep -q '"ready":true'; then
  pass "GET /readyz -> ready"
else
  fail "GET /readyz" "expected {\"ready\":true} from $MAILER_URL/readyz"
fi

REQUEST_ID="$(new_request_id)"
JSON_FILE="$(mktemp)"
trap 'rm -f "$JSON_FILE"' EXIT
printf '%s' "$(printf '{"mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s"}' \
  "$REQUEST_ID" "$PURPOSE" "$TO_EMAIL" "$SUBJECT" "$TEXT_BODY")" >"$JSON_FILE"

post_mail_request "$JSON_FILE"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"accepted"'; then
  pass "POST /api/mail-requests -> 202 accepted"
else
  fail "POST /api/mail-requests" "expected 202 accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

if mailpit_received_subject "$SUBJECT"; then
  pass "Mailpit received '$SUBJECT'"
else
  fail "Mailpit delivery" "message '$SUBJECT' not found in Mailpit within 30 seconds"
fi

finish
