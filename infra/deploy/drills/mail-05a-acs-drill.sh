#!/usr/bin/env bash
set -Eeuo pipefail
set +x

COMPOSE_DIR="${MAIL05A_COMPOSE_DIR:-/opt/amane-mailer}"
INPUT_FILE="${ACS_DRILL_INPUT_FILE:-./mail-05a-acs-drill.input.json}"
KEEP_INPUT="$(printf '%s' "${ACS_DRILL_KEEP_INPUT:-false}" | tr '[:upper:]' '[:lower:]')"
TENANT_DEVELOP="${MAIL05A_TENANT_DEVELOP:-00000000-0000-0000-0000-000000000101}"
TENANT_PRODUCTION="${MAIL05A_TENANT_PRODUCTION:-00000000-0000-0000-0000-000000000301}"
SOURCE_SERVICE="${MAIL05A_SOURCE_SERVICE:-example-service}"
WORK_DIR="mail-05a-work"
CLIENT_COMPOSE="compose.mail-05a-client.yml"
ACS_DRILL_COMPOSE="compose.acs-drill.yml"
ACS_DRILL_TENANTS="tenants.acs-drill.json"

cd "$COMPOSE_DIR"

COMPOSE_EXTRA=()
if [ -n "${MAIL05A_COMPOSE_EXTRA:-}" ]; then
  for f in $MAIL05A_COMPOSE_EXTRA; do
    COMPOSE_EXTRA+=( -f "$f" )
  done
fi

BASE=(docker compose --env-file .env -f compose.yml "${COMPOSE_EXTRA[@]}" -f "$CLIENT_COMPOSE")
DRILL=(docker compose --env-file .env -f compose.yml "${COMPOSE_EXTRA[@]}" -f "$ACS_DRILL_COMPOSE" -f "$CLIENT_COMPOSE")
RESTORE_NEEDED=0
INPUT_REMOVE_READY=0
REQ_ACS=""
FINAL_STATE=""

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
  for _ in $(seq 1 90); do
    if run_client "$cmd_name" -fsS "$MAILER_BASE_URL/readyz" >"$WORK_DIR/ready.out" 2>"$WORK_DIR/ready.err"; then
      cat "$WORK_DIR/ready.out"
      echo
      return 0
    fi
    sleep 1
  done

  echo "ready wait timed out for $label"
  cat "$WORK_DIR/ready.err" || true
  echo "[recent mailer logs: $label]"
  local -n compose_cmd="$cmd_name"
  "${compose_cmd[@]}" logs --tail 120 mailer 2>/dev/null \
    | grep -Ei 'fail|error|exception|critical|tenant|json|path|mailaddress|format|unauthorized|denied|request' \
    | sed -E 's/accesskey=[^;"[:space:]]*/accesskey=***/gi' \
    | sed -E 's/(Authorization: Bearer )[A-Za-z0-9._~+\/=-]+/\1***/gi' || true
  return 1
}

state_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); exit }'
}

stats_value() {
  local key="$1"
  awk -F= -v key="$key" '$1 == key { print substr($0, length(key) + 2); exit }'
}

require_stat_integer() {
  local key="$1"
  local stats="$2"
  local value
  value="$(printf '%s\n' "$stats" | stats_value "$key")"
  if ! [[ "$value" =~ ^[0-9]+$ ]]; then
    echo "missing or invalid db stats key: $key"
    exit 1
  fi
  printf '%s\n' "$value"
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

db_stats() {
  local cmd_name="$1"
  shift
  local -n compose_cmd="$cmd_name"
  "${compose_cmd[@]}" exec -T mailer /app/Amane.Mailer db stats "$@"
}

read_secret_input() {
  if [ ! -f "$INPUT_FILE" ]; then
    echo "missing input file: $INPUT_FILE"
    exit 1
  fi

  mapfile -t fields < <(python3 - "$INPUT_FILE" <<'PY'
import base64
import json
import re
import sys
from email.utils import parseaddr

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)

conn = str(data.get("connection_string", "")).strip()
sender = str(data.get("sender_email", "")).strip()
recipient = str(data.get("recipient_email", "")).strip()

if not conn or not sender or not recipient:
    raise SystemExit("input must contain connection_string, sender_email, and recipient_email")
if not re.match(r"(?i)^endpoint=https://.+;accesskey=.+", conn):
    raise SystemExit("connection_string does not look like an ACS endpoint/accesskey connection string")
for label, value in [("sender_email", sender), ("recipient_email", recipient)]:
    parsed = parseaddr(value)[1]
    if parsed != value or "@" not in parsed:
        raise SystemExit(f"{label} must be a bare email address")

for value in [conn, sender, recipient]:
    print(base64.b64encode(value.encode("utf-8")).decode("ascii"))
PY
)

  ACS_CONN="$(printf '%s' "${fields[0]}" | base64 -d)"
  SENDER_EMAIL="$(printf '%s' "${fields[1]}" | base64 -d)"
  RECIPIENT_EMAIL="$(printf '%s' "${fields[2]}" | base64 -d)"
}

