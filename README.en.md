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

- [.NET SDK](https://dotnet.microsoft.com/download) — version pinned in `global.json` (currently 10.0.303)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## Setup entry point

For first-time setup, start at the single
[setup entry point](docs/ops/setup-guide.en.md) [(ja)](docs/ops/setup-guide.md)
and prefer **Easy Setup**
([Easy Setup](docs/ops/setup-guide.en.md#easy-setup-recommended) /
[Manual](docs/ops/setup-guide.en.md#manual-deployment) /
[Hardened](docs/ops/setup-guide.en.md#hardened-deployment)).
Detailed steps link out to existing runbooks. Judgment, order, and safety
boundaries are owned by the setup guide.

## Verify Locally

From the repository root:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

See [Code quality gates](docs/ops/code-quality-gates.en.md)
[(ja)](docs/ops/code-quality-gates.md) for formatter and staged analyzer details.

## Run With Mailpit

To confirm your **first delivered message**, start with the Admin-free
[Zero-Admin first-mail quickstart](docs/ops/first-mail-quickstart.en.md) [(ja)](docs/ops/first-mail-quickstart.md).
On PowerShell, run `.\scripts\local-first-mail-smoke.ps1`; on bash, run `bash scripts/local-first-mail-smoke.sh` for the same checks automatically.

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
For Linux / macOS bash and curl steps that verify Mailpit receipt, idempotent
repost, and conflict, see
[Local Mailer + Mailpit runbook for Linux/macOS](docs/ops/local-mailer-docker-runbook-bash.en.md) [(ja)](docs/ops/local-mailer-docker-runbook-bash.md).

## Admin UI

Setting `AMANE_ADMIN_ENABLED=true` enables `/admin` (disabled by default).
The admin UI is an **internal-network-only, experimental** operational aid.
Direct exposure to the public internet is not a supported configuration.
In production, use a reverse proxy, firewall, or Docker port publish restriction as the network boundary.

**Current limitations ([ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md) / [ADR 0014](docs/adr/0014-admin-session-tenant-throttle-audit-design.md))**

- Login throttle is SQLite-backed (lock state survives process restart)
- Durable server-side session store (credential-hash change revocation, explicit logout, expiry, concurrent session limit)
- Per-admin tenant scope is implemented (`admin_users` / `admin_user_tenant_scopes`). Scoped admins can view and operate only allowed tenants. Break-glass admins can access all tenants (enhanced audit). When two or more effective tenants exist and Admin is enabled, startup fails closed unless at least one scoped or break-glass admin exists
- The env bootstrap admin (`AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`) is seeded into `admin_users` on first database creation with **all configured tenant scopes** (`is_break_glass=false`; **not** treated as break-glass). In multi-tenant production, avoid relying on the bootstrap admin; provision per-tenant scoped admins instead ([runbook](docs/ops/local-mailer-docker-runbook.en.md#admin-tenant-scope-operations))
- Scoped / break-glass admins are created with `admin user create` (generate hashes with `admin hash-password`)
- Audit log persists body-view and auth events (login, logout, session expired, account locked, login rate limited) to `admin_audit_events` (stdout mirror). Retention sweep uses `MAILER_ADMIN_AUDIT_RETENTION_DAYS` (default 180 days); explicit purge via `db admin-audit purge --older-than-days <days>`
- When `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true`, raw IP addresses are not stored in the database; keyed hashes are used instead (startup fail-closed when the key is unset)

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
- [ACS secret / platform-owned sender registration CLI](docs/ops/register-acs-cli-runbook.en.md) [(ja)](docs/ops/register-acs-cli-runbook.md)
- [Backup operations](docs/ops/backup-operations.en.md) [(ja)](docs/ops/backup-operations.md)
- [Restore procedure](docs/ops/restore-procedure.en.md) [(ja)](docs/ops/restore-procedure.md)
- [Restore verification](docs/ops/restore-verification.en.md) [(ja)](docs/ops/restore-verification.md)

After v1.3.6 is published, smoke the GHCR image (default `ghcr.io/kooiei-in4a/amane-mailer:v1.3.6`)
from a clean state — pulling it, starting Mailer + Mailpit, and checking `/healthz`,
`/readyz`, a valid POST, Mailpit delivery, idempotent repost, conflict, 401, and 403 —
run `scripts/release-smoke.sh` on Linux local Docker (operational canonical entrypoint).
On Windows, run the equivalent normal-path live smoke with `scripts/release-smoke.ps1`
(PowerShell + Docker Desktop; same contract as the shell script). See
[Published release image smoke](docs/ops/release-image-smoke.en.md) [(ja)](docs/ops/release-image-smoke.md)
for steps and configuration. Published identities:
[v1.3.6 release record](docs/releases/v1.3.6.md) /
[GitHub Release](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.6).

The v1.3.6 GHCR runtime image is **`linux/amd64` only**.
The current public release tag is `v1.3.6`, but release smoke requires an explicit
`MAILER_IMAGE_TAG` or `MAILER_IMAGE_DIGEST` (no implicit default).
Confirm the platform in the release notes or Docker manifest and pin
`MAILER_IMAGE_PLATFORM=linux/amd64` when needed.

```bash
MAILER_IMAGE_TAG=v1.3.6 bash scripts/release-smoke.sh
```

```powershell
$env:MAILER_IMAGE_TAG = 'v1.3.6'
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

Minimum information to POST a mail request to a running Mailer and, when needed, query delivery status with GET.

### Submit a mail request (POST)

**Official Consumer SDKs (TypeScript / Python):** request builder, automatic
`payload_hash`, typed errors, and 503 retries — see [sdk/](sdk/README.md).

- **Endpoint**: `POST http://mailer:8080/internal/mail-requests`
- **Auth**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - Default local token: `local-mail-service-token`
- **Required JSON fields**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `subject`, `payload_hash`
- **Recipient requirement**: at least one recipient across the `to`, `cc`, and `bcc` roles. Treat an omitted, `null`, or empty role as zero recipients.
- **Content requirement**: at least one of `html_body` and `text_body` is required.
- **`payload_hash`**: SHA-256 of the canonical delivery payload.
  Use `MailPayloadHasher` from `Amane.Mailer.Contracts` (.NET),
  or see [examples/payload-hash/](examples/payload-hash/README.md) for Python / JavaScript / Go,
  verify a request JSON file with `python examples/payload-hash/python/verify_request.py request.json`,
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

### Query delivery status (GET)

`202 Accepted` only means the request was persisted. Use GET to learn the Worker delivery outcome (`delivered`, `failed`, and so on).

- **Endpoint**: `GET http://mailer:8080/internal/mail-requests/{mail_request_id}?tenant_id={uuid}&source_service={name}`
- **Auth**: same Bearer token as POST
- **Required query params**: `tenant_id`, `source_service` (same values as the POST body)
- **Response fields**: `mail_request_id`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `scheduled_at`, `accepted_at`, `delivered_at`, `last_error_code`
- **Optional**: POST `scheduled_at` (UTC) for deferred send. Pre-send cancel / reschedule are documented under OpenAPI `/cancel` and `/reschedule`
- **No PII**: recipient, subject, and body are not returned

`status` values describe Worker delivery state (`queued`, `processing`, `delivered`, `failed`, `dead_lettered`, `cancelled`, `delivery_unknown`). They are separate from POST acceptance values `accepted` / `already_accepted`.

`delivery_unknown` is a terminal status. It means provider invocation started but provider acceptance could not be proved; it does not mean the message was unsent or safe to retry. Mailer does not automatically or manually resend delivery for the same `mail_request_id`. This is distinct from a Consumer SDK retry of a transient HTTP 503, an idempotent retry of the same JSON POST, or a deliberate business resend with a new `mail_request_id` after assessing duplicate risk.

Missing IDs and other tenants' IDs both return **404 `NOT_FOUND`** without leaking existence.

Example immediately after POST (reuse the same `request_id`, `tenant_id`, and `source_service`):

```bash
curl -fsS "http://127.0.0.1:5280/internal/mail-requests/${request_id}?tenant_id=00000000-0000-0000-0000-000000000101&source_service=example-service" \
  -H "Authorization: Bearer local-mail-service-token"
```

Expected response right after acceptance:

```json
{
  "mail_request_id": "<request_id>",
  "status": "queued",
  "attempt_count": 0,
  "max_attempts": 3,
  "accepted_at": "2026-07-21T12:00:00Z"
}
```

After the Worker finishes delivery, `status` becomes `delivered` and related fields update. See [docs/api/openapi.yaml](docs/api/openapi.yaml) and the [service spec delivery-status section](docs/service-spec.en.md#delivery-status-query-get) for the full HTTP status table and error codes.

For the Consumer app compose network setup, see the comments in [infra/deploy/compose.yml](infra/deploy/compose.yml).

For a full runnable Python Consumer sample that computes `payload_hash`, POSTs
to a local Mailer, and handles `accepted` / `already_accepted` /
`IDEMPOTENCY_CONFLICT`, see [examples/consumer-python/](examples/consumer-python/README.md).
For a full runnable Node.js Consumer sample that uses the existing JavaScript
`payload_hash` helper, POSTs to a local Mailer, and handles `accepted` /
`already_accepted` / `IDEMPOTENCY_CONFLICT`, see
[examples/consumer-node/](examples/consumer-node/README.md).

## Branch strategy and CI

Work flows `feature/**` / `fix/**` → `develop` → `main`. After each `main`
merge, sync `main` back into `develop` manually. CI is weighted by branch path:
feature pushes run build/test only, PRs to `develop` add OpenAPI validation and
Native AOT publish smoke, and PRs to `main` run full CI including amd64 Docker
and compose smoke (arm64 Docker on `main` push). See
[Branch strategy and CI weighting](docs/ops/branch-and-ci-workflow.en.md)
[(ja)](docs/ops/branch-and-ci-workflow.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Key Docs

- [Setup entry point](docs/ops/setup-guide.en.md) [(ja)](docs/ops/setup-guide.md)
- [Branch strategy and CI weighting](docs/ops/branch-and-ci-workflow.en.md) [(ja)](docs/ops/branch-and-ci-workflow.md)
- [Service spec](docs/service-spec.en.md) [(ja)](docs/service-spec.md)
- [OpenAPI HTTP reference](docs/api/openapi.yaml)
- [Consumer SDKs](sdk/README.md)
- [Webhook verification](docs/consumer/webhook-verification.md)
- [Prometheus metrics and alerts](docs/ops/metrics-and-alerts.en.md) [(ja)](docs/ops/metrics-and-alerts.md)
- [Backup operations](docs/ops/backup-operations.en.md) [(ja)](docs/ops/backup-operations.md)
- [GHCR image publishing](docs/ops/ghcr-image-publish.en.md) [(ja)](docs/ops/ghcr-image-publish.md)
- [Release artifact verification](docs/ops/release-artifact-verification.en.md) [(ja)](docs/ops/release-artifact-verification.md)
- [Configuration README](config/mailer/README.en.md) [(ja)](config/mailer/README.md)
- [Security policy](SECURITY.md)
