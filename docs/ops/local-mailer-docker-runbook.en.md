[日本語](local-mailer-docker-runbook.md)

# Local Mailer Docker Runbook

This runbook covers starting Mailer and Mailpit on local Docker, and verifying the Mailer admin UI, Mailpit delivery, ACS switching, and Dead Letter behavior. The Consumer app itself (`app` / `db`) is out of scope.

For deploy compose rehearsal on a deploy host (3 tenants / shared network), see [local-deploy-rehearsal-runbook.en.md](local-deploy-rehearsal-runbook.en.md).

## Prerequisites

- Docker Desktop is running.
- Run commands from the repository root.
- Commands assume Windows PowerShell.
- Local compose file: `infra/docker/docker-compose.local.yml`.
- Default is `MAILER_PROVIDER=mailpit`. ACS live sending runs only when you have approved ACS resources, a verified sender address, and a deliverable recipient address.
- `config/mailer/tenants.local*.json` is in `.gitignore`. Do not commit tenant JSON or connection strings used for live sending.

## 1. Stop Mailer

```powershell
docker compose -f infra/docker/docker-compose.local.yml down
```

## 2. Initialize the Mailer DB

Mailer SQLite is bind-mounted at `data/mailer/`, not stored in a Docker volume.
To verify from a clean state, delete the local DB files.

```powershell
$mailerDbFiles = @(
  ".\data\mailer\mailer.db",
  ".\data\mailer\mailer.db-wal",
  ".\data\mailer\mailer.db-shm"
)

Remove-Item -LiteralPath $mailerDbFiles -Force -ErrorAction SilentlyContinue
```

This removes local Mailer mail-request history. Do not run this on production or develop deploy hosts.

## 3. Build images

```powershell
docker compose -f infra/docker/docker-compose.local.yml build mailer mailer-migrate
```

## 4. Create an admin UI password hash

The admin UI requires `AMANE_ADMIN_PASSWORD_HASH`. Use any local verification password you choose.

```powershell
$adminPassword = Read-Host "Mailer admin password"
$hash = @($adminPassword, $adminPassword) |
  docker compose -f infra/docker/docker-compose.local.yml run --rm -T --no-deps mailer admin hash-password 2>$null |
  Select-Object -Last 1

if ($hash -notlike "pbkdf2:sha256:*") {
  throw "Failed to generate AMANE_ADMIN_PASSWORD_HASH."
}
```

## 5. Start Mailer / Mailpit

Even if `.env` contains ACS values, the PowerShell session below overrides them to Mailpit.
Set `AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` so the admin UI is reachable via Docker port publish.
This is a `/admin` request `Connection.LocalIpAddress` allowlist, not a socket bind.
The actual host exposure is still limited by compose `ports` (`127.0.0.1:5280:8080` in this runbook).
The old `AMANE_ADMIN_BIND` / `MAILER_ADMIN_BIND` names remain as deprecated aliases.
`AMANE_ADMIN_ALLOW_HTTP=true` and `AMANE_ADMIN_PII_LIST_MODE=visible` are for local verification only.
Do not enable HTTP or PII display on production or develop deploy hosts.
Switching steps from section 5 onward assume the same PowerShell session.
If you resume in a new session, regenerate `$hash` in section 4 and reconfigure the admin env vars.

```powershell
$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"       # local Docker HTTP only
$env:AMANE_ADMIN_PII_LIST_MODE = "visible" # local UI verification only

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.example.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAIL_SERVICE_TOKEN = "local-mail-service-token"
$env:MAILPIT_SMTP_HOST = "mailpit"
$env:MAILPIT_SMTP_PORT = "1025"
$env:MAILPIT_SMTP_USE_SSL = "false"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --wait mailer
```

## 6. Verify startup

```powershell
docker compose -f infra/docker/docker-compose.local.yml ps

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/healthz |
  Select-Object -ExpandProperty Content

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/readyz |
  Select-Object -ExpandProperty Content
```

Expected:

```json
{"healthy":true}
{"ready":true}
```

Open in a browser:

- Mailer admin UI: <http://127.0.0.1:5280/admin/login>
- Mailpit UI: <http://127.0.0.1:8025/>

Admin login uses username `admin` and the password you entered in section 4.

## 7. Submit a test email

The following is a smoke test that submits one request to the `example-develop` tenant.
`payload_hash` is a SHA-256 of delivery-relevant fields after normalization.

```powershell
$tenantId = "00000000-0000-0000-0000-000000000101"
$sourceService = "example-service"
$to = "smoke@example.com"
$subject = "Local Mailer Docker smoke"
$textBody = "Hello from local Docker Mailer smoke."
$purpose = "local-docker-smoke"

$canonical = ([ordered]@{
  purpose = $purpose
  source_service = $sourceService
  subject = $subject
  text_body = $textBody
  to = @([ordered]@{ email = $to })
} | ConvertTo-Json -Depth 6 -Compress)

$sha = [System.Security.Cryptography.SHA256]::Create()
$payloadHash = [System.BitConverter]::ToString(
  $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical))
).Replace("-", "").ToLowerInvariant()

$requestId = [guid]::NewGuid().ToString()
$request = [ordered]@{
  tenant_id = $tenantId
  source_service = $sourceService
  mail_request_id = $requestId
  purpose = $purpose
  to = @(@{ email = $to })
  subject = $subject
  text_body = $textBody
  payload_hash = $payloadHash
}

$json = $request | ConvertTo-Json -Depth 6 -Compress

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:5280/internal/mail-requests" `
  -Headers @{ Authorization = "Bearer local-mail-service-token" } `
  -ContentType "application/json" `
  -Body $json