write_live_tenants() {
  SENDER_EMAIL="$SENDER_EMAIL" \
  TENANT_DEVELOP="$TENANT_DEVELOP" \
  TENANT_PRODUCTION="$TENANT_PRODUCTION" \
  SOURCE_SERVICE="$SOURCE_SERVICE" \
  python3 - > "$ACS_DRILL_TENANTS" <<'PY'
import json
import os
import sys

sender = os.environ["SENDER_EMAIL"]
tenant_develop = os.environ["TENANT_DEVELOP"]
tenant_production = os.environ["TENANT_PRODUCTION"]
source_service = os.environ["SOURCE_SERVICE"]
tenants = {
  "version": 1,
  "environment": "shared",
  "tenants": [
    {
      "tenant_id": tenant_develop,
      "name": "drill-develop",
      "source_services": [source_service],
      "default_from": {"email": sender, "display_name": "Drill (develop)"},
      "token_env": "MAIL_SERVICE_TOKEN_DEVELOP",
      "provider": "acs",
      "live_sending": True,
      "metadata_max_bytes": 4096,
      "retry": {"max_attempts": 10, "initial_delay_seconds": 10, "max_delay_seconds": 300},
    },
    {
      "tenant_id": "00000000-0000-0000-0000-000000000201",
      "name": "drill-staging",
      "source_services": [source_service],
      "default_from": {"email": "noreply@example.com", "display_name": "Drill (staging)"},
      "token_env": "MAIL_SERVICE_TOKEN_STAGING",
      "provider": "acs",
      "live_sending": False,
      "metadata_max_bytes": 4096,
      "retry": {"max_attempts": 10, "initial_delay_seconds": 10, "max_delay_seconds": 300},
    },
    {
      "tenant_id": tenant_production,
      "name": "drill-production",
      "source_services": [source_service],
      "default_from": {"email": "noreply@example.com", "display_name": "Drill (production)"},
      "token_env": "MAIL_SERVICE_TOKEN_PRODUCTION",
      "provider": "acs",
      "live_sending": False,
      "metadata_max_bytes": 4096,
      "retry": {"max_attempts": 10, "initial_delay_seconds": 10, "max_delay_seconds": 300},
    },
  ],
}
json.dump(tenants, sys.stdout, ensure_ascii=False, indent=2)
sys.stdout.write("\n")
PY
  # The runtime container runs as a non-root user, so the bind-mounted tenant
  # file must be world-readable. It contains sender metadata, not tokens or ACS keys.
  chmod 644 "$ACS_DRILL_TENANTS"
}

write_override() {
  cat > "$ACS_DRILL_COMPOSE" <<'YAML'
services:
  mailer:
    environment:
      MAILER_TENANTS_PATH: /tmp/mail-05a-acs-drill/tenants.json
      ACS_CONNECTION_STRING: ${ACS_CONNECTION_STRING:?ACS_CONNECTION_STRING required for ACS drill}
      Mailer__Worker__Enabled: "true"
    volumes:
      - ./tenants.acs-drill.json:/tmp/mail-05a-acs-drill/tenants.json:ro
YAML
  chmod 600 "$ACS_DRILL_COMPOSE"
}

