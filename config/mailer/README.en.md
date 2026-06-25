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

`provider` normally comes from the tenant JSON. Setting `MAILER_PROVIDER` or
`Mailer:Provider` overrides the provider for every tenant.

Deployment-specific tenant files should be mounted into the container and
validated against `tenants.schema.json` before deployment. The Docker image only
includes safe examples and the schema.
Use `develop` for local verification files unless you intentionally add a new
environment value to the schema.

The shared deploy template (`tenants.shared.example.json`) contains three
tenants — `example-develop`, `example-staging`, `example-production` — each
with a distinct `token_env`. Copy this file, rename the tenants to match your
service, replace placeholder values, and mount it as `tenants.json` in the
deploy directory.

Use `live_sending=false` for local and test tenants. A tenant with
`provider=acs` and `live_sending=false` does not send live mail. Set the
effective provider to `acs`, `live_sending=true`, and `ACS_CONNECTION_STRING`
only for an approved live sender.
