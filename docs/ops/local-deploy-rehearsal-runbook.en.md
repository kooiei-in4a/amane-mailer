[日本語](local-deploy-rehearsal-runbook.md)

# Local deploy rehearsal runbook

This runbook reproduces on local Docker the same shared Mailer stack shape as deploy-host `infra/deploy/compose.yml`. The Consumer app itself, deploy hosts, and production ACS live sending are out of scope.

For the development stack with Mailpit, see [local-mailer-docker-runbook.en.md](local-mailer-docker-runbook.en.md).
This runbook is for rehearsing the deploy template (3 tenants / `MAILER_NETWORK_NAME` network).

## What to verify

| Item | How to verify |
|------|---------------|
| Compose config | `docker compose ... config --quiet` |
| DB migration | `mailer-migrate` (profile `ops`) |
| Container startup | `mailer` is healthy |
| HTTP | `GET /healthz` → `{"healthy":true}`, `GET /readyz` → `{"ready":true}` |
| CLI | `/app/Amane.Mailer healthcheck` → exit 0 |
| Admin UI | Set `AMANE_ADMIN_*`, verify `/admin/login` and `/admin/mail-requests` after login |
| Tenant token | `token_env` in `tenants.json` matches `MAIL_SERVICE_TOKEN_*` in `.env` inside the container |
| Shared network | Docker network `MAILER_NETWORK_NAME` (set in `.env`); `mailer` service resolves as alias `mailer` |
| Internal network | `amane-mailer_internal` has `internal=true` (no outbound access) |

## Prerequisites

- Docker Desktop (Linux engine) is running
- Command examples assume Windows PowerShell 5.1+ / PowerShell 7+
- Do **not** commit `.env` or `tenants.json` under `infra/deploy`

## Quick start (recommended)

To reproduce without GHCR authentication, build images from the repository.

```powershell
cd infra/deploy
.\scripts\local-rehearsal.ps1 -Build
```

The no-send shared Mailer smoke (`mail-05a-no-send-smoke.sh`) is **not run by default**. Add `-RunSmoke` only when needed
(requires bash / python3 and the same Docker context as PowerShell).

```powershell
.\scripts\local-rehearsal.ps1 -Build -RunSmoke
```

If you already pulled a `sha-*` tag from GHCR:

```powershell
cd infra/deploy
copy .env.example .env
copy ..\..\config\mailer\tenants.shared.example.json tenants.json
# Edit MAILER_IMAGE_TAG and each MAIL_SERVICE_TOKEN_* in .env
$env:MAILER_PULL_POLICY = 'never'   # or set in .env
.\scripts\local-rehearsal.ps1
```

The script does not overwrite existing `.env` / `tenants.json`.

## Manual steps

### 1. Working directory and files

```powershell
cd infra/deploy
New-Item -ItemType Directory -Force -Path data | Out-Null
```

| File | Source template |
|------|-----------------|
| `.env` | `.env.example` |
| `tenants.json` | `config/mailer/tenants.shared.example.json` |

### 2. Map tenant `token_env` to `.env`

`tenants.shared.example.json` assigns a distinct `token_env` to each of the 3 tenants.

| Tenant | `token_env` | `.env` variable |
|--------|-------------|-----------------|
| `example-develop` | `MAIL_SERVICE_TOKEN_DEVELOP` | `MAIL_SERVICE_TOKEN_DEVELOP` |
| `example-staging` | `MAIL_SERVICE_TOKEN_STAGING` | `MAIL_SERVICE_TOKEN_STAGING` |
| `example-production` | `MAIL_SERVICE_TOKEN_PRODUCTION` | `MAIL_SERVICE_TOKEN_PRODUCTION` |

Use **different values** for all three tokens (same operational model as production).
`MAIL_SERVICE_TOKEN` is a compatibility variable for single-tenant setups. In the shared 3-tenant layout,
a placeholder is fine.

Consumer apps should use the same token values as the corresponding `MAIL_SERVICE_TOKEN_*`
(when switching to production; for local rehearsal, aligning Mailer-side values is enough).

### 3. Choose an image source

**A. Local build (no GHCR)**

```powershell
docker compose --env-file .env `
  -f compose.yml `
  -f compose.local-rehearsal.yml `
  -f compose.local-rehearsal.build.yml `
  build mailer mailer-migrate
```

Example `.env`:

