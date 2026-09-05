#!/usr/bin/env bash
set -Eeuo pipefail
set +x

COMPOSE_DIR="${MAIL05A_COMPOSE_DIR:-/opt/amane-mailer}"
# Physical tenant_id for db CLI lookups equals the Sender ID that owns MAILER_API_KEY.
TENANT_DEVELOP="${MAIL05A_TENANT_DEVELOP:-00000000-0000-0000-0000-000000000101}"
# Compatibility-only persistence identity used by db request-state (not a Consumer field).
SOURCE_SERVICE="amane-v2-internal"
WORK_DIR="mail-05a-work"
CLIENT_COMPOSE="compose.mail-05a-client.yml"
WORKER_DISABLED_COMPOSE="compose.worker-disabled.yml"

cd "$COMPOSE_DIR"

COMPOSE_EXTRA=()
if [ -n "${MAIL05A_COMPOSE_EXTRA:-}" ]; then
  for f in $MAIL05A_COMPOSE_EXTRA; do
    COMPOSE_EXTRA+=( -f "$f" )
  done
fi

BASE=(docker compose --env-file .env -f compose.yml "${COMPOSE_EXTRA[@]}" -f "$CLIENT_COMPOSE")
DISABLED=(docker compose --env-file .env -f compose.yml "${COMPOSE_EXTRA[@]}" -f "$WORKER_DISABLED_COMPOSE" -f "$CLIENT_COMPOSE")
RESTORE_NEEDED=0
REQ401=""
REQ202=""

read_env_value() {
  local key="$1"
  python3 - "$key" <<'PY'
import os
import sys

key = sys.argv[1]
if key in os.environ:
    print(os.environ[key])
    raise SystemExit

try:
    with open(".env", "r", encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            name, value = line.split("=", 1)
            if name == key:
                value = value.strip()
                if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
                    value = value[1:-1]
                print(value)
                raise SystemExit
except FileNotFoundError:
    pass
PY
}

new_uuid() {
  python3 - <<'PY'
import uuid
print(uuid.uuid4())
PY
}

write_client_compose() {
  cat > "$CLIENT_COMPOSE" <<'YAML'
services:
  mail-05a-curl:
    image: ${MAIL05A_CURL_IMAGE:-curlimages/curl:8.11.1}
    user: "${MAIL05A_CURL_USER:-0:0}"
    profiles:
      - mail-05a
    restart: "no"
    networks:
      - internal
    volumes:
      - ./mail-05a-work:/mail-05a:ro
YAML
}

run_client() {
  local cmd_name="$1"
  shift
  local -n compose_cmd="$cmd_name"
  "${compose_cmd[@]}" run --rm --no-deps mail-05a-curl "$@"
}

wait_ready() {
  local label="$1"
  local cmd_name="$2"

  echo "[wait ready: $label]"
  for _ in $(seq 1 60); do
    if run_client "$cmd_name" -fsS "$MAILER_BASE_URL/readyz" >"$WORK_DIR/ready.out" 2>"$WORK_DIR/ready.err"; then
      cat "$WORK_DIR/ready.out"
      echo
      return 0
    fi
    sleep 1
  done

  echo "ready wait timed out for $label"
  cat "$WORK_DIR/ready.err" || true
  return 1
}

write_payload() {
  local path="$1"
  local request_id="$2"
  python3 - "$path" "$request_id" <<'PY'
import json
import sys

path, request_id = sys.argv[1], sys.argv[2]
request = {
    "mail_request_id": request_id,
    "purpose": "mail-05a-smoke",
    "to": [{"email": "mail-05a-smoke@example.invalid"}],
    "subject": "MAIL-05a smoke",
    "text_body": "MAIL-05a smoke test. No live delivery expected.",
}
with open(path, "w", encoding="utf-8") as f:
    json.dump(request, f, ensure_ascii=False, separators=(",", ":"))
PY
}

write_curl_config() {
  local path="$1"
  local api_key="$2"
  local payload_path="$3"

  (
    umask 077
    printf '%s\n' 'silent'
    printf '%s\n' 'show-error'
    printf '%s\n' 'request = "POST"'
    printf 'url = "%s/api/mail-requests"\n' "$MAILER_BASE_URL"
    printf 'header = "Authorization: Bearer %s"\n' "$api_key"
    printf '%s\n' 'header = "Content-Type: application/json"'
    printf 'data = "@/mail-05a/%s"\n' "$payload_path"
    printf '%s\n' 'write-out = "\nHTTP_STATUS=%{http_code}\n"'
  ) > "$path"
}

request_state() {
  local cmd_name="$1"
  local request_id="$2"
  local -n compose_cmd="$cmd_name"
  "${compose_cmd[@]}" exec -T mailer /app/Amane.Mailer db request-state \
    --tenant-id "$TENANT_DEVELOP" \
    --source-service "$SOURCE_SERVICE" \
    --mail-request-id "$request_id"
}

state_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); exit }'
}

