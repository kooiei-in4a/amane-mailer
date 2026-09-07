[日本語](vps-dogfood-deployment.md)

# VPS dogfood deployment (Issue #744 Source Stage)

This runbook is the Issue #744 source-stage reference deployment. Caddy owns the host's
80/443 listeners, while Mailer runs as an HTTP backend on a Docker network.
Mailer port 8080 is never published on the host.

This document's scope is the deployment security boundary and the fresh
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

The edge path contract is:

| Path | Caddy edge boundary | Authentication sent to Mailer |
|---|---|---|
| `/api/*`, `/healthz`, `/readyz` | public | Existing path-specific authentication |
| `/admin`, `/admin/*`, `/setup`, `/setup/*` | GeoLite2 JP CIDR **and** Caddy Basic Auth | Caddy `Authorization` is removed; Mailer's own Admin/Setup authentication remains |
| `/metrics` | `MAILER_MANAGEMENT_ALLOWED_CIDRS` operator CIDR | Existing Mailer metrics bearer |
| Everything else | 404 | Not sent upstream |

For Admin/Setup, a source outside the JP CIDRs receives an edge 404 before Caddy can issue a
Basic Auth challenge. Only a JP source that passes Caddy Basic Auth is reverse-proxied to Mailer.

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
- Caddy requires `/admin` and `/setup` to match the JP IPv4/IPv6 CIDRs derived by
  the renderer from GeoLite2 Country CSVs and to pass Caddy Basic Auth. Non-JP
  requests get a 404 before the challenge, and the successful Caddy Basic
  credential is removed with `header_up -Authorization` before Mailer receives it.
- `/metrics` keeps the existing `MAILER_MANAGEMENT_ALLOWED_CIDRS` operator
  restriction and Mailer metrics bearer. The JP list must not be reused to expose
  metrics to all of Japan.
- Only `/api/*`, `/healthz`, and `/readyz` are public proxy paths; everything else
  gets a 404.
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

From `infra/deploy`, create `.env`. Do not simply copy the Caddyfile: render it
from GeoLite2 and the hash input.

```bash
cp .env.vps-dogfood.example .env
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
- `MAILER_MANAGEMENT_ALLOWED_CIDRS` is the `/metrics` operator source IP/CIDR
  selected by the VPN/firewall boundary. The `192.0.2.0/24` in `.env.example` is
  TEST-NET documentation space and must be replaced. Separate multiple values
  with spaces, for example `"192.0.2.0/24 2001:db8:1234::/48"`.
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

### Generate the Caddy edge artifact

The operator obtains and stores the GeoLite2 Country CSVs separately. The renderer
does not connect to MaxMind, download data, or handle a license key/account ID. Pass
the three CSVs and an already-generated bcrypt hash file from a separate secure
provisioning path. Generating the real password or hash is outside this source stage.

```bash
python3 render-vps-management-edge.py \
  --ipv4-blocks /secure/geolite/GeoLite2-Country-Blocks-IPv4.csv \
  --ipv6-blocks /secure/geolite/GeoLite2-Country-Blocks-IPv6.csv \
  --locations /secure/geolite/GeoLite2-Country-Locations-en.csv \
  --basic-auth-username caddy-admin \
  --basic-auth-hash-file /secure/operator-secrets/caddy-admin.bcrypt \
  --template Caddyfile.vps-dogfood.example \
  --output Caddyfile.vps-dogfood
