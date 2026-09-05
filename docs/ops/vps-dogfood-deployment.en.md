[日本語](vps-dogfood-deployment.md)

# VPS dogfood deployment (PR1)

This runbook is the Issue #733 PR1 reference deployment. Caddy owns the host's
80/443 listeners, while Mailer runs as an HTTP backend on a Docker network.
Mailer port 8080 is never published on the host.

This document's PR1 scope is the deployment security boundary and the fresh
setup route. ACS live sending, official smoke clients, multi-sender/API-key
dogfood, revoke, and restart dogfood are separate verification scopes. The PR3
full backup/restore path is now provided by the dedicated runbooks and helpers
below; it does not mix Caddy state into the Mailer archive.

## Topology

```text
Internet / operator
        │ HTTPS :443 (Caddy automatic HTTPS)
        ▼
proxy (Caddy, host :80/:443 only)
        │ vps_proxy network
        ▼
mailer:8080 (no host port)
```

`compose.vps-dogfood.yml` overlays the base `mailer` service as follows:

- `mailer` joins only `internal` and the dedicated `vps_proxy` network. The
  base consumer `mailer` network is replaced for this profile, so it does not
  leave a direct proxy-bypass path.
- `proxy` and `mailer` use fixed IPv4 addresses on the dedicated network.
  Mailer trusts forwarded headers from the one fixed proxy IPv4 only; it does
  not trust the whole Docker network or `0.0.0.0/0`.
- The dedicated network is intentionally not Docker `internal: true`: Mailer
  needs outbound ACS access and Caddy needs outbound ACME access. Only `proxy`
  and `mailer` join it, and only `proxy` publishes host ports.
- Caddy admits `/admin`, `/setup`, and `/metrics` only from the client source
  IP/CIDRs in `MAILER_MANAGEMENT_ALLOWED_CIDRS`. Other management requests get
  an edge 404. Only `/api/*`, `/healthz`, and `/readyz` are public proxy paths.
- The legacy tenant JSON bind and `MAILER_TENANTS_PATH`, `MAIL_SERVICE_TOKEN*`,
  and `MAILER_PROVIDER` kept by the base compose are removed from the effective
  `mailer` and `mailer-migrate` services by this overlay's Compose merge
  (`!override` / `!reset`). The VPS managed-v2 migration and first-run path does
  not require tenant JSON, tenant tokens, or a v1 provider setting.

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` is not the operator's client IP. It allows
the Mailer server-side `Connection.LocalIpAddress` for requests arriving from
the proxy (the Mailer-side address on this profile). The operator CIDR is a
separate Caddy `remote_ip` restriction.

## Prepare the host

Provide the Docker Engine and a Compose plugin that supports `!override` and `!reset`, public
DNS, and host firewall policy first. Mailer does not install Docker or configure
firewall, DNS, or a TLS account.

From `infra/deploy`:

```bash
cp .env.vps-dogfood.example .env
cp Caddyfile.vps-dogfood.example Caddyfile.vps-dogfood
```

Replace the VPS placeholders in `.env`. At minimum, verify:

- `MAILER_IMAGE_REPOSITORY` and `MAILER_IMAGE_TAG` identify a verified
  published Mailer image.
- `MAILER_DATA_PATH` is the persistent directory for managed SQLite state.
- `./secrets/acs` and `./secrets/bounce-queue` are protected mode-0700
  directories. They are read-only compatibility/manual-registration mounts.
  Browser setup stores the managed-v2 provider authority at
  `MAILER_DATA_PATH/secrets/acs` (container: `/app/data/secrets/acs`). Register
  the ACS provider secret through the approved file-based flow; do not put it in
  `.env` or in a tenant token variable. If metrics are enabled, add a private
  `MAILER_METRICS_BEARER_TOKEN` only to the private host `.env`.
- `MAILER_PUBLIC_HOSTNAME` is the actual DNS name.
- `MAILER_MANAGEMENT_ALLOWED_CIDRS` is the operator source IP/CIDR selected by
  the VPN/firewall boundary. The `192.0.2.0/24` in `.env.example` is TEST-NET
  documentation space and must be replaced. Separate multiple values with
  spaces, for example `"192.0.2.0/24 2001:db8:1234::/48"`.
- `MAILER_VPS_PROXY_NETWORK_SUBNET` and the fixed IPv4 values do not conflict
  with an existing host network. If they change, keep the subnet and both
  fixed addresses consistent.
- Do not set `MAILER_TENANTS_HOST_PATH`, `MAILER_TENANTS_CONTAINER_PATH`,
  `MAIL_SERVICE_TOKEN`, `MAIL_SERVICE_TOKEN_DEVELOP`,
  `MAIL_SERVICE_TOKEN_STAGING`, `MAIL_SERVICE_TOKEN_PRODUCTION`, or
  `MAILER_PROVIDER`. Do not create a `tenants.json` for a fresh VPS.

### VPS managed-v2 first-run authority

The reference path has this contract:

- `SQLite managed state` = product configuration authority for provider,
  instance owner, sender, and API-key state.
- `provider secret` = protected file. The browser-setup canonical path is
  `MAILER_DATA_PATH/secrets/acs/acs_connection_string` (container:
  `/app/data/secrets/acs/acs_connection_string`). ACS secrets are handled only
  through the file-based registration flow and its protected setup path.
- `bootstrap token` = transient protected file. Display it once, protect it like
  a password/provider secret, and remove the file safely when it is no longer needed.
- `tenants.json` / `MAIL_SERVICE_TOKEN*` = legacy/manual path. They are not needed
  by the VPS v2 reference deployment and are not the active product configuration
  source of truth for first-run setup.

The common `infra/deploy/.env.example` serves the base compose manual/compatibility
path. Use `.env.vps-dogfood.example` for VPS, so its legacy placeholders do not
need to be configured.

For SSH-tunnel-only access, bind Caddy's host ports to `127.0.0.1` and do not
publish the remote host's 80/443. This makes the public API tunnel-only too.
For a public API with private management, operate public 80/443 through the
firewall and verify the management restriction both in Caddy and at the
VPN/host boundary.

## Validate and start Compose

Select the profile explicitly:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d
```