write_payload() {
  local path="$1"
  local request_id="$2"
  RECIPIENT_EMAIL="$RECIPIENT_EMAIL" \
  TENANT_DEVELOP="$TENANT_DEVELOP" \
  SOURCE_SERVICE="$SOURCE_SERVICE" \
  python3 - "$path" "$request_id" <<'PY'
import hashlib
import json
import os
import sys

path, request_id = sys.argv[1], sys.argv[2]
delivery = {
    "source_service": os.environ["SOURCE_SERVICE"],
    "purpose": "mail-05a-acs-drill",
    "to": [{"email": os.environ["RECIPIENT_EMAIL"]}],
    "subject": "MAIL-05a ACS delivery drill",
    "text_body": "MAIL-05a ACS delivery drill. One approved verification recipient only.",
}
canonical = json.dumps(delivery, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
request = {
    "tenant_id": os.environ["TENANT_DEVELOP"],
    **delivery,
    "mail_request_id": request_id,
    "payload_hash": hashlib.sha256(canonical.encode("utf-8")).hexdigest(),
}
with open(path, "w", encoding="utf-8") as f:
    json.dump(request, f, ensure_ascii=False, separators=(",", ":"))
PY
}

write_curl_config() {
  local path="$1"
  local token="$2"
  local payload_path="$3"

  (
    umask 077
    printf '%s\n' 'silent'
    printf '%s\n' 'show-error'
    printf '%s\n' 'request = "POST"'
    printf 'url = "%s/internal/mail-requests"\n' "$MAILER_BASE_URL"
    printf 'header = "Authorization: Bearer %s"\n' "$token"
    printf '%s\n' 'header = "Content-Type: application/json"'
    printf 'data = "@/mail-05a/%s"\n' "$payload_path"
    printf '%s\n' 'write-out = "\nHTTP_STATUS=%{http_code}\n"'
  ) > "$path"
}

restore_normal() {
  local status=$?
  if [ "$RESTORE_NEEDED" = "1" ]; then
    echo "[restore normal mailer]"
    unset ACS_CONNECTION_STRING
    "${BASE[@]}" up -d --force-recreate mailer >/dev/null || status=1
    rm -f "$ACS_DRILL_COMPOSE" "$ACS_DRILL_TENANTS" || status=1
    wait_ready restored BASE || status=1
    "${BASE[@]}" exec -T mailer /app/Amane.Mailer healthcheck || status=1
    echo NORMAL_COMPOSE_RESTORED
  fi

  rm -rf "$WORK_DIR" "$CLIENT_COMPOSE" || status=1

  if [ "$INPUT_REMOVE_READY" = "1" ] && [ "$KEEP_INPUT" != "true" ] && [ "$(basename "$INPUT_FILE")" != "mail-05a-acs-drill.input.example.json" ]; then
    rm -f "$INPUT_FILE" || status=1
    echo "INPUT_FILE_REMOVED=$INPUT_FILE"
  elif [ -f "$INPUT_FILE" ] && [ "$(basename "$INPUT_FILE")" != "mail-05a-acs-drill.input.example.json" ]; then
    echo "INPUT_FILE_RETAINED=$INPUT_FILE"
  fi

  [ -n "$REQ_ACS" ] && echo "REQ_ACS=$REQ_ACS"
  [ -n "$FINAL_STATE" ] && echo "FINAL_STATE=$FINAL_STATE"
  unset ACS_CONNECTION_STRING ACS_CONN SENDER_EMAIL RECIPIENT_EMAIL
  exit "$status"
}
trap restore_normal EXIT

MAILER_HTTP_PORT_VALUE="$(read_env_value MAILER_HTTP_PORT || true)"
MAILER_HTTP_PORT_VALUE="${MAILER_HTTP_PORT_VALUE:-8080}"
MAILER_BASE_URL="http://mailer:${MAILER_HTTP_PORT_VALUE}"

read_secret_input

echo "[precheck files]"
if [ -e compose.worker-disabled.yml ] || [ -e "$ACS_DRILL_COMPOSE" ] || [ -e "$ACS_DRILL_TENANTS" ] || [ -e "$CLIENT_COMPOSE" ] || [ -e "$WORK_DIR" ]; then
  echo "temporary MAIL-05a file already exists; aborting"
  exit 1
fi
mkdir -p "$WORK_DIR"
chmod 700 "$WORK_DIR"
write_client_compose
echo "no temporary override files present"

echo "[precheck acs empty]"
ACS_CONFIGURED="$(read_env_value ACS_CONNECTION_STRING || true)"
if [ -n "$ACS_CONFIGURED" ]; then
  echo "ACS_CONNECTION_STRING must be empty before MAIL-05a drill"
  exit 1
fi
unset ACS_CONFIGURED
echo ACS_CONNECTION_STRING_EMPTY

echo "[precheck ready]"
wait_ready current BASE

echo "[precheck healthcheck]"
"${BASE[@]}" exec -T mailer /app/Amane.Mailer healthcheck

echo "[precheck queued/processing]"
ALL_STATS="$(db_stats BASE)"
PROD_STATS="$(db_stats BASE --tenant-id "$TENANT_PRODUCTION")"
ALL_QUEUED="$(require_stat_integer status_queued "$ALL_STATS")"
ALL_PROCESSING="$(require_stat_integer status_processing "$ALL_STATS")"
PROD_QUEUED="$(require_stat_integer status_queued "$PROD_STATS")"
PROD_PROCESSING="$(require_stat_integer status_processing "$PROD_STATS")"
ACTIVE_COUNT=$(( ALL_QUEUED + ALL_PROCESSING ))
PROD_ACTIVE_COUNT=$(( PROD_QUEUED + PROD_PROCESSING ))
echo "active_queue_count=$ACTIVE_COUNT"
echo "production_active_queue_count=$PROD_ACTIVE_COUNT"
[ "$ACTIVE_COUNT" = "0" ]
[ "$PROD_ACTIVE_COUNT" = "0" ]

echo "[start ACS drill mailer: develop live only, production fail-closed]"
RESTORE_NEEDED=1
write_live_tenants
write_override
export ACS_CONNECTION_STRING="$ACS_CONN"
"${DRILL[@]}" up -d --force-recreate mailer >/dev/null
wait_ready acs-drill DRILL
"${DRILL[@]}" exec -T mailer /app/Amane.Mailer healthcheck
echo DRILL_MAILER_HEALTHY

REQ_ACS="$(new_uuid)"
echo "REQ_ACS=$REQ_ACS"

echo "[prepare request payload]"
write_payload "$WORK_DIR/mail-05a-acs-payload.json" "$REQ_ACS"
TOKEN_DEVELOP="$(read_env_value MAIL_SERVICE_TOKEN_DEVELOP || true)"
if [ -z "$TOKEN_DEVELOP" ]; then
  echo MAIL_SERVICE_TOKEN_DEVELOP_MISSING
  exit 1
fi
write_curl_config "$WORK_DIR/mail-05a-acs.curlrc" "$TOKEN_DEVELOP" "mail-05a-acs-payload.json"
unset TOKEN_DEVELOP RECIPIENT_EMAIL
echo "payload prepared"

echo "[post ACS request]"
OUT="$(run_client DRILL --config /mail-05a/mail-05a-acs.curlrc)"
printf '%s\n' "$OUT"
printf '%s\n' "$OUT" | grep -q 'HTTP_STATUS=202'
printf '%s\n' "$OUT" | grep -qi 'accepted'

echo "[wait ACS worker result]"
for _ in $(seq 1 150); do
  STATE="$(request_state DRILL "$REQ_ACS")"
  STATUS="$(printf '%s\n' "$STATE" | state_value status)"
  ATTEMPT_COUNT="$(printf '%s\n' "$STATE" | state_value attempt_count)"
  ATTEMPT_ROWS="$(printf '%s\n' "$STATE" | state_value attempt_rows)"
  MESSAGE_ID_PRESENT="$(printf '%s\n' "$STATE" | state_value provider_message_id_present)"
  LAST_ERROR_CODE="$(printf '%s\n' "$STATE" | state_value last_error_code)"
  FINAL_STATE="status=$STATUS,attempt_count=$ATTEMPT_COUNT,attempt_rows=$ATTEMPT_ROWS,provider_message_id_present=$MESSAGE_ID_PRESENT,last_error_code=$LAST_ERROR_CODE"
  echo "DB_STATE=$FINAL_STATE"
  case "$STATUS" in
    delivered)
      [ "$ATTEMPT_COUNT" = "1" ] && [ "$ATTEMPT_ROWS" = "1" ] && [ "$MESSAGE_ID_PRESENT" = "true" ] && break
      ;;
    failed|dead_lettered)
      break
      ;;
  esac
  sleep 2
done

case "$FINAL_STATE" in
  status=delivered,attempt_count=1,attempt_rows=1,provider_message_id_present=true,*) echo "ACS_DELIVERED_BY_PROVIDER"; INPUT_REMOVE_READY=1 ;;
  *) echo "ACS_NOT_DELIVERED_STATE=$FINAL_STATE"; exit 1 ;;
esac

echo "[success before restore]"
