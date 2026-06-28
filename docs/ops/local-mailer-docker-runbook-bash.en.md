[日本語](local-mailer-docker-runbook-bash.md) | [Windows PowerShell version](local-mailer-docker-runbook.en.md)

# Local Mailer + Mailpit runbook for Linux/macOS

This runbook uses Linux / macOS bash and curl to start local Docker Mailer + Mailpit,
then verify health / readiness, a successful POST, Mailpit receipt, an idempotent repost,
a conflict response, and the Admin UI. For the same local compose flow in Windows
PowerShell, use the [Local Mailer Docker runbook](local-mailer-docker-runbook.en.md).

This runbook intentionally focuses on the local Mailpit smoke path. For ACS live sending
or Dead Letter checks, use the relevant sections in the Windows PowerShell runbook or the
deploy / release smoke runbooks.

## Important differences from the PowerShell runbook

- These steps assume Linux / macOS `bash` and `curl`.
- `payload_hash` is computed with one of `sha256sum`, `shasum -a 256`, or `openssl`.
- `jq` is not required for JSON checks. If installed, you can use it only to pretty-print responses.
- On a Linux / macOS fresh checkout, Docker bind-mount directory ownership matters. Normal compose
  startup runs `data-init`, which prepares `data/mailer`, so no extra setup is usually needed.
- Admin UI environment variables use `export`. `AMANE_ADMIN_ALLOW_HTTP=true` and
  `AMANE_ADMIN_PII_LIST_MODE=visible` are for local verification only.
- ACS live sending / Dead Letter checks stay in the Windows PowerShell runbook, deploy runbooks, or release smoke runbooks.

## Prerequisites

- Docker Desktop or Docker Engine is running.
- The `docker compose` plugin is available.
- Run commands from the repository root.
- `bash`, `curl`, `awk`, `grep`, `tail`, and `uuidgen` are available.
- One of `sha256sum`, `shasum`, or `openssl` is available.
- Default host ports `5280` (Mailer) and `8025` (Mailpit) are free.

`jq` is optional. The commands below do not require it.
On Debian / Ubuntu, install missing `uuidgen` with `sudo apt install uuid-runtime`.

## 1. Set common variables

```bash
set -Eeuo pipefail
set +x

COMPOSE_FILE="infra/docker/docker-compose.local.yml"
MAILER_URL="http://127.0.0.1:5280"
MAILPIT_URL="http://127.0.0.1:8025"

TENANT_ID="00000000-0000-0000-0000-000000000101"
SOURCE_SERVICE="example-service"
TO_EMAIL="smoke@example.com"
PURPOSE="local-docker-smoke"
TEXT_BODY="Hello from local Docker Mailer smoke."
SUBJECT_OK="Local Mailer Docker bash smoke"
SUBJECT_CONFLICT="Local Mailer Docker bash smoke conflict"
MAIL_SERVICE_TOKEN="local-mail-service-token"
```

## 2. Stop Mailer and optionally reset the DB

```bash
docker compose -f "$COMPOSE_FILE" down
rm -f data/mailer/mailer.db data/mailer/mailer.db-wal data/mailer/mailer.db-shm
```

This removes local Mailer mail-request history. Do not run it on production or develop deploy hosts.

To verify only fresh-checkout bind-mount permissions on Linux/macOS, you can also run the dedicated script:

```bash
bash scripts/local-compose-fresh-data-check.sh
```

## 3. Build images

```bash
docker compose -f "$COMPOSE_FILE" build mailer mailer-migrate
```

## 4. Create an Admin UI password hash

The Admin UI requires `AMANE_ADMIN_PASSWORD_HASH`. Enter any local verification password.

```bash
read -r -s -p "Mailer admin password: " admin_password
printf '\n'

hash="$(
  printf '%s\n%s\n' "$admin_password" "$admin_password" |
    docker compose -f "$COMPOSE_FILE" run --rm -T --no-deps mailer admin hash-password 2>/dev/null |
    tail -n 1
)"

case "$hash" in
  pbkdf2:sha256:*) ;;
  *) echo "Failed to generate AMANE_ADMIN_PASSWORD_HASH." >&2; exit 1 ;;
esac

unset admin_password
```

## 5. Start Mailer / Mailpit / Admin UI

Steps from section 1 onward assume the same bash session. If you resume in a new session,
regenerate `hash` in section 4, then reconfigure the Admin UI and Mailpit env vars.

Even if `.env` contains ACS values, this shell overrides them to Mailpit.
`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` is the `/admin` request
`Connection.LocalIpAddress` allowlist, not a socket bind. Actual host exposure is
still limited by compose `ports` (`127.0.0.1:5280:8080`).

```bash
export AMANE_ADMIN_ENABLED="true"
export AMANE_ADMIN_USERNAME="admin"
export AMANE_ADMIN_PASSWORD_HASH="$hash"
export AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS="0.0.0.0"
export AMANE_ADMIN_ALLOW_HTTP="true"
export AMANE_ADMIN_PII_LIST_MODE="visible"

export MAILER_TENANTS_PATH="/app/config/mailer/tenants.example.json"
export MAILER_PROVIDER="mailpit"
export MAIL_SERVICE_TOKEN="$MAIL_SERVICE_TOKEN"
export MAILPIT_SMTP_HOST="mailpit"
export MAILPIT_SMTP_PORT="1025"
export MAILPIT_SMTP_USE_SSL="false"
export ACS_CONNECTION_STRING=""

docker compose -f "$COMPOSE_FILE" up -d --wait mailer
```

