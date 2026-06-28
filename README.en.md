# Amane Mailer

[Japanese README](README.md)

Amane Mailer is a general-purpose mail delivery microservice. It accepts mail
requests, persists them, and delivers them asynchronously via Azure Communication
Services (ACS) or Mailpit through a background Worker. Consumer applications
assemble the body, recipients, and subject, then POST a delivery request — the
Mailer handles transport.

## Layout

- `src/Amane.Mailer`: ASP.NET Core / Native AOT Mailer service.
- `src/Amane.Mailer.Contracts`: source of truth for HTTP contract DTOs, error constants, and payload hash helper (NuGet package).
- `tests/`: Mailer and Contracts test suites.
- `config/mailer`: Safe tenant examples and JSON schema.
- `infra/docker`: Local Docker build and Mailpit compose.
- `infra/deploy`: Deploy-time compose template for production.
- `docs/`: API spec, ADRs, and runbooks.

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) — version pinned in `global.json` (currently 10.0.301)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## Verify Locally

From the repository root:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

## Run With Mailpit

The local compose file builds the Mailer image and starts Mailpit:

```powershell
docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
```

Useful local URLs:

- Mailer health: <http://127.0.0.1:5280/healthz>
- Mailer readiness: <http://127.0.0.1:5280/readyz>
- Mailpit UI: <http://127.0.0.1:8025/>

The default local token is `local-mail-service-token`, and the safe example
tenant is loaded from the local `config/mailer/tenants.example.json` bind mount.
For the full smoke procedure, including Admin UI setup, ACS switching, and Dead
Letter checks, see
[Local Mailer Docker runbook](docs/ops/local-mailer-docker-runbook.en.md) [(ja)](docs/ops/local-mailer-docker-runbook.md).

## Admin UI

Setting `AMANE_ADMIN_ENABLED=true` enables `/admin` (disabled by default).
The admin UI is an **internal-network-only, experimental** operational aid.
Direct exposure to the public internet is not a supported configuration.
In production, use a reverse proxy, firewall, or Docker port publish restriction as the network boundary.

**Current limitations ([ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md) goals not yet implemented)**