restore_worker() {
  local status=$?
  if [ "$RESTORE_NEEDED" = "1" ]; then
    echo "[restore worker]"
    "${BASE[@]}" up -d --force-recreate mailer >/dev/null || status=1
    rm -f "$WORKER_DISABLED_COMPOSE" || status=1
    wait_ready restored BASE || status=1
    "${BASE[@]}" exec -T mailer /app/Amane.Mailer healthcheck || status=1
    echo WORKER_RESTORED
  fi

  rm -rf "$WORK_DIR" "$CLIENT_COMPOSE" || status=1
  [ -n "$REQ401" ] && echo "REQ401=$REQ401"
  [ -n "$REQ202" ] && echo "REQ202=$REQ202"
  exit "$status"
}
trap restore_worker EXIT

MAILER_HTTP_PORT_VALUE="$(read_env_value MAILER_HTTP_PORT || true)"
MAILER_HTTP_PORT_VALUE="${MAILER_HTTP_PORT_VALUE:-8080}"
MAILER_BASE_URL="http://mailer:${MAILER_HTTP_PORT_VALUE}"

echo "[precheck files]"
if [ -e "$WORKER_DISABLED_COMPOSE" ] || [ -e "$CLIENT_COMPOSE" ] || [ -e "$WORK_DIR" ]; then
  echo "temporary MAIL-05a file already exists; aborting"
  exit 1
fi
mkdir -p "$WORK_DIR"
chmod 700 "$WORK_DIR"
write_client_compose
echo "no temporary override files present"

echo "[precheck compose ps]"
"${BASE[@]}" ps

echo "[precheck acs empty]"
ACS_CONFIGURED="$(read_env_value ACS_CONNECTION_STRING || true)"
if [ -n "$ACS_CONFIGURED" ]; then
  echo "ACS_CONNECTION_STRING must be empty before MAIL-05a smoke"
  exit 1
fi
unset ACS_CONFIGURED
echo ACS_CONNECTION_STRING_EMPTY

echo "[precheck managed API key]"
MAILER_API_KEY="$(read_env_value MAILER_API_KEY || true)"
if [ -z "$MAILER_API_KEY" ]; then
  echo "MAILER_API_KEY is required."
  echo "Provide a managed API key for the Sender used by this smoke test."
  exit 1
fi
echo MAILER_API_KEY_PRESENT

echo "[precheck healthz]"
run_client BASE -fsS "$MAILER_BASE_URL/healthz"
echo

echo "[precheck readyz]"
wait_ready current BASE

echo "[precheck healthcheck]"
"${BASE[@]}" exec -T mailer /app/Amane.Mailer healthcheck

echo "[precheck stats]"
"${BASE[@]}" exec -T mailer /app/Amane.Mailer db stats --tenant-id "$TENANT_DEVELOP"

echo "[precheck mailer tag]"
sed -n 's/^MAILER_IMAGE_TAG=/MAILER_IMAGE_TAG=/p' .env

REQ401="$(new_uuid)"
REQ202="$(new_uuid)"
echo "[smoke ids]"
echo "REQ401=$REQ401"
echo "REQ202=$REQ202"

echo "[write 401 payload]"
write_payload "$WORK_DIR/mail-05a-401.json" "$REQ401"
write_curl_config "$WORK_DIR/mail-05a-401.curlrc" "invalid-mail-05a-api-key" "mail-05a-401.json"

echo "[401 api call]"
OUT401="$(run_client BASE --config /mail-05a/mail-05a-401.curlrc)"
printf '%s\n' "$OUT401"
printf '%s\n' "$OUT401" | grep -q 'HTTP_STATUS=401'
printf '%s\n' "$OUT401" | grep -q 'UNAUTHORIZED'

echo "[401 db verification]"
STATE401="$(request_state BASE "$REQ401")"
printf '%s\n' "$STATE401"
[ "$(printf '%s\n' "$STATE401" | state_value found)" = "false" ]

echo "[disable worker]"
cat > "$WORKER_DISABLED_COMPOSE" <<'YAML'
services:
  mailer:
    environment:
      Mailer__Worker__Enabled: "false"
YAML
RESTORE_NEEDED=1
"${DISABLED[@]}" up -d --force-recreate mailer >/dev/null
wait_ready worker-disabled DISABLED
"${DISABLED[@]}" exec -T mailer /app/Amane.Mailer healthcheck
echo WORKER_DISABLED_BY_HEALTHCHECK

echo "[write 202 payload]"
write_payload "$WORK_DIR/mail-05a-202.json" "$REQ202"
write_curl_config "$WORK_DIR/mail-05a-202.curlrc" "$MAILER_API_KEY" "mail-05a-202.json"
unset MAILER_API_KEY

echo "[202 api call]"
OUT202="$(run_client DISABLED --config /mail-05a/mail-05a-202.curlrc)"
printf '%s\n' "$OUT202"
printf '%s\n' "$OUT202" | grep -q 'HTTP_STATUS=202'
printf '%s\n' "$OUT202" | grep -qi 'accepted'

echo "[202 db verification while worker disabled]"
STATE202="$(request_state DISABLED "$REQ202")"
printf '%s\n' "$STATE202"
[ "$(printf '%s\n' "$STATE202" | state_value found)" = "true" ]
[ "$(printf '%s\n' "$STATE202" | state_value status)" = "queued" ]
[ "$(printf '%s\n' "$STATE202" | state_value attempt_count)" = "0" ]
[ "$(printf '%s\n' "$STATE202" | state_value attempt_rows)" = "0" ]
[ "$(printf '%s\n' "$STATE202" | state_value provider_message_id_present)" = "false" ]

echo "[success before restore]"
