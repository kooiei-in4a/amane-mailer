#!/usr/bin/env bash
# Clean-state release smoke for the published Mailer image (issue #11).
#
# Pulls ghcr.io/kooiei-in4a/amane-mailer:v0.1.0, starts Mailer + Mailpit from a
# clean compose project and named volume, and exercises the public release
# runtime path end to end:
#
#   - GET  /healthz                        -> 200
#   - GET  /readyz                         -> 200
#   - POST /internal/mail-requests (ok)    -> 202 accepted
#   - Mailpit receives the message
#   - same id + same payload               -> 202 already_accepted
#   - same id + different payload          -> 409 IDEMPOTENCY_CONFLICT
#   - invalid token                        -> 401 UNAUTHORIZED_TENANT
#   - unknown source_service               -> 403 SOURCE_SERVICE_NOT_ALLOWED
#
# Each check prints [PASS]/[FAIL] with the failing detail, and the compose
# project + volume are removed on exit (including on failure).
#
# Dependencies: bash, curl, sha256sum, docker (with the compose plugin).
#
# Config via environment (all optional):
#   MAILER_IMAGE_REPOSITORY  default ghcr.io/kooiei-in4a/amane-mailer
#   MAILER_IMAGE_TAG         default v0.1.0
#   MAILER_PULL_POLICY       default always   (set "missing" to reuse a local image)
#   MAILER_HTTP_PORT         default 15280     (host port for Mailer)
#   MAILPIT_HTTP_PORT        default 18025     (host port for Mailpit API/UI)
#   MAIL_SERVICE_TOKEN       default local-mail-service-token
#   RELEASE_SMOKE_PROJECT    default amane-mailer-release-smoke
#   RELEASE_SMOKE_KEEP       set to 1 to skip cleanup (debugging only)
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
COMPOSE_FILE="$REPO_ROOT/infra/docker/docker-compose.release-smoke.yml"

export MAILER_IMAGE_REPOSITORY="${MAILER_IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}"
export MAILER_IMAGE_TAG="${MAILER_IMAGE_TAG:-v0.1.0}"
export MAILER_PULL_POLICY="${MAILER_PULL_POLICY:-always}"
export MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-15280}"
export MAILPIT_HTTP_PORT="${MAILPIT_HTTP_PORT:-18025}"
export MAIL_SERVICE_TOKEN="${MAIL_SERVICE_TOKEN:-local-mail-service-token}"
export RELEASE_SMOKE_PROJECT="${RELEASE_SMOKE_PROJECT:-amane-mailer-release-smoke}"

MAILER_URL="http://127.0.0.1:${MAILER_HTTP_PORT}"
MAILPIT_URL="http://127.0.0.1:${MAILPIT_HTTP_PORT}"
COMPOSE=(docker compose -f "$COMPOSE_FILE")

# Fixed example-tenant values from config/mailer/tenants.example.json. The
# compose project starts from a clean volume each run, so fixed ids are safe.
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