If `config --quiet` fails, check placeholders, the required hostname/CIDR, and
the fixed network addresses. Also verify that the rendered `mailer` and
`mailer-migrate` services contain no tenant JSON mount,
`MAILER_TENANTS_PATH`, `MAIL_SERVICE_TOKEN*`, or `MAILER_PROVIDER`. After startup:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

curl -fsS https://MAILER_PUBLIC_HOSTNAME/healthz
curl -i https://MAILER_PUBLIC_HOSTNAME/readyz
```

With fresh state and no tenant JSON or `MAIL_SERVICE_TOKEN*`, migration still
succeeds and `/readyz` remains `503` (uninitialized). That is the expected
pre-setup state. `/setup` is reachable from the approved management CIDR, and
direct `http://host:8080` access to Mailer must fail.

## Browser Setup

Display the bootstrap token from inside the container once. It is the value of a
transient protected file; treat it like the password and provider secret, not as
a tenant token. Do not put it in shell history, logs, issues, or chat.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

Open `https://MAILER_PUBLIC_HOSTNAME/setup` from an operator network allowed by
Caddy, then follow the existing FirstRunSetup order: bootstrap authentication,
file-based provider-secret registration, instance owner, sender, and finalize. `/setup` requires HTTPS. Caddy's
`X-Forwarded-Proto` is trusted only from the dedicated proxy IP, preserving the
Secure-cookie and antiforgery HTTPS contract.

After finalize, `initialized_at` is a one-way gate. Restart Mailer and verify
that `/readyz` becomes ready. An initialized runtime no longer maps `/setup`,
so a stale bootstrap token file cannot reopen it. Use `/admin` through the same
management route.

## Operational boundaries

- Public consumer requests use `https://MAILER_PUBLIC_HOSTNAME/api/...`. The
  backend Docker name/port is not the consumer's public contract.
- Combine the Caddy CIDR restriction for `/admin` and `/setup` with a
  VPN/firewall/SSH tunnel and instance-owner authentication. This profile does
  not make a public Admin safe through the Mailer application alone.
- `/metrics` is also a management path. A metrics bearer token does not replace
  the edge restriction.
- `MAILER_TENANTS_PATH`, `MAIL_SERVICE_TOKEN_*`, and `MAILER_PROVIDER` remain in
  `infra/deploy/compose.yml` for the baseline manual/v1 compatibility path, but
  `compose.vps-dogfood.yml` removes them from both services. In VPS managed-v2,
  SQLite is the product configuration authority, the provider secret is a
  protected file, and the bootstrap token is a transient protected file.
  `tenants.json` / `MAIL_SERVICE_TOKEN*` are not needed by the VPS v2 reference
  deployment.
- The `caddy_data` / `caddy_config` named volumes and Mailer's data volume are
  persistent deployment state. Mailer's full instance backup takes
  `MAILER_DATA_PATH/mailer.db`, the canonical provider secret, and
  `attachment-spool/committed` at one stopped point. Caddy volumes, bootstrap
  tokens, logs, staging, and the external `/run/secrets/acs` compatibility mount
  are not mixed into the archive. See
  [backup-operations](backup-operations.en.md),
  [restore-procedure](restore-procedure.en.md), and
  [restore-verification](restore-verification.en.md) for the PR3 path.

## Stop

Stop without deleting data:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood down
```

Do not use `down -v` from this PR1 runbook: it can delete the Mailer database
and Caddy certificate state.