```dotenv
MAILER_IMAGE_REPOSITORY=amane-mailer
MAILER_IMAGE_TAG=local-rehearsal
MAILER_PULL_POLICY=never
```

**B. Published GHCR `sha-*` tag**

```dotenv
MAILER_IMAGE_REPOSITORY=ghcr.io/YOUR_GITHUB_ORG/amane-mailer
MAILER_IMAGE_TAG=sha-<git-sha>
MAILER_PULL_POLICY=always
```

If the image is cached locally, use `MAILER_PULL_POLICY=never` to avoid re-authentication.

Before proceeding manually to step 4+, for local build (3-A) **always** set `.env` to
`amane-mailer` / `local-rehearsal` / `never` as above. Leaving the GHCR placeholder from
`.env.example` (`sha-replace-with-published-git-sha`) causes image pull to fail unless you
also attach `compose.local-rehearsal.build.yml`.
`local-rehearsal.ps1 -Build` fills these when creating a new `.env`, but does not overwrite
an existing `.env`.

### 4. Validate compose → migrate → start

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml config --quiet

docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml up -d --wait mailer
```

With a new image, **always** run `mailer-migrate` successfully before starting `mailer`.

### 5. HTTP / CLI health

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/healthz | Select-Object -ExpandProperty Content
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/readyz | Select-Object -ExpandProperty Content

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  exec -T mailer /app/Amane.Mailer healthcheck
```

Port `5281` is the published port in `compose.local-rehearsal.yml`.
It does not conflict with `5280` in `infra/docker/docker-compose.local.yml` (Mailpit development stack).

### 6. Admin UI

To verify the Admin UI, set `AMANE_ADMIN_*` in the same PowerShell session where you start
`local-rehearsal.ps1`. For access via Docker port publish during local rehearsal, explicitly set
`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` and `AMANE_ADMIN_ALLOW_HTTP=true`.
`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` is a `/admin` request `Connection.LocalIpAddress`
allowlist, not a socket bind. Actual host exposure is still limited by
`compose.local-rehearsal.yml` `ports` (`127.0.0.1:5281`).
The old `AMANE_ADMIN_BIND` / `MAILER_ADMIN_BIND` names remain as deprecated aliases.
This is for local HTTP verification only. On deploy hosts, keep `AMANE_ADMIN_ALLOW_HTTP=false`
under the HTTPS reverse-proxy setup.