```

`caddy-admin` is a non-secret example. Choose and pass a deployment-specific username;
the renderer rejects empty, whitespace, newline, and Caddyfile-token-injection values.

`Caddyfile.vps-dogfood` is an ignored runtime artifact. The renderer uses only
`network.geoname_id → locations.geoname_id → country_iso_code == JP`; it never
falls back to `registered_country_geoname_id` or `represented_country_geoname_id`.
Empty/unknown network geonames are not JP. Zero JP CIDRs, invalid/default CIDRs,
missing required columns, conflicting mappings, and an empty/invalid hash stop the
render fail-closed. Raw GeoLite CSV data is not copied to the output, and the hash
value is not printed in logs or the summary.

### Caddy Basic Auth credential boundary

For live operations, generate a new, sufficiently strong random Caddy Basic Auth password
with an approved password manager or CSPRNG. This rework does not generate a real password
or real hash. The Caddy Basic Auth password is a separate credential from the Mailer Admin
password and from the Setup bootstrap token; never reuse any of them. It is also unrelated
to MaxMind / GeoLite download credentials, account IDs, or license keys.

- For the Basic Auth credential material, only the bcrypt hash belongs in the `Caddyfile`. Never
  store the plaintext password in the repository, production `Caddyfile`, `.env`, an issue, or a log.
- Give the renderer the already-generated hash only, using `--basic-auth-hash-file`; do not
  give it a plaintext password or a password generator. Do not print the hash in logs or evidence.
- Handle the live password and bcrypt hash separately through the operator's approved secret
  path. This rework does not generate or record a real credential.

### Safe live-edge apply lifecycle (production runbook)

This section is a production live-edge runbook. The VPS commands below are documentation only
and are **not executed during this rework**. Source Stage build/validation approval does not
authorize live mutation. Before changing production, obtain a separate Human approval recorded
with the change ID, hostname, candidate digest, executor, and rollback owner.

1. **Generate a candidate from real GeoLite input.** From the reviewed source checkout root, use
   the operator-secured current GeoLite2 Country IPv4 blocks, IPv6 blocks, locations-en CSV, and
   an already-generated bcrypt hash file—not the self-test fixture. Do not change the production
   `/srv/platform/edge/Caddyfile` at this stage.

   ```bash
   set -Eeuo pipefail
   umask 077
   edge_dir=/srv/platform/edge
   candidate="${edge_dir}/Caddyfile.candidate"
   change_id="$(date -u +%Y%m%dT%H%M%SZ)"
   render_record="${edge_dir}/change-records/${change_id}-caddy-render.txt"
   mkdir -p "${edge_dir}/change-records"

   if ! python3 infra/deploy/render-vps-management-edge.py \
     --ipv4-blocks /secure/geolite/current/GeoLite2-Country-Blocks-IPv4.csv \
     --ipv6-blocks /secure/geolite/current/GeoLite2-Country-Blocks-IPv6.csv \
     --locations /secure/geolite/current/GeoLite2-Country-Locations-en.csv \
     --basic-auth-username caddy-admin \
     --basic-auth-hash-file /secure/operator-secrets/caddy-admin.bcrypt \
     --template infra/deploy/Caddyfile.vps-dogfood.example \
     --output "${candidate}" >"${render_record}"; then
     echo 'STOP: GeoLite render failed; production Caddyfile was not changed.' >&2
     exit 1
   fi
   ```

   `caddy-admin` is a non-secret example. Use a deployment-specific username in live operations.
   Keep the render summary as a protected value-free change record; it must not contain a hash or password.

2. **Record candidate counts, size, and digest.** Reconcile the renderer summary with the candidate
   and record `IPv4 CIDR count`, `IPv6 CIDR count`, `bytes`, and `SHA-256` in the change record.
   The following additional record checks the candidate on disk without displaying a secret value.

   ```bash
   {
     grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "${render_record}"
     printf 'candidate bytes: %s\n' "$(stat -c '%s' "${candidate}")"
     printf 'candidate SHA-256: %s\n' "$(sha256sum "${candidate}" | awk '{print $1}')"
   } >>"${render_record}"
   ```

   A zero count, unexpected count, mismatched bytes/SHA-256, or inability to record the summary is
   STOP. Do not copy raw GeoLite CSV data, the password, or the bcrypt hash into the change record.

3. **Validate with pinned/running Caddy 2.10.2 before changing production.** Record the running
   `proxy` version and image digest and confirm that they match the repository pin. Keep the image
   pinned; do not substitute digest-less `caddy:2.10.2` or `latest`.

   ```bash
   caddy_image='caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d'
   running_image="$(docker inspect --format '{{.Config.Image}}' proxy)"
   test "${running_image}" = "${caddy_image}"
   docker exec proxy caddy version

   if ! docker run --rm --pull=never \
     --env MAILER_PUBLIC_HOSTNAME=mailer.example.invalid \
     --env MAILER_MANAGEMENT_ALLOWED_CIDRS=192.0.2.0/24 \
     --mount "type=bind,src=${candidate},dst=/etc/caddy/Caddyfile,readonly" \
     "${caddy_image}" \
     caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile; then
     echo 'STOP: candidate Caddyfile validation failed; production Caddyfile was not changed.' >&2
     exit 1
   fi
   ```

   The hostname and metrics CIDR above are documentation placeholders. Pass the production values
   from operator configuration without printing them. A validation failure is always STOP: do not
   replace production with the candidate and do not fall back to an allow-all CIDR.

4. **Perform live mutation only after separate Human approval.** Source Stage approval, the renderer
   self-test, CI pass, and a successful step-3 validation are not the live-mutation Human approval.
   STOP if the approval is not recorded, the candidate digest changed, or the SSH rollback owner is absent.

5. **Save current as timestamped/SHA-256 last-known-good.** After approval, verify that
   `/srv/platform/edge/Caddyfile` is not a symlink, is owned by `root:root`, and has this profile's
   expected mode `0600`. Do not repair a different owner/mode in place and continue; stop for review.
   Compute current's SHA-256 and save it in a same-filesystem directory named
   `Caddyfile.<UTC timestamp>.sha256-<64 hex>`. Record that the backup hash matches current.

   ```bash
   current="${edge_dir}/Caddyfile"
   test -f "${current}" && test ! -L "${current}"
   test "$(stat -c '%U:%G' "${current}")" = 'root:root'
   expected_mode="$(stat -c '%a' "${current}")"
   test "${expected_mode}" = '600'
   current_sha256="$(sha256sum "${current}" | awk '{print $1}')"
   last_known_good_dir="${edge_dir}/last-known-good"
   last_known_good="${last_known_good_dir}/Caddyfile.${change_id}.sha256-${current_sha256}"
   mkdir -p "${last_known_good_dir}"
   install -o root -g root -m "${expected_mode}" \
     "${current}" "${last_known_good}"
   test "$(sha256sum "${last_known_good}" | awk '{print $1}')" = "${current_sha256}"
   ```

6. **Atomic-replace the candidate.** Confirm that candidate and current are on the same filesystem
   and that candidate is also `root:root` / expected mode `0600`. Set candidate ownership/mode before
   the replace, then rename within the same directory and verify afterward.

   ```bash
   test -f "${candidate}" && test ! -L "${candidate}"
   chown root:root "${candidate}"
   chmod "${expected_mode}" "${candidate}"
   test "$(stat -c '%d' "${candidate}")" = "$(stat -c '%d' "${current}")"
   mv -f -- "${candidate}" "${current}"
   test "$(stat -c '%U:%G %a' "${current}")" = 'root:root 600'
   ```

   Do not copy across directories, edit, truncate, or temporarily replace the file with an allow-all
   configuration. If `mv` fails, STOP and leave current unchanged.

7. **Use `caddy reload`, not a container restart/recreate.** After replacing the mounted current path,
   run only the Caddy reload below. Do not use `docker compose restart`, `up -d --force-recreate`, or
   container stop/start in this apply lifecycle.

   ```bash
   docker exec proxy caddy reload \
     --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

