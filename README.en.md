# Amane Mailer

[Japanese README](README.md)

Amane Mailer is a general-purpose mail delivery microservice. It accepts mail
requests, persists them, and delivers them asynchronously via Azure Communication
Services (ACS) or Mailpit through a background Worker. Consumer applications
assemble the body, recipients, and subject, then POST a delivery request — the
Mailer handles transport.

## Layout

- `src/Amane.Mailer`: ASP.NET Core / Native AOT Mailer service.
- `src/Amane.Mailer.Contracts`: DTOs, error constants, and payload hash helper (NuGet package).
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

MAIL-05a drill helper scripts under `infra/deploy/drills/` use the SQLite Mailer
CLI (`healthcheck`, `db stats`, `db request-state`) and a temporary curl compose
client. See
[docs/ops/drills/mail-05a-drill-guide.html](docs/ops/drills/mail-05a-drill-guide.html).
For local deploy rehearsal (no ACS live send), use
[Local deploy rehearsal runbook](docs/ops/local-deploy-rehearsal-runbook.en.md) [(ja)](docs/ops/local-deploy-rehearsal-runbook.md).

## Contracts Package

`Amane.Mailer.Contracts` is published manually to GitHub Packages via
[`.github/workflows/publish-contracts.yml`](.github/workflows/publish-contracts.yml).
Use `workflow_dispatch` with an explicit version (e.g. `1.0.0-alpha.1`).

## Consumer Quick Start

Minimum information to POST a mail request to a running Mailer:

- **Endpoint**: `POST http://mailer:8080/internal/mail-requests`
- **Auth**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - Default local token: `local-mail-service-token`
- **Required fields**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `to`, `subject`, `payload_hash`
- **`payload_hash`**: SHA-256 of the canonical delivery payload.
  Use `MailPayloadHasher` from `Amane.Mailer.Contracts` (.NET),
  or see [docs/api/openapi.yaml](docs/api/openapi.yaml) for the algorithm spec.

After starting the local compose stack, you can run this smoke request from the
host:

```bash
curl -i -X POST http://127.0.0.1:5280/internal/mail-requests \
  -H "Authorization: Bearer local-mail-service-token" \
  -H "Content-Type: application/json" \
  -d '{
    "tenant_id": "00000000-0000-0000-0000-000000000101",
    "mail_request_id": "00000000-0000-0000-0000-000000000201",
    "source_service": "example-service",
    "purpose": "FormResponseNotification",
    "to": [
      { "email": "admin@example.com" }
    ],
    "subject": "New response",
    "text_body": "A new response arrived.",
    "payload_hash": "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9"
  }'
```

Expected response: `202 Accepted` with this JSON body:

```json
{
  "mail_request_id": "00000000-0000-0000-0000-000000000201",
  "status": "accepted"
}
```

For the Consumer app compose network setup, see the comments in [infra/deploy/compose.yml](infra/deploy/compose.yml).

## Key Docs

- [Service spec](docs/service-spec.en.md) [(ja)](docs/service-spec.md)
- [OpenAPI contract](docs/api/openapi.yaml)
- [Backup operations](docs/ops/backup-operations.en.md) [(ja)](docs/ops/backup-operations.md)
- [GHCR image publishing](docs/ops/ghcr-image-publish.en.md) [(ja)](docs/ops/ghcr-image-publish.md)
- [Configuration README](config/mailer/README.en.md) [(ja)](config/mailer/README.md)