The Admin UI is an **internal-network-only, experimental** operational aid. Current limits:
login throttle is in-memory only (resets on process restart);
no durable server-side session store (cookie auth only) and immediate session revocation on admin disable or credential change is not implemented;
no per-admin tenant scope; audit log is structured log (stdout) only
(SQLite persistence is tracked in [#6](https://github.com/kooiei-in4a/amane-mailer/issues/6)).

```powershell
$composeFiles = @("-f", "compose.yml", "-f", "compose.local-rehearsal.yml")
# Run the next line only when verifying with local-rehearsal.ps1 -Build.
$composeFiles += "-f", "compose.local-rehearsal.build.yml"

$adminPassword = [System.Net.NetworkCredential]::new(
  "",
  (Read-Host "Mailer admin password" -AsSecureString)
).Password
$hash = @($adminPassword, $adminPassword) |
  docker compose --env-file .env @composeFiles run --rm -T --no-deps mailer admin hash-password 2>$null |
  Select-Object -Last 1

if ($hash -notlike "pbkdf2:sha256:*") {
  throw "Failed to generate AMANE_ADMIN_PASSWORD_HASH."
}

$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"
$env:AMANE_ADMIN_PII_LIST_MODE = "masked"

.\scripts\local-rehearsal.ps1 -Build
```

When verifying with a pre-pulled GHCR image, do not add `compose.local-rehearsal.build.yml` to
`$composeFiles`, and use `.\scripts\local-rehearsal.ps1` as the final command.

After startup, open <http://127.0.0.1:5281/admin/login> in a browser and log in with `admin` and
the password you entered above. Verification is complete if you can reach `/admin/mail-requests` after login.

Minimal headless check:

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/login |
  Select-Object -ExpandProperty StatusCode
```

Expected: `200`.

To verify through login:

```powershell
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginPage = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/login -WebSession $session
$csrf = [regex]::Match($loginPage.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
if (-not $csrf) { throw "CSRF token not found." }

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/api/login `
  -WebSession $session `
  -Method Post `
  -Body @{
    __RequestVerificationToken = $csrf
    username = "admin"
    password = $adminPassword
  } | Out-Null

$mailRequests = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/mail-requests -WebSession $session
if ($mailRequests.StatusCode -ne 200) {
  throw "Unexpected /admin/mail-requests status: $($mailRequests.StatusCode)"
}
```

### 7. Confirm tokens are present in the container

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  exec -T mailer /bin/sh -c 'test -n "$MAIL_SERVICE_TOKEN_DEVELOP" && test -n "$MAIL_SERVICE_TOKEN_STAGING" && test -n "$MAIL_SERVICE_TOKEN_PRODUCTION" && echo TOKENS_PRESENT'
```

### 8. Shared network and alias `mailer`

Compose creates an external network named `MAILER_NETWORK_NAME` (set in `.env`) and assigns alias
`mailer` to the `mailer` service. When Consumer app compose joins the same network, it can reach
`http://mailer:8080`.

```powershell
$networkName = (Get-Content .env | Select-String '^MAILER_NETWORK_NAME=') -replace '^MAILER_NETWORK_NAME=',''
docker network inspect $networkName --format '{{.Name}} internal={{.Internal}}'

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  run --rm --no-deps --network $networkName curlimages/curl:8.11.1 `
  -fsS http://mailer:8080/healthz
```

Also confirm `amane-mailer_internal` (the compose project's internal network) has
`internal: true`.

```powershell
docker network inspect amane-mailer_internal --format 'internal={{.Internal}}'
```

### 9. Cleanup

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml down
```

To reset the DB, delete `infra/deploy/data/` and repeat from step 4.

## Optional: no-send shared Mailer smoke

No-send API / auth / SQLite DB verification. ACS is not required (`ACS_CONNECTION_STRING` stays empty).
**Not run in quick start.** `local-rehearsal.ps1` invokes it only when you explicitly pass `-RunSmoke`.

Manual run:

```powershell
# Pass the same compose override as the PowerShell rehearsal (include build override with -Build)
$env:MAIL05A_COMPOSE_DIR = (Resolve-Path .).Path
$env:MAIL05A_COMPOSE_EXTRA = "compose.local-rehearsal.build.yml"   # -Build only
bash ./drills/mail-05a-no-send-smoke.sh
```

```bash
# Git Bash (same docker context as Docker Desktop)
export MAIL05A_COMPOSE_DIR=/path/to/repo/infra/deploy
export MAIL05A_COMPOSE_EXTRA="compose.local-rehearsal.build.yml"   # local build
bash drills/mail-05a-no-send-smoke.sh
```

Align `MAIL05A_COMPOSE_EXTRA` with the compose override used by `local-rehearsal.ps1`.
If omitted, only `compose.yml` is referenced and `mailer` may be recreated with the GHCR
placeholder image from `.env`.

On Windows, WSL `docker` and Docker Desktop `docker` can use different contexts.
`-RunSmoke` fails if bash-side `docker compose ps` cannot find `mailer`.
If WSL `bash.exe` cannot `cd` to a Windows path, the script fails with a message suggesting Git Bash.
Use Git Bash + Docker Desktop, or align contexts before running.

The script uses SQLite Mailer CLI (`healthcheck`, `db stats`, `db request-state`) and a temporary
`curlimages/curl` compose service. PostgreSQL / `psql` is not required.

The ACS live-send drill (`mail-05a-acs-drill.sh`) is **out of scope for local rehearsal**.
Run it from the deploy directory on a deploy host.

## Rollback (rehearsal notes)

Common minimal rollback steps for local rehearsal:

| Change type | Rollback |
|-------------|----------|
| Image tag | Restore previous `MAILER_IMAGE_TAG` in `.env` → `pull` (if needed) → `mailer-migrate` → `up -d mailer` |
| Tenant JSON | Restore `tenants.json` from timestamped backup → `up -d mailer` (no migrate) |
| Tokens only | Restore `MAIL_SERVICE_TOKEN_*` in `.env` → `up -d mailer` |

Applying an old image tag to a DB that already has forward-only migrations will fail migrate.
In that case, delete `data/` or restore SQLite from backup before rolling back.

## Out of scope (remaining pre-release checks)

- GHCR pull / deploy rehearsal on deploy hosts
- Production `live_sending=true` with approved ACS senders
- Live connection tests from Consumer app compose in each environment
