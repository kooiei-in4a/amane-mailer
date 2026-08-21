# Amane Mailer

[Japanese README](README.md)

Amane Mailer is a general-purpose mail delivery microservice. It accepts mail
requests, persists them, and delivers them asynchronously via Azure Communication
Services (ACS) or Mailpit through a background Worker. Consumer applications
assemble the body, recipients, and subject, then POST a delivery request — the
Mailer handles transport. It supports multiple To / CC / BCC recipients,
validated attachments, idempotent acceptance, scheduled delivery, and delivery
status queries.

## v1.3.0 highlights

- Multiple `to` / `cc` / `bcc` recipients, including Cc-only / Bcc-only requests, with recipient-level delivery state
- Bounded attachments (PDF, JPEG, PNG, DOCX, XLSX, CSV, and TXT) with validation and spool storage
- Durable submission evidence that suppresses duplicate provider invocation after an ambiguous outcome; the public terminal state is `delivery_unknown`
- BCC omitted from MIME headers, masked by default in Admin, and revealed only through an explicit audited capability
- Multi-arch `linux/amd64` / `linux/arm64` release images and Native AOT setup bundles

See the [service specification](docs/service-spec.en.md) and [v1.3.0 release record](docs/releases/v1.3.0.md)
for the detailed contract, limits, and upgrade procedure.

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

After v1.3.0 is published, smoke the GHCR image (default `ghcr.io/kooiei-in4a/amane-mailer:v1.3.0`)
from a clean state — pulling it, starting Mailer + Mailpit, and checking `/healthz`,
`/readyz`, a valid POST, Mailpit delivery, idempotent repost, conflict, 401, and 403 —
run `scripts/release-smoke.sh` (Linux / macOS / Git Bash) or
`scripts/release-smoke.ps1` (Windows / PowerShell with Docker Desktop). See
[Published release image smoke](docs/ops/release-image-smoke.en.md) [(ja)](docs/ops/release-image-smoke.md)
for steps and configuration. Published identities:
[v1.3.0 release record](docs/releases/v1.3.0.md) /
[GitHub Release](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.0).

For the v1.3.0 release, the default smoke tag `v1.3.0` is a
**multi-arch** GHCR runtime image
(`linux/amd64` and `linux/arm64`). For smoke runs, confirm the platform in the
release notes or Docker manifest, then set `MAILER_IMAGE_PLATFORM=linux/amd64` or
`MAILER_IMAGE_PLATFORM=linux/arm64`. On hosts that can only run amd64 through
emulation, pin `linux/amd64` explicitly.

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

Minimum information to POST a mail request to a running Mailer and, when needed, query delivery status with GET.

### Submit a mail request (POST)

**Official Consumer SDKs (TypeScript / Python):** request builder, automatic
`payload_hash`, typed errors, and 503 retries — see [sdk/](sdk/README.md).

- **Endpoint**: `POST http://mailer:8080/internal/mail-requests`
- **Auth**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - Default local token: `local-mail-service-token`
- **Required fields**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `subject`, `payload_hash`, plus at least one recipient across `to`, `cc`, and `bcc`
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

`delivery_unknown` is terminal when provider invocation started but acceptance could not be proved. Do not automatically or manually resend
the same `mail_request_id`. If business requirements require another send, assess duplicate risk and submit a new request with a new ID.

### v1.3 advanced request (To + CC + BCC + attachment)

`to` may be omitted, `null`, or empty when `cc` or `bcc` supplies a recipient. Each role allows at most 10 recipients and the combined
total is 20; duplicate canonical addresses are rejected across and within roles. Attachments allow at most 5 items, 2 MiB per file,
5 MiB decoded total, an 8 MiB provider envelope, and a 16 MiB HTTP envelope. The server revalidates file type, filename, digest, and size.

```json
{
  "tenant_id": "00000000-0000-0000-0000-000000000101",
  "mail_request_id": "<new-uuid>",
  "source_service": "example-service",
  "purpose": "FormResponseNotification",
  "to": [{ "email": "admin@example.com" }],
  "cc": [{ "email": "team@example.com" }],
  "bcc": [{ "email": "audit@example.com" }],
  "subject": "Invoice attached",
  "text_body": "Please find the invoice attached.",
  "attachments": [{
    "file_name": "hello.txt",
    "content_type": "text/plain",
    "content_base64": "SGVsbG8=",
    "content_sha256": "185f8db32271fe25f561a6fc938b2e264306ec304eda518007d1764826381969",
    "byte_length": 5
  }],
  "payload_hash": "9c093783657de26bab51d19b69a23d73d0c9c005f58c5e1762ef0d2514289bc6"
}
```

`payload_hash` is the SHA-256 of the canonical delivery payload. It projects recipient roles and attachment metadata (NFC filename,
canonical content type, byte length, digest, and array order), not raw Base64. See [Contracts](src/Amane.Mailer.Contracts/README.md),
[SDKs](sdk/README.md), and the [payload-hash examples](examples/payload-hash/README.md) for the shared vectors.

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

After the Worker finishes delivery, `status` becomes `delivered` and related fields update. See [docs/api/openapi.yaml](docs/api/openapi.yaml) and the [service spec delivery-status section](docs/service-spec.en.md#delivery-status-query-get) for the full HTTP status table, error codes, and v1.3 resend boundary.

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
