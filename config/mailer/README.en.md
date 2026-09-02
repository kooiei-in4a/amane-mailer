[日本語](README.md)

# Mailer configuration

Schema:

- `tenants.schema.json`

Examples:

- `tenants.example.json` (single-tenant local Mailpit default)
- `tenants.shared.example.json` (three-tenant shared deploy template)
- `tenants.local-acs.json.example` (single-tenant ACS live-send)

Tenant file selection:

```text
one JSON file selected by Mailer:TenantsPath or MAILER_TENANTS_PATH
defaults to config/mailer/tenants.example.json when unset
```

Tenant JSON files are not layered or merged. To use an environment-specific
file, point `Mailer:TenantsPath` or `MAILER_TENANTS_PATH` at that file.

Secrets such as tenant Bearer tokens are not stored in JSON. JSON stores the
environment variable name in `token_env`; set the actual token value in that
environment variable.

Optional `webhook` enables outbound delivery-result webhooks. Set `url` and
`secret_env` (secret value in the environment variable; never plaintext in
tenant JSON). See [webhook verification](../../docs/consumer/webhook-verification.md)
and the `webhook` definition in `tenants.schema.json`.

`provider` normally comes from the tenant JSON. Setting `MAILER_PROVIDER` or the
.NET environment-variable form `Mailer__Provider` (configuration key
`Mailer:Provider`) overrides the provider for every tenant.

Deployment-specific tenant files should be mounted into the container and
validated against `tenants.schema.json` before deployment. The Docker image only
includes safe examples and the schema.
Use `develop` for local verification files unless you intentionally add a new
environment value to the schema.

## Platform-owned sender (`platform-sender.json`)

`platform-sender.json` / `platform-sender.schema.json` hold sender identity
(email and display name) for platform-owned mail that does not belong to a
tenant (for example System Admin confirmation mail). The format is fully
independent of tenant JSON and does not use `tenant_id`, `source_services`,
`token_env`, or other tenant-specific concepts.

- Schema: `platform-sender.schema.json` (`environment` is `staging` only for
  now, `provider` is `acs` only, and `live_sending` must be `false`)
- Example: `platform-sender.example.json`
- Registration: `admin provider register-acs` CLI (interactive input only; the
  command does not accept secret values via arguments or environment variables).
  See the [register-acs CLI runbook](../../docs/ops/register-acs-cli-runbook.en.md).

At the time it is written, no runtime send path reads this file
(MAILER-ACS-INPUT-01 scope). Wiring it into a System Admin confirmation-mail
decision is the responsibility of a separate platform-owned mail request
contract. This command never assigns the sender to an existing tenant or creates
a fake tenant.

## Preflight

Before startup, preflight the tenant JSON against the current shell environment.
Secret values themselves are not printed to stdout or stderr.

```bash
MAIL_SERVICE_TOKEN=local-mail-service-token \
  scripts/validate-tenant-config.sh config/mailer/tenants.example.json
```

For deploy `infra/deploy/tenants.json`, run from a bash session that has loaded
the deploy `.env` values:

```bash
set -a
. infra/deploy/.env
set +a
scripts/validate-tenant-config.sh infra/deploy/tenants.json
```

