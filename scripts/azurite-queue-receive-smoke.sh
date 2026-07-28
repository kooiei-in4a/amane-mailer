#!/usr/bin/env bash
# Azurite + Native AOT receive/delete smoke for ACS Storage Queue Pull transport (#305 / #461).
#
# Closes the #399 residual: successful ReceiveMessages XML -> QueueMessage[] deserialize
# must run on the linux-x64 AOT binary (not only JIT tests). After durable inbox insert,
# also asserts the Queue message was deleted (ApproximateMessagesCount includes invisible
# messages, so a failed delete cannot pass by relying on visibility timeout alone).
#
# Prerequisites:
#   - MAILER_BIN: path to published Amane.Mailer (linux-x64 AOT)
#   - Docker (for Azurite)
#   - curl, python3
#
# Optional:
#   AZURITE_QUEUE_PORT   default 10001
#   AOT_QUEUE_SMOKE_KEEP set to 1 to keep work dir / leave processes
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"

MAILER_BIN="${MAILER_BIN:-}"
AZURITE_QUEUE_PORT="${AZURITE_QUEUE_PORT:-10001}"
KEEP="${AOT_QUEUE_SMOKE_KEEP:-0}"
COMPOSE_FILE="$REPO_ROOT/infra/docker/docker-compose.local.yml"
QUEUE_NAME="amane-aot-smoke"
EVENT_ID="eg-aot-smoke-1"
WORK_DIR=""
MAILER_PID=""
PASS_COUNT=0
FAIL_COUNT=0

# Well-known Azurite account (public emulator key; not a production secret).
CONN="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;QueueEndpoint=http://127.0.0.1:${AZURITE_QUEUE_PORT}/devstoreaccount1;"

log()  { printf '%s\n' "$*"; }
pass() { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); printf '[FAIL] %s -- %s\n' "$1" "$2" >&2; }

cleanup() {
  local status=$?
  if [ -n "${MAILER_PID:-}" ] && kill -0 "$MAILER_PID" >/dev/null 2>&1; then
    if [ "$KEEP" = "1" ]; then
      log "[cleanup] leaving Mailer pid $MAILER_PID running."
    else
      kill "$MAILER_PID" >/dev/null 2>&1 || true
      wait "$MAILER_PID" >/dev/null 2>&1 || true
    fi
  fi
  if [ "$KEEP" != "1" ] && [ -n "${WORK_DIR:-}" ] && [ -d "$WORK_DIR" ]; then
    rm -rf "$WORK_DIR"
  fi
  if [ "$KEEP" != "1" ]; then
    docker compose -f "$COMPOSE_FILE" stop azurite >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

if [ -z "$MAILER_BIN" ] || [ ! -x "$MAILER_BIN" ]; then
  log "[error] MAILER_BIN must point to an executable Amane.Mailer binary"
  exit 2
fi

command -v docker >/dev/null 2>&1 || { log "[error] docker required"; exit 2; }
command -v python3 >/dev/null 2>&1 || { log "[error] python3 required"; exit 2; }
command -v curl >/dev/null 2>&1 || { log "[error] curl required"; exit 2; }

WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/amane-aot-queue-smoke.XXXXXX")"
DATA_DIR="$WORK_DIR/data"
CONFIG_DIR="$WORK_DIR/config"
mkdir -p "$DATA_DIR" "$CONFIG_DIR"

cat > "$CONFIG_DIR/tenants.json" <<'EOF'
{
  "version": 1,
  "environment": "develop",
  "tenants": [
    {
      "tenant_id": "00000000-0000-0000-0000-00000000a305",
      "name": "aot-smoke",
      "source_services": ["aot-smoke"],
      "default_from": {
        "email": "noreply@example.com",
        "display_name": "AOT Smoke"
      },
      "token_env": "MAIL_SERVICE_TOKEN",
      "provider": "mailpit",
      "live_sending": false,
      "metadata_max_bytes": 4096,
      "retry": {
        "max_attempts": 3,
        "initial_delay_seconds": 1,
        "max_delay_seconds": 2
      }
    }
  ]
}
EOF

log "[info] starting Azurite queue on 127.0.0.1:${AZURITE_QUEUE_PORT}"
AZURITE_QUEUE_PORT="$AZURITE_QUEUE_PORT" docker compose -f "$COMPOSE_FILE" up -d azurite

for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:${AZURITE_QUEUE_PORT}/" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

PAYLOAD_FILE="$WORK_DIR/payload.json"
python3 - <<PY >"$PAYLOAD_FILE"
import json
print(json.dumps({
  "id": "${EVENT_ID}",
  "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
  "eventTime": "2026-07-26T19:30:00Z",
  "data": {
    "messageId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
    "status": "Bounced",
    "recipient": "user@example.com"
  }
}))
PY

log "[info] seeding queue message via Azure.Storage.Queues (MessageEncoding.None)"
# Use host-side `dotnet` when available; otherwise fall back to sdk container.
SEED_DIR="$WORK_DIR/queue-seed"
mkdir -p "$SEED_DIR"
cat >"$SEED_DIR/Program.cs" <<'CS'
using Azure.Storage.Queues;
var cs = Environment.GetEnvironmentVariable("SEED_CONN") ?? throw new InvalidOperationException("SEED_CONN");
var qn = args[0];
var path = args[1];
var payload = await File.ReadAllTextAsync(path);
var client = new QueueClient(cs, qn, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
await client.CreateIfNotExistsAsync();
await client.ClearMessagesAsync();
await client.SendMessageAsync(payload);
Console.WriteLine("seeded");
CS
cat >"$SEED_DIR/seed.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Storage.Queues" Version="12.27.1" />
  </ItemGroup>
</Project>
CSPROJ
SEED_CONN="$CONN" dotnet run --project "$SEED_DIR/seed.csproj" -- "$QUEUE_NAME" "$PAYLOAD_FILE" >/dev/null
pass "azurite:queue-seed"

HTTP_PORT="$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"

log "[info] migrating DB with AOT binary"
ConnectionStrings__Mailer="Data Source=${DATA_DIR}/mailer.db" \
MAILER_TENANTS_PATH="$CONFIG_DIR/tenants.json" \
MAIL_SERVICE_TOKEN="local-mail-service-token" \
MAILER_PROVIDER="mailpit" \
"$MAILER_BIN" db migrate >/dev/null
pass "aot:db-migrate"

log "[info] starting AOT mailer with MAILER_BOUNCE_INGESTION=queue"
ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT}" \
ASPNETCORE_ENVIRONMENT="Development" \
ConnectionStrings__Mailer="Data Source=${DATA_DIR}/mailer.db" \
MAILER_TENANTS_PATH="$CONFIG_DIR/tenants.json" \
MAIL_SERVICE_TOKEN="local-mail-service-token" \
MAILER_PROVIDER="mailpit" \
MAILPIT_SMTP_HOST="127.0.0.1" \
MAILPIT_SMTP_PORT="1025" \
MAILPIT_SMTP_USE_SSL="false" \
Mailer__Worker__Enabled="true" \
MAILER_BOUNCE_INGESTION="queue" \
MAILER_BOUNCE_QUEUE_CONNECTION_STRING="$CONN" \
MAILER_BOUNCE_QUEUE_NAME="$QUEUE_NAME" \
Mailer__BounceIngestion__Queue__PollIntervalSeconds="2" \
"$MAILER_BIN" >"$WORK_DIR/mailer.log" 2>&1 &
MAILER_PID=$!

for _ in $(seq 1 40); do
  if curl -fsS "http://127.0.0.1:${HTTP_PORT}/healthz" >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
done

if ! curl -fsS "http://127.0.0.1:${HTTP_PORT}/healthz" >/dev/null 2>&1; then
  fail "aot:healthz" "mailer did not become healthy; see $WORK_DIR/mailer.log"
  exit 1
fi
pass "aot:healthz"

INBOX_COUNT=0
for _ in $(seq 1 30); do
  INBOX_COUNT="$(python3 - <<PY
import sqlite3
con = sqlite3.connect(r"${DATA_DIR}/mailer.db")
cur = con.execute(
  "SELECT COUNT(*) FROM provider_event_inbox WHERE provider='acs' AND event_id=?",
  ("${EVENT_ID}",),
)
print(cur.fetchone()[0])
con.close()
PY
)"
  if [ "$INBOX_COUNT" = "1" ]; then
    break
  fi
  sleep 1