`AMANE_ADMIN_ALLOW_HTTP=true` and `AMANE_ADMIN_PII_LIST_MODE=visible` are for local
verification only. Do not enable HTTP or PII display on production or develop deploy hosts.
For the Admin UI exposure and PII policy, see [ADR 0013](../adr/0013-admin-threat-model-and-pii-policy.md).

## 6. Verify health / readiness

```bash
docker compose -f "$COMPOSE_FILE" ps

curl -fsS "$MAILER_URL/healthz"
printf '\n'

curl -fsS "$MAILER_URL/readyz"
printf '\n'
```

Expected:

```json
{"healthy":true}
{"ready":true}
```

Open in a browser:

- Mailer admin UI: <http://127.0.0.1:5280/admin/login>
- Mailpit UI: <http://127.0.0.1:8025/>

Admin login uses username `admin` and the password from section 4.

## 7. Build payload_hash and request JSON

`payload_hash` is the SHA-256 of the canonical JSON for delivery fields only. This runbook
generates JSON with the same key order as MailPayloadHasher. If values include quotes,
newlines, or other JSON-special characters, use the Python / JavaScript / Go helpers under
`examples/payload-hash/`.

```bash
SHA256_CMD=""
if command -v sha256sum >/dev/null 2>&1; then
  SHA256_CMD="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
  SHA256_CMD="shasum -a 256"
elif command -v openssl >/dev/null 2>&1; then
  SHA256_CMD="openssl"
else
  echo "sha256sum, shasum, or openssl is required." >&2
  exit 1
fi

canonical_payload() {
  subject="$1"
  printf '{"purpose":"%s","source_service":"%s","subject":"%s","text_body":"%s","to":[{"email":"%s"}]}' \
    "$PURPOSE" "$SOURCE_SERVICE" "$subject" "$TEXT_BODY" "$TO_EMAIL"
}

payload_hash() {
  canonical="$1"
  if [ "$SHA256_CMD" = "openssl" ]; then
    printf '%s' "$canonical" | openssl dgst -sha256 | awk '{print $NF}'
  else
    printf '%s' "$canonical" | $SHA256_CMD | awk '{print $1}'
  fi
}

request_json() {
  request_id="$1"
  subject="$2"
  hash_value="$3"
  printf '{"tenant_id":"%s","source_service":"%s","mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s","payload_hash":"%s"}' \
    "$TENANT_ID" "$SOURCE_SERVICE" "$request_id" "$PURPOSE" "$TO_EMAIL" "$subject" "$TEXT_BODY" "$hash_value"
}

if command -v uuidgen >/dev/null 2>&1; then
  request_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
elif [ -r /proc/sys/kernel/random/uuid ]; then
  request_id="$(cat /proc/sys/kernel/random/uuid)"
else
  echo "uuidgen is required. On Debian/Ubuntu, install it with: sudo apt install uuid-runtime" >&2
  exit 1
fi

canonical_ok="$(canonical_payload "$SUBJECT_OK")"
hash_ok="$(payload_hash "$canonical_ok")"
json_ok="$(request_json "$request_id" "$SUBJECT_OK" "$hash_ok")"
```

## 8. Submit a valid POST

```bash
post_mail_request() {
  json="$1"
  curl -sS -w '\nHTTP_STATUS=%{http_code}\n' \
    -X POST "$MAILER_URL/internal/mail-requests" \
    -H "Authorization: Bearer $MAIL_SERVICE_TOKEN" \
    -H "Content-Type: application/json" \
    --data "$json"
}

post_mail_request "$json_ok"
```

Expected: `HTTP_STATUS=202` and `status: "accepted"`.

```json
{"mail_request_id":"<request id>","status":"accepted"}
HTTP_STATUS=202
```

## 9. Verify Mailpit and Admin UI

Check that the subject is present through the Mailpit API. Worker delivery may take a moment,
so wait up to 30 seconds.

```bash
mailpit_found=0
for i in $(seq 1 30); do
  if curl -fsS "$MAILPIT_URL/api/v1/messages" | grep -F "$SUBJECT_OK"; then
    mailpit_found=1
    break
  fi
  sleep 1
done

if [ "$mailpit_found" -ne 1 ]; then
  echo "Mailpit message was not found within 30 seconds." >&2
  exit 1
fi
```

In a browser, open <http://127.0.0.1:8025/> and confirm one message with subject
`Local Mailer Docker bash smoke` arrived.

For the Admin UI, open <http://127.0.0.1:5280/admin/login>, log in with `admin`
and the password from section 4, then confirm `/admin/mail-requests` shows the same
subject as `Delivered`.

## 10. Verify idempotent repost

POST the same `mail_request_id` and the same payload again.

```bash
post_mail_request "$json_ok"
```

Expected: `HTTP_STATUS=202` and `status: "already_accepted"`.

```json
{"mail_request_id":"<request id>","status":"already_accepted"}
HTTP_STATUS=202
```

## 11. Verify conflict

Keep the same `mail_request_id`, change the subject, and recompute `payload_hash`.

```bash
canonical_conflict="$(canonical_payload "$SUBJECT_CONFLICT")"
hash_conflict="$(payload_hash "$canonical_conflict")"
json_conflict="$(request_json "$request_id" "$SUBJECT_CONFLICT" "$hash_conflict")"

post_mail_request "$json_conflict"
```

Expected: `HTTP_STATUS=409` and `IDEMPOTENCY_CONFLICT`.

```json
{"code":"IDEMPOTENCY_CONFLICT"}
HTTP_STATUS=409
```

## 12. Cleanup

To stop containers only:

```bash
docker compose -f "$COMPOSE_FILE" down
```

To reset mail-request history too:

```bash
rm -f data/mailer/mailer.db data/mailer/mailer.db-wal data/mailer/mailer.db-shm
```
