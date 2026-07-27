[Japanese](first-mail-quickstart.md)

# Zero-Admin first-mail quickstart (local Mailpit)

This is the shortest path from a fresh clone to **one delivered message** with Mailer + Mailpit,
without enabling the Admin UI. ACS live sending, Dead Letter, backup / restore, deploy rehearsal,
and multi-tenant shared Mailer are out of scope.

To choose a configuration mode first, see **local Mailpit** in the [setup entry point](setup-guide.en.md).

For fuller smoke coverage (idempotent repost, conflict, Admin UI, and more), see:

- [Local Mailer Docker runbook](local-mailer-docker-runbook.en.md) [(ja)](local-mailer-docker-runbook.md)
- [Local Mailer + Mailpit runbook for Linux/macOS](local-mailer-docker-runbook-bash.en.md) [(ja)](local-mailer-docker-runbook-bash.md)

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) or Docker Engine is running.
- Run commands from the repository root.
- Host ports `5280` (Mailer) and `8025` (Mailpit) are free.
- Steps 1–2 need `curl` only (PowerShell `curl.exe` on Windows is fine).
- Steps 3–5 manual curl examples require **bash** and `curl` (on Windows, use [Git Bash](https://gitforwindows.org/); PowerShell alone cannot run the heredoc or `seq` loop). The **PowerShell automated smoke script** below skips manual steps 3–5.

No Admin environment variables (`AMANE_ADMIN_*`) are required. Local compose defaults to Mailpit delivery.

## Automated smoke

### PowerShell (recommended on Windows)

Run from the same PowerShell session as Docker Desktop:

```powershell
.\scripts\local-first-mail-smoke.ps1
```

Optional env:

```powershell
$env:MAILER_HTTP_PORT = '5280'
$env:MAILPIT_HTTP_PORT = '8025'
.\scripts\local-first-mail-smoke.ps1
```

### bash (Linux / macOS / Git Bash)

```bash
bash scripts/local-first-mail-smoke.sh
```

Optional env:

```bash
MAILER_HTTP_PORT=5280 MAILPIT_HTTP_PORT=8025 bash scripts/local-first-mail-smoke.sh
```

The script prints a `[PASS]` / `[FAIL]` summary on success or failure. On failure it also prints `docker compose ps` and recent logs.

## 1. Start Mailer + Mailpit

```bash
docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
```

The first run may take a few minutes while images build.

## 2. Verify health and readiness

```bash
curl -fsS http://127.0.0.1:5280/healthz
printf '\n'
curl -fsS http://127.0.0.1:5280/readyz
printf '\n'
```

Expected output:

```json
{"healthy":true}
{"ready":true}
```

## 3. POST one mail request

`mail_request_id` is the idempotency key. Use a new UUID for each fresh request.
If `uuidgen` is unavailable, set `request_id` to any UUID string.

```bash
request_id="$(uuidgen)"

curl -i -X POST http://127.0.0.1:5280/internal/mail-requests \
  -H "Authorization: Bearer local-mail-service-token" \
  -H "Content-Type: application/json" \
  -d @- <<JSON
{
  "tenant_id": "00000000-0000-0000-0000-000000000101",
  "mail_request_id": "${request_id}",
  "source_service": "example-service",
  "purpose": "FormResponseNotification",
  "to": [
    { "email": "admin@example.com" }
  ],
  "subject": "New response",
  "text_body": "A new response arrived.",
  "payload_hash": "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9"
}
JSON
```

Expected response: `HTTP/1.1 202 Accepted` with JSON like:

```json
{
  "mail_request_id": "<request_id>",
  "status": "accepted"
}
```

For `payload_hash` calculation, see the [Consumer quick start](../../README.en.md#consumer-quick-start) or [examples/payload-hash/](../../examples/payload-hash/README.md).

## 4. GET delivery status (optional)

POST `202 Accepted` / `status: "accepted"` only means the request was persisted. Use GET to read the Worker delivery outcome.

```bash
curl -fsS "http://127.0.0.1:5280/internal/mail-requests/${request_id}?tenant_id=00000000-0000-0000-0000-000000000101&source_service=example-service" \
  -H "Authorization: Bearer local-mail-service-token"
printf '\n'
```

Expected output right after acceptance (example):

```json
{
  "mail_request_id": "<request_id>",
  "status": "queued",
  "attempt_count": 0,
  "max_attempts": 3,
  "accepted_at": "..."
}
```

If you repeat the same GET after a few seconds, `status` may become `delivered` once the Worker finishes. Recipient, subject, and body are not returned.

## 5. Confirm delivery in Mailpit

### Browser

Open <http://127.0.0.1:8025/> and confirm one message with subject **New response**.

### API (curl)

Worker delivery may take a few seconds. Wait up to 30 seconds:

```bash
subject="New response"
mailpit_found=0
for i in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:8025/api/v1/messages | grep -F "$subject"; then
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

## When something fails

Check these six items in order:

1. **Container state** — `docker compose -f infra/docker/docker-compose.local.yml ps`: are `mailer` and `mailpit` `running` / `healthy`?
2. **Mailer logs** — `docker compose -f infra/docker/docker-compose.local.yml logs mailer --tail 50`: startup errors or DB initialization failures?
3. **Port conflict** — are `5280` / `8025` already in use? Conflicts usually fail compose startup or make `curl` connection refused.
4. **POST returns 401 / 403** — is `Authorization: Bearer local-mail-service-token` correct, and is `tenant_id` the example tenant `00000000-0000-0000-0000-000000000101`?
5. **GET returns 404** — do `mail_request_id`, `tenant_id`, and `source_service` match the POST request? Missing IDs and other tenants' IDs also return 404.
6. **Nothing in Mailpit** — did step 2 return `{"ready":true}`? Wait a few seconds, then recheck the Mailpit UI or API.

## Cleanup

Stop containers only:

```bash
docker compose -f infra/docker/docker-compose.local.yml down
```
