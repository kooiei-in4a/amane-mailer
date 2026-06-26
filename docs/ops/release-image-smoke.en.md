[日本語](release-image-smoke.md)

# Clean-state smoke for the published release image

Pulls the published GHCR runtime image (default `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`)
from a clean state, starts Mailer + Mailpit, and smokes the public release runtime path.

Unlike `infra/docker/docker-compose.local.yml` (which builds from source), this smoke
exercises the **published image itself**. Tenant configuration is the safe example baked
into the image (`/app/config/mailer/tenants.example.json`); no host tenant JSON is mounted.
Mailer state lives in a named volume that `docker compose down -v` removes on exit.

## Prerequisites

- Docker (with the compose plugin) running.
- `bash`, `curl`, and `sha256sum` available.
- The GHCR image is pullable (run `docker login ghcr.io` first if the package is private;
  see [GHCR image publish guide](ghcr-image-publish.en.md)).
- Default host ports `15280` (Mailer) and `18025` (Mailpit) are free.

## Run

From the repository root:

```bash
bash scripts/release-smoke.sh
```

The script:

1. Removes any leftover smoke compose project from a previous run.
2. Pulls the published image and Mailpit, then starts them in a clean project / named volume.
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
| `MAILER_IMAGE_TAG` | `v0.1.0` | Tag under test |
| `MAILER_PULL_POLICY` | `always` | Set `missing` to reuse a local image |
| `MAILER_HTTP_PORT` | `15280` | Mailer host port |
| `MAILPIT_HTTP_PORT` | `18025` | Mailpit API/UI host port |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` | Example tenant token |
| `RELEASE_SMOKE_PROJECT` | `amane-mailer-release-smoke` | Compose project name |
| `RELEASE_SMOKE_KEEP` | (unset) | Set `1` to skip cleanup on exit (debugging) |

Smoke a different tag:

```bash
MAILER_IMAGE_TAG=sha-<git-sha> bash scripts/release-smoke.sh
```

## How it differs from the deploy drills

- `scripts/release-smoke.sh`: a release smoke that validates the **published image's**
  HTTP / idempotency / Mailpit delivery from a clean state, using host-side curl only.
- `infra/deploy/drills/mail-05a-*`: no-send / ACS deploy drills against a running compose
  stack on a deploy host. They use the SQLite Mailer CLI (`healthcheck`, `db stats`,
  `db request-state`) and a temporary curl compose client, and go deeper into worker
  toggling and DB state. See [docs/ops/drills/mail-05a-drill-guide.html](drills/mail-05a-drill-guide.html).