done

if [ "$INBOX_COUNT" = "1" ]; then
  pass "aot:queue-receive-and-inbox-insert"
else
  fail "aot:queue-receive-and-inbox-insert" "inbox row missing after poll window"
fi

log "[info] verifying Queue delete after durable inbox acceptance (#461)"
# ApproximateMessagesCount includes invisible (leased) messages, so a failed delete still reports >0.
cat >"$SEED_DIR/Program.cs" <<'CS'
using Azure.Storage.Queues;
var cs = Environment.GetEnvironmentVariable("SEED_CONN") ?? throw new InvalidOperationException("SEED_CONN");
var qn = args[0];
var client = new QueueClient(cs, qn, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
var props = await client.GetPropertiesAsync();
var approximate = props.Value.ApproximateMessagesCount;
var leftover = await client.ReceiveMessagesAsync(maxMessages: 32, visibilityTimeout: TimeSpan.FromSeconds(1));
var leftoverCount = leftover.Value?.Length ?? 0;
Console.WriteLine($"approximate={approximate};receive={leftoverCount}");
if (approximate != 0 || leftoverCount != 0)
{
    Environment.Exit(1);
}
CS
if SEED_CONN="$CONN" dotnet run --project "$SEED_DIR/seed.csproj" -- "$QUEUE_NAME" >/dev/null; then
  pass "aot:queue-delete-after-inbox-insert"
else
  fail "aot:queue-delete-after-inbox-insert" "queue still has messages after inbox insert (delete missing or failed)"
fi

if grep -Eiq 'AccountKey=|SharedAccessKey=' "$WORK_DIR/mailer.log"; then
  fail "aot:no-connection-string-leak" "storage secret-like material found in mailer.log"
else
  pass "aot:no-connection-string-leak"
fi

if [ "$FAIL_COUNT" -gt 0 ]; then
  log "[summary] FAIL=${FAIL_COUNT} PASS=${PASS_COUNT}"
  exit 1
fi

log "[summary] PASS=${PASS_COUNT}"
exit 0