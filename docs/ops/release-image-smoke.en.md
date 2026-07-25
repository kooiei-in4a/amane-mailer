[日本語](release-image-smoke.md)

# Clean-state smoke for the published release image

After v1.0.0 is published, this runbook pulls the GHCR runtime image (default
`ghcr.io/kooiei-in4a/amane-mailer:v1.0.0`) from a clean state, starts Mailer +
Mailpit, and smokes the release runtime path.

Unlike `infra/docker/docker-compose.local.yml` (which builds from source), this smoke
exercises the **release image itself after publish**. Tenant configuration is the safe example baked
into the image (`/app/config/mailer/tenants.example.json`); no host tenant JSON is mounted.
Mailer state lives in a named volume that `docker compose down -v` removes on exit.

## Prerequisites

- Docker (with the compose plugin) running.
- On Linux / macOS / Git Bash: `bash`, `curl`, and `sha256sum` available.
- On Windows: PowerShell 5.1+ and Docker Desktop (same Docker CLI context as PowerShell).
- The GHCR image is pullable (run `docker login ghcr.io` first if the package is private;
  see [GHCR image publish guide](ghcr-image-publish.en.md)).
- For the v1.0.0 release, the default smoke tag `v1.0.0` is expected to be a
  **multi-arch** GHCR runtime image after publish
  (`linux/amd64` and `linux/arm64`). For smoke runs, confirm the platform in the
  release notes or Docker manifest, then set `MAILER_IMAGE_PLATFORM=linux/amd64` or
  `MAILER_IMAGE_PLATFORM=linux/arm64`.
- On hosts that can only run amd64 through emulation, pin `linux/amd64` explicitly.
- Default host ports `15280` (Mailer) and `18025` (Mailpit) are free.

## Run

From the repository root:

Linux / macOS / Git Bash:

```bash
bash scripts/release-smoke.sh
```

Windows (PowerShell, Docker Desktop):

```powershell
.\scripts\release-smoke.ps1
```

On Windows, prefer the PowerShell entrypoint above. Running
`bash scripts/release-smoke.sh` through WSL can target a different Docker daemon
than Docker Desktop's Windows CLI context.

The script:

1. Removes any leftover smoke compose project from a previous run.
2. Pulls the target release image and Mailpit, then starts them in a clean project / named volume.
3. Runs the checks below, printing `[PASS]` / `[FAIL]` per line.
4. Removes the compose project and volume on exit (including on failure).

Checks:

- `GET /healthz` returns `200`
- `GET /readyz` returns `200`
- A valid `POST /internal/mail-requests` returns `202 accepted`
- Mailpit receives the message
- Same `mail_request_id` + same payload returns `202 already_accepted`
- Same `mail_request_id` + different payload returns `409 IDEMPOTENCY_CONFLICT`
- An invalid token returns `401 UNAUTHORIZED_TENANT`
- An unknown `source_service` returns `403 SOURCE_SERVICE_NOT_ALLOWED`

Any failure makes the exit code `1` and prints `Smoke result: N passed, M failed`.
If startup itself fails, the script prints `docker compose ps` and recent logs.

## Configuration (environment variables, all optional)

| Variable | Default | Purpose |
|----------|---------|---------|
| `MAILER_IMAGE_REPOSITORY` | `ghcr.io/kooiei-in4a/amane-mailer` | Image repository |
| `MAILER_IMAGE_TAG` | `v1.0.0` | Tag under test |
| `MAILER_IMAGE_PLATFORM` | `linux/amd64` | Mailer runtime image platform to smoke. For multi-arch releases, run once per release-noted platform such as `linux/amd64` and `linux/arm64`. |
| `MAILER_PULL_POLICY` | `always` | Set `missing` to reuse a local image |
| `MAILPIT_IMAGE` | `axllent/mailpit:latest` | Mailpit helper image. The default `latest` is intentional; override it when a tag / digest pin is needed. |
| `MAILER_HTTP_PORT` | `15280` | Mailer host port |
| `MAILPIT_HTTP_PORT` | `18025` | Mailpit API/UI host port |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` | Example tenant token |
| `RELEASE_SMOKE_PROJECT` | `amane-mailer-release-smoke` | Compose project name |
| `RELEASE_SMOKE_KEEP` | (unset) | Set `1` to skip cleanup on exit (debugging) |

Smoke a different tag:

```bash
MAILER_IMAGE_TAG=sha-<git-sha> bash scripts/release-smoke.sh
```

```powershell
$env:MAILER_IMAGE_TAG = 'sha-<git-sha>'; .\scripts\release-smoke.ps1
```

Mailpit is a smoke helper and is not included in the release artifact. See the
[container image pinning policy](container-image-pinning.en.md) for the
intentional `latest` usage and how to pin it when needed.

## Recorded smoke results

Value-free smoke results for `v1.0.0` (digest, date, environment, per-check pass/fail)
are recorded in [docs/releases/v1.0.0.md](../releases/v1.0.0.md) after publish.
Previous `v0.9.2` results remain in [docs/releases/v0.9.2.md](../releases/v0.9.2.md).
Previous `v0.9.1` results remain in [docs/releases/v0.9.1.md](../releases/v0.9.1.md).
Previous `v0.9.0` results remain in [docs/releases/v0.9.0.md](../releases/v0.9.0.md).
Previous `v0.4.0` results remain in [docs/releases/v0.4.0.md](../releases/v0.4.0.md).
Previous `v0.3.0` results remain in [docs/releases/v0.3.0.md](../releases/v0.3.0.md).
Older `v0.2.0` results remain in [docs/releases/v0.2.0.md](../releases/v0.2.0.md).

## How it differs from the deploy drills

- `scripts/release-smoke.sh` / `scripts/release-smoke.ps1`: a release smoke that validates the **target release image's**
  HTTP / idempotency / Mailpit delivery from a clean state. The bash script uses host-side
  `curl`; the PowerShell script uses `Invoke-WebRequest`. Admin UI, HTTPS webhook tenant
  startup, and `db backup` CLI are out of scope.
- `scripts/native-aot-path-smoke.sh`: black-box checks against the **linux-x64 Native AOT
  binary** published by CI `Native AOT publish smoke` — Admin login, HTTPS webhook tenant
  `/readyz`, and `db backup` (issue #286). It does not cover the release image or Mailpit
  delivery. ACS live stays manual because it depends on secrets.
- `infra/deploy/drills/mail-05a-*`: no-send / ACS deploy drills against a running compose
  stack on a deploy host. They use the SQLite Mailer CLI (`healthcheck`, `db stats`,
  `db request-state`) and a temporary curl compose client, and go deeper into worker
  toggling and DB state. See [docs/ops/drills/mail-05a-drill-guide.html](drills/mail-05a-drill-guide.html).