```

Expected:

```json
{
  "mail_request_id": "<request id>",
  "status": "accepted"
}
```

## 8. Verify the admin UI and Mailpit

Mailer admin UI:

1. Open <http://127.0.0.1:5280/admin/login>.
2. Log in with `admin` and the password from section 4.
3. Go to `/admin/mail-requests` and confirm the row for `Local Mailer Docker smoke` shows `Delivered`.

Mailpit:

1. Open <http://127.0.0.1:8025/>.
2. Confirm one message with subject `Local Mailer Docker smoke` has arrived.

## 9. Prepare a tenant for ACS live sending

Run this only when verifying ACS live sending. Use a sender/domain approved in ACS.

```powershell
Copy-Item `
  -LiteralPath .\config\mailer\tenants.local-acs.json.example `
  -Destination .\config\mailer\tenants.local-acs.json `
  -ErrorAction Stop
```

Edit `config/mailer/tenants.local-acs.json` and set at least the following to real values:

- `name`
- `source_services`
- `default_from.email`
- `default_from.display_name`

This file is read from `/app/config/mailer/` via the `config/mailer` bind mount.
You can switch to it by changing `MAILER_TENANTS_PATH` without rebuilding the image.

## 10. Switch to ACS and send live

Use the same `source_service` as in the section 9 tenant JSON and a recipient address you can verify.

```powershell
$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.local-acs.json"
$env:MAILER_PROVIDER = "acs"
$env:ACS_CONNECTION_STRING = "<ACS connection string>"
$env:MAILPIT_SMTP_HOST = "mailpit"

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

Change `$sourceService`, `$to`, `$subject`, and `$textBody` from section 7 for ACS verification and submit.
Example:

```powershell
$sourceService = "<value included in source_services in tenants.local-acs.json>"
$to = "<email address where you can confirm delivery>"
$subject = "Local Mailer ACS smoke"
$textBody = "Hello from local Docker Mailer via ACS."
```

After submission, confirm `Local Mailer ACS smoke` shows `Delivered` on `/admin/mail-requests`.
If ACS rejects the send, status becomes `Failed` or `DeadLettered` after retries, and the detail view shows the provider error on the attempt.
The displayed and stored error message is a classified, sanitized summary (connection strings, tokens, URL query strings, and email addresses are masked, while the triage `error_code` is kept). Raw provider responses are not stored. See "Provider Error Sanitization" in [SECURITY.md](../../SECURITY.md) for details.

## 11. Verify Dead Letter

To verify Dead Letter display without credentials, use the Mailpit provider and intentionally fail the SMTP destination.
If you want separate history, initialize the DB in section 2 before running this.

```powershell
@'
{
  "version": 1,
  "environment": "develop",
  "tenants": [
    {
      "tenant_id": "00000000-0000-0000-0000-000000000101",
      "name": "example-deadletter",
      "source_services": ["example-service"],
      "default_from": {
        "email": "noreply@example.com",
        "display_name": "Example Service"
      },
      "token_env": "MAIL_SERVICE_TOKEN",
      "provider": "mailpit",
      "live_sending": false,
      "metadata_max_bytes": 4096,
      "retry": {
        "max_attempts": 1,
        "initial_delay_seconds": 1,
        "max_delay_seconds": 1
      }
    }
  ]
}
'@ | Set-Content -LiteralPath .\config\mailer\tenants.local-deadletter.json -Encoding UTF8

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.local-deadletter.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAILPIT_SMTP_HOST = "127.0.0.1"
$env:MAILPIT_SMTP_PORT = "1025"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

Change `$subject` from section 7 to `Local Mailer Dead Letter smoke` and submit. Wait a few seconds, then check status.
`127.0.0.1:1025` points to loopback inside the mailer container, so it fails immediately as an unbound SMTP destination rather than reaching Mailpit.

```powershell
Start-Sleep -Seconds 5
docker compose -f infra/docker/docker-compose.local.yml exec -T mailer /app/Amane.Mailer db stats
```

Expected:

```text
status_dead_lettered=1
dead_lettered_total=1
```

In the admin UI, go to `/admin/dead-letters` and confirm a row for `Local Mailer Dead Letter smoke` appears.

## 12. Return to Mailpit after ACS / Dead Letter verification

Run this in the same PowerShell session as section 5, or regenerate `$hash` in section 4 and reconfigure the admin env vars below.

```powershell
$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"
$env:AMANE_ADMIN_PII_LIST_MODE = "visible"

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.example.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAIL_SERVICE_TOKEN = "local-mail-service-token"
$env:MAILPIT_SMTP_HOST = "mailpit"
$env:MAILPIT_SMTP_PORT = "1025"
$env:MAILPIT_SMTP_USE_SSL = "false"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

Run section 7 again and confirm `Delivered` in the admin UI and receipt in the Mailpit UI.
This confirms you have returned to Mailpit after ACS or Dead Letter verification.

## 13. Cleanup

To stop containers only:

```powershell
docker compose -f infra/docker/docker-compose.local.yml down
```

To remove the local tenant JSON created for Dead Letter verification:

```powershell
Remove-Item -LiteralPath .\config\mailer\tenants.local-deadletter.json -Force -ErrorAction SilentlyContinue
```

To reset including mail-request history, also delete the DB files from section 2.