The preflight checks the `tenants.schema.json` shape, duplicate `tenant_id`
values, empty or duplicate `source_services`, whether each `token_env` exists in
the environment, token values that look like placeholders, the ACS secret
requirement when the effective provider (`MAILER_PROVIDER` / `Mailer__Provider`
overrides included) is `acs` and `live_sending=true` (accepts
`ACS_CONNECTION_STRING_FILE` or bare `ACS_CONNECTION_STRING`), and the Mailpit
SMTP host / port configuration policy. This preflight targets the current shell
environment and does not read `appsettings*.json`.
Outside `Development` / `Testing`, Mailer runtime startup also fail-closes when
a tenant authentication token resolves to a known placeholder value, using the
same placeholder detection predicate as `setup doctor` and this preflight script.
Runtime enforcement is relaxed only in `Development` / `Testing`; `setup doctor`
may still report placeholders in those environments.
`Testing` is a privileged environment name for test hosts and must not be used
in production deployment.
See [#150](https://github.com/kooiei-in4a/amane-mailer/issues/150) for the
tenant config preflight validator background.

## Tenant / env troubleshooting

When tenant JSON and environment variables may be out of sync, first run
`scripts/validate-tenant-config.sh <tenants.json>` from the same shell and check
the same `MAILER_TENANTS_PATH`, `MAILER_PROVIDER` / `Mailer__Provider`,
`MAIL_SERVICE_TOKEN_*`, ACS secret (`ACS_CONNECTION_STRING_FILE` for
Staging/Production deploy, or `ACS_CONNECTION_STRING` for local/drill), and
Mailpit SMTP settings that Mailer will use at startup. Do not paste secret
values into docs or issues; use only placeholders such as `replace-with-*` or
`local-mail-service-token`.

| Symptom | Settings to check | Safe fix |
|---------|-------------------|----------|
| `401 UNAUTHORIZED_TENANT` | Request `tenant_id`, Bearer token, tenant JSON `tenant_id`, `token_env`, and whether that environment variable exists | Set the correct token in the environment variable for that tenant, and align the request `tenant_id` with the token. Do not write token values into JSON or logs. |
| `403 SOURCE_SERVICE_NOT_ALLOWED` | Request `source_service` and the tenant JSON `source_services` allowlist | Add the caller's official `source_service` name to the allowlist, or fix the request to use an already registered name. Check case and `-` / `_` differences. |
| `LIVE_SENDING_DISABLED` | Tenant JSON `provider`, `live_sending`, and the effective provider after `MAILER_PROVIDER` / `Mailer__Provider` overrides | Keep `live_sending=false` for local / staging by default. Only approved production senders should combine `provider=acs`, `live_sending=true`, and a registered ACS secret. |
| Provider configuration missing | Effective provider, ACS secret (`ACS_CONNECTION_STRING_FILE` or `ACS_CONNECTION_STRING`), Mailpit SMTP host / port | For Staging/Production deploy with `provider=acs` and `live_sending=true`, register the file secret with `admin provider register-acs` (see the register-acs runbook). Local ACS drill may still use bare `ACS_CONNECTION_STRING`. For Mailpit, verify the SMTP host / port are reachable from the container. |
| Nothing arrives in Mailpit | `MAILER_PROVIDER` / `Mailer__Provider`, tenant JSON `provider`, `live_sending`, Mailpit SMTP host / port, and whether the Worker is running | For local smoke, set `MAILER_PROVIDER=mailpit` explicitly and verify the Mailpit UI / API port is not confused with the SMTP port. Worker delivery can take a few seconds, so retry the check after a short wait. |
| Wrong tenant JSON path | `MAILER_TENANTS_PATH` / `Mailer:TenantsPath`, Docker mount path, startup logs, and the file path passed to preflight | Make the startup JSON path match the JSON file you preflighted. In deploy, mount the host-owned file read-only and point `MAILER_TENANTS_PATH` at the container path. |
| Unexpected provider from `MAILER_PROVIDER` override | `MAILER_PROVIDER`, `Mailer__Provider`, tenant JSON `provider`, and the effective provider reported by preflight | The override changes the provider for every tenant. After local smoke or ACS drills, remove stale overrides and confirm the effective provider matches the intended tenant JSON. |

`live_sending` is the fail-closed live-send gate. Use these defaults by
environment:

| Environment | Recommended policy |
|-------------|--------------------|
| local / test | Use `provider=mailpit` or `MAILER_PROVIDER=mailpit`, with `live_sending=false`. Verify delivery in Mailpit without sending real mail. |
| staging | Keep `live_sending=false` by default. If you briefly validate ACS connectivity or sender setup, limit it to a dedicated tenant, recipient, and runbook step. |
| production | Only approved ACS senders should use `provider=acs`, `live_sending=true`, and the file-based ACS secret registered via `admin provider register-acs`. Keep production and non-production tokens / `tenant_id` values separate. |

The shared deploy template (`tenants.shared.example.json`) contains three
tenants — `example-develop`, `example-staging`, `example-production` — each
with a distinct `token_env`. Copy this file, rename the tenants to match your
service, replace placeholder values, and mount it as `tenants.json` in the
deploy directory.

Use `live_sending=false` for local and test tenants. A tenant with
`provider=acs` and `live_sending=false` does not send live mail. Set the
effective provider to `acs`, `live_sending=true`, and a registered ACS secret
only for an approved live sender.