- Login throttle is in-memory only (resets on process restart)
- No durable server-side session store (cookie auth only); immediate session revocation on admin disable or credential change is not implemented
- No per-admin tenant scope (single `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`)
- Audit log persists body-view and login success/failure events to the `admin_audit_events` SQLite table (and mirrors them to stdout). Logout, session-expired, and login-rate-limited events, plus retention sweep and optional network identifier hashing, are not yet implemented (tracked in [#6](https://github.com/kooiei-in4a/amane-mailer/issues/6))

## Deployment Notes

The runtime image includes only safe examples and the tenant schema. Real tenant
JSON files are deploy-time inputs and must be mounted into the container:

- Deploy compose: `infra/deploy/compose.yml`
- Safe env template: `infra/deploy/.env.example`
- Tenant schema: `config/mailer/tenants.schema.json`

Do not commit real tenant tokens, ACS connection strings, production sender
addresses, or deploy-host `.env` files.

Operational runbooks:

- [Local deploy rehearsal](docs/ops/local-deploy-rehearsal-runbook.en.md) [(ja)](docs/ops/local-deploy-rehearsal-runbook.md)
- [Backup operations](docs/ops/backup-operations.en.md) [(ja)](docs/ops/backup-operations.md)
- [Restore procedure](docs/ops/restore-procedure.en.md) [(ja)](docs/ops/restore-procedure.md)
- [Restore verification](docs/ops/restore-verification.en.md) [(ja)](docs/ops/restore-verification.md)

To smoke the published GHCR image (default `ghcr.io/kooiei-in4a/amane-mailer:v0.1.1`)
from a clean state — pulling it, starting Mailer + Mailpit, and checking `/healthz`,
`/readyz`, a valid POST, Mailpit delivery, idempotent repost, conflict, 401, and 403 —
run `scripts/release-smoke.sh` (Linux / macOS / Git Bash) or
`scripts/release-smoke.ps1` (Windows / PowerShell with Docker Desktop). See
[Published release image smoke](docs/ops/release-image-smoke.en.md) [(ja)](docs/ops/release-image-smoke.md)
for steps and configuration.

The published GHCR runtime image for the default `v0.1.1` tag is
**`linux/amd64` only**. On Apple Silicon or ARM Linux hosts, you can smoke that
tag only when Docker Desktop or the Docker engine can run amd64 images through
emulation, for example with
`MAILER_IMAGE_PLATFORM=linux/amd64 bash scripts/release-smoke.sh`. For
multi-arch releases, confirm the platform / runtime manifest digest in the
Docker manifest or release notes, then smoke each target platform with
`MAILER_IMAGE_PLATFORM=linux/amd64` or `MAILER_IMAGE_PLATFORM=linux/arm64`.

```bash
bash scripts/release-smoke.sh
```

```powershell
.\scripts\release-smoke.ps1
```

No-send / ACS deploy drill helper scripts under `infra/deploy/drills/`
(`mail-05a-*`) use the SQLite Mailer CLI (`healthcheck`, `db stats`,
`db request-state`) and a temporary curl compose client. See
[docs/ops/drills/mail-05a-drill-guide.html](docs/ops/drills/mail-05a-drill-guide.html).
For local deploy rehearsal (no ACS live send), use
[Local deploy rehearsal runbook](docs/ops/local-deploy-rehearsal-runbook.en.md) [(ja)](docs/ops/local-deploy-rehearsal-runbook.md).

## Contracts Package

`Amane.Mailer.Contracts` is published to nuget.org.
Publish versions manually with [`.github/workflows/publish-contracts.yml`](.github/workflows/publish-contracts.yml)
by running it from a release tag ref. The package version is derived from the tag and validated against the csproj `<Version>`.

The code-level source of truth for the HTTP contract is `src/Amane.Mailer.Contracts/`. The Mailer runtime references the same DTOs / constants, and [OpenAPI](docs/api/openapi.yaml) is the Consumer-facing HTTP reference / public schema synchronized with them. Service release versions, Docker image tags, NuGet package versions, and OpenAPI `info.version` are all kept in sync under the same `X.Y.Z` (see [Versioning Policy](docs/service-spec.en.md#versioning-policy)).

The Contracts package targets `net8.0` for broader consumer compatibility. The Mailer runtime targets `net10.0`, but release version alignment and target framework are separate concerns. See the Target Framework section in [`src/Amane.Mailer.Contracts/README.md`](src/Amane.Mailer.Contracts/README.md).

## Consumer Quick Start

Minimum information to POST a mail request to a running Mailer:

- **Endpoint**: `POST http://mailer:8080/internal/mail-requests`
- **Auth**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - Default local token: `local-mail-service-token`
- **Required fields**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `to`, `subject`, `payload_hash`
- **`payload_hash`**: SHA-256 of the canonical delivery payload.
  Use `MailPayloadHasher` from `Amane.Mailer.Contracts` (.NET),
  or see [examples/payload-hash/](examples/payload-hash/README.md) for Python / JavaScript / Go,
  and [docs/api/openapi.yaml](docs/api/openapi.yaml) for the algorithm spec.

After starting the local compose stack, you can run this smoke request from the
host. `mail_request_id` is the idempotency key, so use a fresh UUID for each
new request unless you intentionally want to retry the same request.
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

Expected response: `202 Accepted` with this JSON body containing the generated
`request_id`:

```json
{
  "mail_request_id": "<request_id>",
  "status": "accepted"
}
```

A second POST with the same `request_id` and the same JSON is an idempotent
retry, not a new acceptance: it returns `202 Accepted` with
`status: "already_accepted"`. Distinguish new requests from retries by checking
whether the response body `status` is `accepted` or `already_accepted`.

To safely try a conflict, use a local environment only, keep the same
`request_id`, change a hash-covered field such as `subject`, recompute
`payload_hash` for that payload, and POST again. The expected result is
`409 Conflict` / `IDEMPOTENCY_CONFLICT`.

For the Consumer app compose network setup, see the comments in [infra/deploy/compose.yml](infra/deploy/compose.yml).

## Key Docs

- [Service spec](docs/service-spec.en.md) [(ja)](docs/service-spec.md)
- [OpenAPI HTTP reference](docs/api/openapi.yaml)
- [Backup operations](docs/ops/backup-operations.en.md) [(ja)](docs/ops/backup-operations.md)
- [GHCR image publishing](docs/ops/ghcr-image-publish.en.md) [(ja)](docs/ops/ghcr-image-publish.md)
- [Release artifact verification](docs/ops/release-artifact-verification.en.md) [(ja)](docs/ops/release-artifact-verification.md)
- [Configuration README](config/mailer/README.en.md) [(ja)](config/mailer/README.md)
- [Security policy](SECURITY.md)