8. **Run all post-reload acceptance checks.** Record each result in the existing value-free acceptance
   record. From a JP source, `/admin` and `/setup` must cover the Caddy Basic Auth fail/success boundary
   and Mailer's own authentication boundary; a non-JP source must receive a 404 before the challenge.
   The `/api` regression must be an approved no-send regression check.

   - `/healthz`
   - `/readyz`
   - `/api` regression
   - `/admin`
   - `/setup`
   - public host `:8080` is unreachable (the Mailer backend port is not published)
   - SSH login / rollback session

9. **On failure, restore old Caddyfile → validate → reload → regression verification.** For a reload
   failure, acceptance failure, JP allow-list misgeneration, or ownership/mode drift, do not skip steps
   or edit around the failure. Restore last-known-good, validate it with the same pinned Caddy 2.10.2,
   then `caddy reload`, then repeat every step-8 acceptance check and the `/api` regression. If restore,
   validation, reload, or regression verification fails, keep SSH available, STOP, and escalate.

   Restore the current path without editing it in place: put last-known-good in a same-directory
   temporary file with root ownership / expected mode, then atomically rename it.

   ```bash
   rollback_candidate="${edge_dir}/Caddyfile.rollback.${change_id}"
   install -o root -g root -m "${expected_mode}" \
     "${last_known_good}" "${rollback_candidate}"
   mv -f -- "${rollback_candidate}" "${current}"
   test "$(stat -c '%U:%G %a' "${current}")" = 'root:root 600'

   docker run --rm --pull=never \
     --env MAILER_PUBLIC_HOSTNAME=mailer.example.invalid \
     --env MAILER_MANAGEMENT_ALLOWED_CIDRS=192.0.2.0/24 \
     --mount "type=bind,src=${current},dst=/etc/caddy/Caddyfile,readonly" \
     "${caddy_image}" \
     caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
   docker exec proxy caddy reload \
     --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

   If an incorrectly generated JP allow-list rejects every request from Japan, do not attempt to fix it
   through the public path. Use the pre-established SSH session to restore last-known-good, validate,
   reload, and verify the regression. Do not begin an apply until SSH rollback has been confirmed.

10. **Fail closed on GeoLite download/render/validate/update failure.** Keep the current last-known-good
    production Caddyfile in place. Never fall back to an empty candidate, empty CIDRs, default routes, or
    allow-all (`0.0.0.0/0` / `::/0`). Record the failure and STOP.

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

If `config --quiet` fails, check placeholders, the required hostname, the metrics
CIDR, the fixed network addresses, and the generated Caddyfile. Also verify that the rendered `mailer` and
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
pre-setup state. From an approved JP source, `/setup` requires Caddy Basic Auth
and Mailer's own authentication; a non-JP source gets a 404 before the challenge.
`/metrics` separately requires the operator CIDR and metrics bearer, and direct
`http://host:8080` access to Mailer must fail.

