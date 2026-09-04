[日本語](release-image-smoke.md)

# Clean-state smoke for the published release image

After v1.3.8 is published, this runbook pulls the GHCR runtime image (current public example: `ghcr.io/kooiei-in4a/amane-mailer:v1.3.8`) from a clean state, starts Mailer +
Mailpit, and smokes the release runtime path.

Unlike `infra/docker/docker-compose.local.yml` (which builds from source), this smoke
exercises the **release image itself after publish**. Tenant configuration is the safe example baked
into the image (`/app/config/mailer/tenants.example.json`); no host tenant JSON is mounted.
Mailer state lives in a named volume that `docker compose down -v` removes on exit.

## Supported platforms

| Category | Scope |
|----------|-------|
| **Release smoke gate (supported)** | Linux local Docker only, via `scripts/release-smoke.sh` |
| **Contract parity (non-gate)** | `scripts/release-smoke.ps1` — PowerShell implementation mirroring the shell contract; validated on Linux via fixture / self-tests |
| **Out of support scope** | Release smoke live runs on Windows Docker Desktop |

Windows Docker Desktop is **not** a supported release / acceptance gate platform.
The official clean-state smoke gate for published release images is **Linux local Docker only**.
`release-smoke.ps1` is retained for contract parity but a Windows live smoke PASS is not required.

## Prerequisites

- Docker (with the compose plugin) running on Linux.
- `bash`, `curl`, and `sha256sum` available.
- The GHCR image is pullable (run `docker login ghcr.io` first if the package is private;
  see [GHCR image publish guide](ghcr-image-publish.en.md)).
- For v1.3.8, the runtime image is **`linux/amd64` only**. Confirm the platform in the release notes or Docker
  manifest and pin `MAILER_IMAGE_PLATFORM=linux/amd64` when needed.
- Default host ports `15280` (Mailer) and `18025` (Mailpit) are free.
- **The target Mailer image must be supplied explicitly via `MAILER_IMAGE_TAG` or `MAILER_IMAGE_DIGEST` (exactly one; no implicit default).**

## Run

From the repository root (**supported canonical operational entrypoint**):

```bash
MAILER_IMAGE_TAG=v1.3.8 bash scripts/release-smoke.sh
```

Smoke by immutable digest:

```bash
MAILER_IMAGE_DIGEST=sha256:<digest> bash scripts/release-smoke.sh
```

### PowerShell variant (contract parity / non-gate)

`scripts/release-smoke.ps1` mirrors the shell contract.
Only the **Linux local Docker shell entrypoint** is supported as a release gate.
Validate the PowerShell contract on Linux with:

- `scripts/release-smoke-preflight-self-test.ps1`
- `scripts/release-client-self-test.ps1`

Live smoke on Windows Docker Desktop is **out of support scope**.
Do not use WSL `bash scripts/release-smoke.sh` as a gate either; Docker context can diverge from a supported Linux host.

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

Mailpit is a smoke helper and is not included in the release artifact. See the
[container image pinning policy](container-image-pinning.en.md) for the
intentional `latest` usage and how to pin it when needed.

## Recorded smoke results

Value-free smoke results for `v1.3.8` (digest, date, environment, per-check pass/fail)
are recorded in [docs/releases/v1.3.8.md](../releases/v1.3.8.md).
Previous `v1.2.0` results remain in [docs/releases/v1.2.0.md](../releases/v1.2.0.md).
