[日本語](vps-dogfood-deployment.md)

# VPS dogfood deployment (PR1)

This runbook is the Issue #733 PR1 reference deployment. Caddy owns the host's
80/443 listeners, while Mailer runs as an HTTP backend on a Docker network.
Mailer port 8080 is never published on the host.

PR1 covers the deployment security boundary and the fresh setup route. It does
not include ACS live sending, official smoke clients, multi-sender/API-key
dogfood, revoke, restart dogfood, or backup/restore work.

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

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` is not the operator's client IP. It allows
the Mailer server-side `Connection.LocalIpAddress` for requests arriving from
the proxy (the Mailer-side address on this profile). The operator CIDR is a
separate Caddy `remote_ip` restriction.

## Prepare the host

Provide the Docker Engine and a Compose plugin that supports `!override`, public
DNS, and host firewall policy first. Mailer does not install Docker or configure
firewall, DNS, or a TLS account.

From `infra/deploy`:

```bash
cp .env.example .env
cp Caddyfile.vps-dogfood.example Caddyfile.vps-dogfood
```

Replace all placeholders in `.env`. At minimum, verify:

- `MAILER_IMAGE_REPOSITORY` and `MAILER_IMAGE_TAG` identify a verified
  published Mailer image.
- `MAILER_TENANTS_HOST_PATH` points to the real tenant JSON; token values stay
  only in the private `.env`.
- `MAILER_METRICS_BEARER_TOKEN` is a private production value.
- `MAILER_PUBLIC_HOSTNAME` is the actual DNS name.
- `MAILER_MANAGEMENT_ALLOWED_CIDRS` is the operator source IP/CIDR selected by
  the VPN/firewall boundary. The `192.0.2.0/24` in `.env.example` is TEST-NET
  documentation space and must be replaced. Separate multiple values with
  spaces, for example `"192.0.2.0/24 2001:db8:1234::/48"`.
- `MAILER_VPS_PROXY_NETWORK_SUBNET` and the fixed IPv4 values do not conflict
  with an existing host network. If they change, keep the subnet and both
  fixed addresses consistent.

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
  --profile vps-dogfood up -d
```

If `config --quiet` fails, check placeholders, the required hostname/CIDR, and
the fixed network addresses. After startup:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

curl -fsS https://MAILER_PUBLIC_HOSTNAME/healthz
curl -i https://MAILER_PUBLIC_HOSTNAME/readyz
```

On fresh state, `/readyz` remains `503` (uninitialized) after migration. That
is the expected pre-setup state. Direct `http://host:8080` access to Mailer
must fail.

## Browser Setup

Display the bootstrap token from inside the container once, and treat it like
the password, ACS secret, and API tokens. Do not put it in shell history, logs,
issues, or chat.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

Open `https://MAILER_PUBLIC_HOSTNAME/setup` from an operator network allowed by
Caddy, then follow the existing FirstRunSetup order: bootstrap authentication,
provider, instance owner, sender, and finalize. `/setup` requires HTTPS. Caddy's
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
  `compose.yml` for manual/fresh compatibility. After managed v2 initialization,
  SQLite is authoritative for provider, Admin, Sender, and API-key state. Do not
  maintain two competing v2 sources of truth; legacy cleanup is outside PR1.
- The `caddy_data` / `caddy_config` named volumes and Mailer's data volume are
  persistent deployment state. Full backup/restore documentation changes are
  PR3 work; this profile does not provide volume-deleting commands.

## Stop

Stop without deleting data:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood down
```

Do not use `down -v` from this PR1 runbook: it can delete the Mailer database
and Caddy certificate state.