require_deps() {
  local missing=()
  command -v docker >/dev/null 2>&1 || missing+=("docker")
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  command -v awk >/dev/null 2>&1 || missing+=("awk")
  detect_sha256 || missing+=("sha256sum|shasum|openssl")
  if [ "${#missing[@]}" -gt 0 ]; then
    log "[error] missing required tools: ${missing[*]}"
    exit 2
  fi
  if ! docker compose version >/dev/null 2>&1; then
    log "[error] 'docker compose' plugin is not available"
    exit 2
  fi
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

# Delivery fields only, sorted by key, matching MailPayloadHasher canonical form.
# All values below are free of JSON-special characters, so plain interpolation is safe.
canonical_payload() { # subject source_service
  printf '{"purpose":"%s","source_service":"%s","subject":"%s","text_body":"%s","to":[{"email":"%s"}]}' \
    "$PURPOSE" "$2" "$1" "$TEXT_BODY" "$TO_EMAIL"
}

payload_hash() { # canonical-json
  if [ "$SHA256_CMD" = "openssl" ]; then
    printf '%s' "$1" | openssl dgst -sha256 | awk '{print $NF}'
  else
    printf '%s' "$1" | $SHA256_CMD | awk '{print $1}'
  fi
}

request_json() { # mail_request_id source_service subject payload_hash
  printf '{"tenant_id":"%s","source_service":"%s","mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s","payload_hash":"%s"}' \
    "$TENANT_ID" "$2" "$1" "$PURPOSE" "$TO_EMAIL" "$3" "$TEXT_BODY" "$4"
}

# Performs a POST and sets HTTP_STATUS + RESP_BODY.
post_mail_request() { # token json
  local token="$1" json="$2" raw
  raw="$(curl -sS -m 30 -o - -w $'\n__STATUS__%{http_code}' \
    -X POST "$MAILER_URL/internal/mail-requests" \
    -H "Authorization: Bearer $token" \
    -H "Content-Type: application/json" \
    --data "$json" 2>&1)" || true
  HTTP_STATUS="${raw##*__STATUS__}"
  RESP_BODY="${raw%$'\n'__STATUS__*}"
}

http_get_status() { # path
  curl -sS -m 15 -o /dev/null -w '%{http_code}' "$MAILER_URL$1" 2>/dev/null || echo "000"
}

wait_for_http() { # path
  local path="$1" i status
  for i in $(seq 1 30); do
    status="$(http_get_status "$path")"
    [ "$status" = "200" ] && return 0
    sleep 2
  done
  return 1
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

log "== Amane Mailer release smoke =="
log "image:   ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}"
log "project: ${RELEASE_SMOKE_PROJECT}"
log "mailer:  ${MAILER_URL}"
log "mailpit: ${MAILPIT_URL}"
log ""

require_deps

# Start from a clean state in case a previous run left the project behind.
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

# Host-published port can lag container health by a moment.
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

# Happy path: 202 accepted.
CANON_OK="$(canonical_payload "$SUBJECT_OK" "$SOURCE_SERVICE")"
HASH_OK="$(payload_hash "$CANON_OK")"
JSON_OK="$(request_json "$REQUEST_ID_OK" "$SOURCE_SERVICE" "$SUBJECT_OK" "$HASH_OK")"
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"accepted"'; then
  pass "POST /internal/mail-requests -> 202 accepted"
else
  fail "POST /internal/mail-requests" "expected 202 accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

# Mailpit delivery.
if mailpit_received_subject "$SUBJECT_OK"; then
  pass "Mailpit received '$SUBJECT_OK'"
else
  fail "Mailpit delivery" "message '$SUBJECT_OK' not found in Mailpit within timeout"
fi

# Idempotent repost: same id + same payload -> already_accepted.
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_OK"
if [ "$HTTP_STATUS" = "202" ] && printf '%s' "$RESP_BODY" | grep -q '"status":"already_accepted"'; then
  pass "Repost same id+payload -> 202 already_accepted"
else
  fail "Repost same id+payload" "expected 202 already_accepted, got $HTTP_STATUS body=$RESP_BODY"
fi

# Conflict: same id + different payload -> 409.
CANON_CONFLICT="$(canonical_payload "$SUBJECT_CONFLICT" "$SOURCE_SERVICE")"
HASH_CONFLICT="$(payload_hash "$CANON_CONFLICT")"
JSON_CONFLICT="$(request_json "$REQUEST_ID_OK" "$SOURCE_SERVICE" "$SUBJECT_CONFLICT" "$HASH_CONFLICT")"
post_mail_request "$MAIL_SERVICE_TOKEN" "$JSON_CONFLICT"
if [ "$HTTP_STATUS" = "409" ] && printf '%s' "$RESP_BODY" | grep -q 'IDEMPOTENCY_CONFLICT'; then
  pass "Repost same id+different payload -> 409 IDEMPOTENCY_CONFLICT"
else
  fail "Repost same id+different payload" "expected 409 IDEMPOTENCY_CONFLICT, got $HTTP_STATUS body=$RESP_BODY"
fi

# Invalid token -> 401.
JSON_401="$(request_json "$REQUEST_ID_401" "$SOURCE_SERVICE" "$SUBJECT_OK" "$HASH_OK")"
post_mail_request "invalid-release-smoke-token" "$JSON_401"
if [ "$HTTP_STATUS" = "401" ] && printf '%s' "$RESP_BODY" | grep -q 'UNAUTHORIZED_TENANT'; then
  pass "Invalid token -> 401 UNAUTHORIZED_TENANT"
else
  fail "Invalid token" "expected 401 UNAUTHORIZED_TENANT, got $HTTP_STATUS body=$RESP_BODY"
fi

# Unknown source_service -> 403. Hash is computed for the unknown service so the
# rejection is the source-service check, not a payload-hash mismatch.
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
