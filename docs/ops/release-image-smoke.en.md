[日本語](release-image-smoke.md)

# Clean-state smoke for the published release image

After v1.3.6 is published, this runbook pulls the GHCR runtime image (current public example: `ghcr.io/kooiei-in4a/amane-mailer:v1.3.6`) from a clean state, starts Mailer +
Mailpit, and smokes the release runtime path.

Unlike `infra/docker/docker-compose.local.yml` (which builds from source), this smoke
exercises the **release image itself after publish**. Tenant configuration is the safe example baked
into the image (`/app/config/mailer/tenants.example.json`); no host tenant JSON is mounted.
Mailer state lives in a named volume that `docker compose down -v` removes on exit.

## Prerequisites

- **Operational canonical release gate**: normal release verification runs on Linux local Docker only.
- Docker (with the compose plugin) running.
- On Linux: `bash`, `curl`, and `sha256sum` available.
- On Windows: PowerShell 5.1+ and Docker Desktop (same Docker CLI context as PowerShell).
- The GHCR image is pullable (run `docker login ghcr.io` first if the package is private;
  see [GHCR image publish guide](ghcr-image-publish.en.md)).
- For v1.3.6, the runtime image is **`linux/amd64` only**. Confirm the platform in the release notes or Docker
  manifest and pin `MAILER_IMAGE_PLATFORM=linux/amd64` when needed.
- Default host ports `15280` (Mailer) and `18025` (Mailpit) are free.
- **The target Mailer image must be supplied explicitly via `MAILER_IMAGE_TAG` or `MAILER_IMAGE_DIGEST` (exactly one; no implicit default).**

`scripts/release-smoke.ps1` mirrors the shell contract for PowerShell. Linux fixture /
self-tests validate the contract; **Windows Docker Desktop can run the same normal-path live smoke**.
Cross-platform implementation acceptance (e.g. issue #506) requires both Linux and Windows live smoke to pass.

## Run

From the repository root (canonical operational entrypoint on Linux):

```bash
MAILER_IMAGE_TAG=v1.3.6 bash scripts/release-smoke.sh
```

Windows (PowerShell, Docker Desktop; same contract as shell):

```powershell
$env:MAILER_IMAGE_TAG = 'v1.3.6'
.\scripts\release-smoke.ps1
```

On Windows, prefer the PowerShell entrypoint above. Running
`bash scripts/release-smoke.sh` through WSL can target a different Docker daemon
than Docker Desktop's Windows CLI context.

Smoke by immutable digest:

```bash
MAILER_IMAGE_DIGEST=sha256:<digest> bash scripts/release-smoke.sh
```

```powershell
$env:MAILER_IMAGE_DIGEST = 'sha256:<digest>'
.\scripts\release-smoke.ps1
```

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

## Configuration (environment variables)

| Variable | Default | Purpose |
|----------|---------|---------|
| `MAILER_IMAGE_TAG` | (required; mutually exclusive with `MAILER_IMAGE_DIGEST`) | Mailer image tag under test (`latest` is rejected) |
| `MAILER_IMAGE_DIGEST` | (required; mutually exclusive with `MAILER_IMAGE_TAG`) | Mailer image digest (`sha256:<64-lowercase-hex>`) |
| `MAILER_IMAGE_REPOSITORY` | `ghcr.io/kooiei-in4a/amane-mailer` | Image repository |
| `MAILER_IMAGE_PLATFORM` | `linux/amd64` | Mailer runtime image platform to smoke |
| `MAILER_PULL_POLICY` | `always` | Set `missing` to reuse a local image |
| `MAILPIT_IMAGE` | `axllent/mailpit:latest` | Mailpit helper image. The default `latest` is intentional. |
| `MAILER_HTTP_PORT` | `15280` | Mailer host port |
| `MAILPIT_HTTP_PORT` | `18025` | Mailpit API/UI host port |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` | Example tenant token |
| `RELEASE_SMOKE_PROJECT` | `amane-mailer-release-smoke` | Compose project name |
| `RELEASE_SMOKE_KEEP` | (unset) | Set `1` to skip cleanup on exit (debugging) |

Smoke a different tag:

```bash
MAILER_IMAGE_TAG=sha-<git-sha> bash scripts/release-smoke.sh
```

PowerShell (same contract; Windows Docker Desktop live smoke):

```powershell
$env:MAILER_IMAGE_TAG = 'sha-<git-sha>'; .\scripts\release-smoke.ps1
```

Mailpit is a smoke helper and is not included in the release artifact. See the
[container image pinning policy](container-image-pinning.en.md) for the
intentional `latest` usage and how to pin it when needed.

## Recorded smoke results

Value-free smoke results for `v1.3.6` (digest, date, environment, per-check pass/fail)
are recorded in [docs/releases/v1.3.6.md](../releases/v1.3.6.md).
Previous `v1.2.0` results remain in [docs/releases/v1.2.0.md](../releases/v1.2.0.md).