## Browser Setup

Display the bootstrap token from inside the container once. It is the value of a
transient protected file; treat it like the password and provider secret, not as
a tenant token. Do not put it in shell history, logs, issues, or chat.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

Open `https://MAILER_PUBLIC_HOSTNAME/setup` from a source covered by the generated
JP CIDRs. Pass Caddy Basic Auth first, then follow the existing FirstRunSetup order:
Mailer bootstrap authentication, file-based provider-secret registration, instance owner, sender, and finalize. `/setup` requires HTTPS. Caddy's
`X-Forwarded-Proto` is trusted only from the dedicated proxy IP, preserving the
Secure-cookie and antiforgery HTTPS contract.

After finalize, `initialized_at` is a one-way gate. Restart Mailer and verify
that `/readyz` becomes ready. An initialized runtime no longer maps `/setup`,
so a stale bootstrap token file cannot reopen it. Use `/admin` through the same
management route.

## Operational boundaries

- Public consumer requests use `https://MAILER_PUBLIC_HOSTNAME/api/...`. The
  backend Docker name/port is not the consumer's public contract.
- `/admin` and `/setup` require both a GeoLite2-derived JP CIDR and Caddy Basic
  Auth. Non-JP sources get a 404 before the Basic challenge. Combine this with a
  VPN/firewall/SSH tunnel and instance-owner authentication; this profile does not
  make a public Admin safe through the Mailer application alone.
- `/metrics` uses the operator boundary in `MAILER_MANAGEMENT_ALLOWED_CIDRS`, not
  the Japan CIDR list, and still requires the Mailer metrics bearer.
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

Do not use `down -v` from this source-stage runbook: it can delete the Mailer database
and Caddy certificate state.
