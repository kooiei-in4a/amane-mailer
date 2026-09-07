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

Generate the real GeoLite2 Country candidate only on the operator side or on
`agent-dev01`. The renderer does not connect to MaxMind, download data, or handle
a license key/account ID. Keep the following inputs only in a secure operator /
`agent-dev01` input path; do not place them on the VPS.

- GeoLite raw CSVs (IPv4 blocks, IPv6 blocks, and locations-en)
- MaxMind account ID / license key
- bcrypt input hash file
- plaintext Basic Auth password

The only items that may be transferred to the VPS are a generated Caddy candidate
after separate approval and value-free IPv4/IPv6 CIDR counts, bytes, and SHA-256
metadata. Generating the real password or hash is outside this source stage. The
`operator_input_dir` and `caddy_hash_file` below are paths on the operator /
`agent-dev01`, never `/srv/platform/edge`.

```bash
operator_input_dir='/path/on/operator-or-agent-dev01/geolite/current'
caddy_hash_file='/path/on/operator-or-agent-dev01/caddy-admin.bcrypt'

python3 render-vps-management-edge.py \
  --ipv4-blocks "${operator_input_dir}/GeoLite2-Country-Blocks-IPv4.csv" \
  --ipv6-blocks "${operator_input_dir}/GeoLite2-Country-Blocks-IPv6.csv" \
  --locations "${operator_input_dir}/GeoLite2-Country-Locations-en.csv" \
  --basic-auth-username caddy-admin \
  --basic-auth-hash-file "${caddy_hash_file}" \
  --template Caddyfile.vps-dogfood.example \
  --output ./Caddyfile.vps-dogfood.candidate
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

Fresh-confirmed actual VPS topology is:

| item | source of truth |
| --- | --- |
| Compose project / service | `amane-platform-edge` / `proxy` |
| edge working directory / compose | `/srv/platform/edge` / `/srv/platform/edge/compose.yml` |
| current Caddyfile | `/srv/platform/edge/Caddyfile` |
| observed running container name | `amane-platform-edge-proxy-1` (reference only; resolve by labels) |
| current ownership / mode baseline | `root:root` / `0644` (Fresh-confirmed; re-read and preserve live) |
| container path / mount | `/etc/caddy/Caddyfile`, single-file read-only bind mount |
| mount source / destination | `/srv/platform/edge/Caddyfile` → `/etc/caddy/Caddyfile`, RW=false |
| pinned image | `caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d` |

1. **Generate a candidate from real GeoLite input on the operator side.** From the reviewed source
   checkout root and the operator / `agent-dev01` secure input path, use current GeoLite2 Country
   IPv4 blocks, IPv6 blocks, locations-en CSV, and an already-generated bcrypt hash file—not the
   self-test fixture. Never place raw CSV, MaxMind credentials, the bcrypt input hash file, or the
   plaintext password on the VPS. Do not change `/srv/platform/edge/Caddyfile` at this stage.

   ```bash
   set -Eeuo pipefail
   umask 077
   operator_input_dir='/path/on/operator-or-agent-dev01/geolite/current'
   caddy_hash_file='/path/on/operator-or-agent-dev01/caddy-admin.bcrypt'
   candidate="${PWD}/infra/deploy/Caddyfile.vps-dogfood"
   change_id="$(date -u +%Y%m%dT%H%M%SZ)"
   render_record="${PWD}/${change_id}-caddy-render.txt"

   if ! python3 infra/deploy/render-vps-management-edge.py \
     --ipv4-blocks "${operator_input_dir}/GeoLite2-Country-Blocks-IPv4.csv" \
     --ipv6-blocks "${operator_input_dir}/GeoLite2-Country-Blocks-IPv6.csv" \
     --locations "${operator_input_dir}/GeoLite2-Country-Locations-en.csv" \
     --basic-auth-username caddy-admin \
     --basic-auth-hash-file "${caddy_hash_file}" \
     --template infra/deploy/Caddyfile.vps-dogfood.example \
     --output "${candidate}" >"${render_record}"; then
     echo 'STOP: GeoLite render failed; production Caddyfile was not changed.' >&2
     exit 1
   fi
   test -f "${candidate}" && test ! -L "${candidate}"
   ```

   `caddy-admin` is a non-secret example. Use a deployment-specific username in live operations.
   Keep the render summary as a protected, value-free operator-side record; it must not contain a
   hash or password.

2. **Record candidate counts, size, and digest on the operator side.** Reconcile the renderer summary
   with the candidate and record `IPv4 CIDR count`, `IPv6 CIDR count`, `bytes`, and `SHA-256` in the
   change record. A zero count, unexpected count, mismatched bytes/SHA-256, or inability to record the
   summary is STOP. Do not copy raw GeoLite CSV data, the password, or the bcrypt hash into the record.

   ```bash
   candidate_bytes="$(stat -c '%s' "${candidate}")"
   candidate_sha256="$(sha256sum "${candidate}" | awk '{print $1}')"
   {
     grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "${render_record}"
     printf 'candidate bytes: %s\n' "${candidate_bytes}"
     printf 'candidate SHA-256: %s\n' "${candidate_sha256}"
   } >>"${render_record}"
   ```

3. **Validate with the pinned/running Caddy 2.10.2 before changing production.** Resolve the running
   edge container from Compose labels. The candidate is still local to the operator; send it to the
   actual Caddy binary over stdin. Zero or multiple matching running containers is STOP.

   ```bash
   edge_project='amane-platform-edge'
   edge_service='proxy'
   edge_workdir='/srv/platform/edge'
   current="${edge_workdir}/Caddyfile"
   caddy_container_path='/etc/caddy/Caddyfile'
   caddy_image='caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d'

   mapfile -t edge_container_ids < <(
     docker ps --quiet \
       --filter "label=com.docker.compose.project=${edge_project}" \
       --filter "label=com.docker.compose.service=${edge_service}" \
       --filter status=running
   )
   if (( ${#edge_container_ids[@]} != 1 )); then
     echo 'STOP: expected exactly one running edge container.' >&2
     exit 1
   fi
   edge_container="${edge_container_ids[0]}"

   test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${edge_container}")" = "${edge_project}"
   test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "${edge_container}")" = "${edge_service}"
   running_image="$(docker inspect --format '{{.Config.Image}}' "${edge_container}")"
   test "${running_image}" = "${caddy_image}"
   mount_record="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/etc/caddy/Caddyfile"}}{{.Type}}|{{.Source}}|{{.Destination}}|{{.RW}}{{end}}{{end}}' "${edge_container}")"
   test "${mount_record}" = "bind|/srv/platform/edge/Caddyfile|/etc/caddy/Caddyfile|false"
   running_version="$(docker exec "${edge_container}" caddy version)"
   case "${running_version}" in
     *v2.10.2*) ;;
     *) echo 'STOP: running Caddy is not 2.10.2.' >&2; exit 1 ;;
   esac

   # Resolve/inspect on the VPS SSH session; stream only candidate bytes from the operator via stdin.
   if ! ssh vps docker exec -i "${edge_container}" \
     caddy validate --config - --adapter caddyfile <"${candidate}"; then
     echo 'STOP: actual running Caddy rejected candidate; production Caddyfile was not changed.' >&2
     exit 1
   fi
   ```

   `caddy validate --config -` failure is always STOP: do not transfer the candidate, mutate
   production, or fall back to an allow-all CIDR. This stdin validation uses the actual running
   binary without placing GeoLite CSVs or the bcrypt input hash file on the VPS.

4. **Perform live mutation only after separate Human approval.** Source Stage approval, renderer
   self-test, CI pass, and the step-3 actual Caddy validation PASS are not the live-mutation Human
   approval. Record a separate approval after confirming the candidate count, bytes, SHA-256, target
   hostname, executor, and SSH rollback owner. STOP if approval is absent, the digest changed, or the
   rollback owner / SSH session is absent.

5. **Fresh-read current metadata and save a timestamped/SHA-256 last-known-good.** After approval,
   verify that `/srv/platform/edge/Caddyfile` is a regular non-symlink and matches the mount source.
   The Fresh-confirmed baseline is `root:root` / `0644`, but this is an observed baseline, not an
   instruction to reset the mode. The live preflight obtains and records owner, group, mode, inode,
   device, SHA-256, and bytes, then preserves those values unchanged. If they differ, STOP for a
   separate Human approval; do not mix a mode change into the #744 live edge apply.

   ```bash
   current='/srv/platform/edge/Caddyfile'
   test -f "${current}" && test ! -L "${current}"
   test "$(stat -c '%F' "${current}")" = 'regular file'
   test "${mount_record}" = 'bind|/srv/platform/edge/Caddyfile|/etc/caddy/Caddyfile|false'

   original_device="$(stat -c '%d' "${current}")"
   original_inode="$(stat -c '%i' "${current}")"
   original_owner="$(stat -c '%U' "${current}")"
   original_group="$(stat -c '%G' "${current}")"
   original_uid="$(stat -c '%u' "${current}")"
   original_gid="$(stat -c '%g' "${current}")"
   original_mode="$(stat -c '%a' "${current}")"
   original_bytes="$(stat -c '%s' "${current}")"
   original_sha256="$(sha256sum "${current}" | awk '{print $1}')"
   printf 'current owner=%s group=%s mode=%s inode=%s device=%s bytes=%s SHA-256=%s\n' \
     "${original_owner}" "${original_group}" "${original_mode}" "${original_inode}" \
     "${original_device}" "${original_bytes}" "${original_sha256}"

   edge_dir='/srv/platform/edge'
   last_known_good_dir="${edge_dir}/last-known-good"
   last_known_good="${last_known_good_dir}/Caddyfile.${change_id}.sha256-${original_sha256}"
   mkdir -p "${last_known_good_dir}"
   install -o "${original_owner}" -g "${original_group}" -m "${original_mode}" \
     -- "${current}" "${last_known_good}"
   test "$(sha256sum "${last_known_good}" | awk '{print $1}')" = "${original_sha256}"
   printf 'last-known-good change_id=%s original_owner=%s original_group=%s original_mode=%s original_inode=%s original_device=%s original_SHA-256=%s\n' \
     "${change_id}" "${original_owner}" "${original_group}" "${original_mode}" \
     "${original_inode}" "${original_device}" "${original_sha256}" \
     >>"${edge_dir}/change-records/${change_id}-caddy-live.txt"
   ```

   The backup may have a different inode, but its recorded metadata is the original current owner,
   group, mode, inode, device, SHA-256, and bytes. `install` is used only to create the
   last-known-good backup; it is never used to replace the mounted current path.

6. **After Human approval, transfer only the generated Caddy candidate to protected staging.** From
   the operator side, transfer only to `/srv/platform/edge/staging/<change-id>/Caddyfile`. The staging
   directory is not public, Git-managed, or Compose configuration, and contains generated Caddy only.
   Do not transfer raw CSV, MaxMind credentials, the bcrypt input hash file, or the plaintext password.
   Compare staging size and SHA-256 with the operator record; mismatch is STOP.

   ```bash
   # Operator side; run only after Human approval.
   scp -- "${candidate}" \
     "vps:/srv/platform/edge/staging/${change_id}/Caddyfile"

   # VPS side; use the existing protected staging directory and inspect only the candidate.
   staging_candidate="/srv/platform/edge/staging/${change_id}/Caddyfile"
   test -f "${staging_candidate}" && test ! -L "${staging_candidate}"
   chown "${original_uid}:${original_gid}" "${staging_candidate}"
   chmod "${original_mode}" "${staging_candidate}"
   test "$(stat -c '%s' "${staging_candidate}")" = "${candidate_bytes}"
   test "$(sha256sum "${staging_candidate}" | awk '{print $1}')" = "${candidate_sha256}"
   ```

7. **Preserve the single-file bind mount inode and update current contents in-place.** Do not replace
   current with a different inode. Do not rely on a bare `cp candidate current`; read all candidate
   bytes, open current in write mode, guard original device / inode / owner / group / mode, truncate,
   write the exact bytes, flush, call `os.fsync()`, and close. If current device or inode changed, do
   not write and STOP.

   ```bash
   write_contents_in_place() {
     python3 - "$1" "$2" "$3" "$4" \
       "${original_device}" "${original_inode}" "${original_uid}" "${original_gid}" "${original_mode}" <<'PY'
   import hashlib
   import os
   import pathlib
   import stat
   import sys

   source = pathlib.Path(sys.argv[1])
   target = pathlib.Path(sys.argv[2])
   expected_sha256 = sys.argv[3]
   expected_bytes = int(sys.argv[4])
   expected_device = int(sys.argv[5])
   expected_inode = int(sys.argv[6])
   expected_uid = int(sys.argv[7])
   expected_gid = int(sys.argv[8])
   original_mode = int(sys.argv[9], 8)

   source_stat = source.lstat()
   if source.is_symlink() or not stat.S_ISREG(source_stat.st_mode):
       raise SystemExit("STOP: candidate is not a regular non-symlink file")
   data = source.read_bytes()
   if len(data) != expected_bytes:
       raise SystemExit("STOP: candidate byte count changed")
   if hashlib.sha256(data).hexdigest() != expected_sha256:
       raise SystemExit("STOP: candidate SHA-256 changed")

   flags = os.O_RDWR | getattr(os, "O_NOFOLLOW", 0)
   fd = os.open(target, flags)
   try:
       target_stat = os.fstat(fd)
       if not stat.S_ISREG(target_stat.st_mode):
           raise SystemExit("STOP: current is not a regular file")
       if (
           target_stat.st_dev != expected_device
           or target_stat.st_ino != expected_inode
           or target_stat.st_uid != expected_uid
           or target_stat.st_gid != expected_gid
           or stat.S_IMODE(target_stat.st_mode) != original_mode
       ):
           raise SystemExit("STOP: current device/inode/ownership/mode guard failed")
       with os.fdopen(fd, "r+b", buffering=0, closefd=False) as handle:
           handle.seek(0)
           handle.truncate(0)
           written = handle.write(data)
           if written != len(data):
               raise SystemExit("STOP: short write")
           handle.flush()
           os.fsync(handle.fileno())
   finally:
       os.close(fd)
   PY
   }

   write_contents_in_place "${staging_candidate}" "${current}" \
     "${candidate_sha256}" "${candidate_bytes}"

   test "$(stat -c '%d' "${current}")" = "${original_device}"
   test "$(stat -c '%i' "${current}")" = "${original_inode}"
   test "$(stat -c '%U' "${current}")" = "${original_owner}"
   test "$(stat -c '%G' "${current}")" = "${original_group}"
   test "$(stat -c '%a' "${current}")" = "${original_mode}"

   host_caddy_sha="$(sha256sum "${current}" | awk '{print $1}')"
   container_caddy_sha="$(docker exec "${edge_container}" sha256sum /etc/caddy/Caddyfile | awk '{print $1}')"
   test "${host_caddy_sha}" = "${candidate_sha256}"
   test "${container_caddy_sha}" = "${candidate_sha256}"
   # HOST_CADDY_SHA == CANDIDATE_SHA == CONTAINER_CADDY_SHA is mandatory before reload.
   ```

   Do not reload until host current and container `/etc/caddy/Caddyfile` have equal SHA-256 bytes
   matching the candidate. This guard directly confirms visibility through the single-file read-only
   bind mount.

8. **After the container-visible byte guard, validate and use `caddy reload`.** A validation failure is
   STOP and no reload occurs. This lifecycle does not use `docker restart`, `docker compose restart`,
   `docker stop/start`, `up -d --force-recreate`, container stop/start, or container recreate.

   ```bash
   if ! docker exec "${edge_container}" \
     caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile; then
     echo 'STOP: mounted candidate validation failed; do not reload.' >&2
     exit 1
   fi
   docker exec "${edge_container}" \
     caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

9. **Run all post-reload acceptance checks.** Record each result in the existing value-free acceptance
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

10. **On failure, restore the old Caddyfile in the same inode → validate → reload → regression
    verification.** For a reload failure, acceptance failure, JP allow-list misgeneration, or ownership /
    mode drift, do not skip steps or edit around the failure. From the pre-established SSH session,
    restore last-known-good bytes into current in-place and restore the original owner / group / mode on
    the same inode. If current device / inode differs from original, do not replace the path; STOP and
    escalate.

    After restore, verify `device unchanged`, `inode unchanged`, `owner restored`, `group restored`,
    `mode restored`, and `HOST_CADDY_SHA == BACKUP_SHA == CONTAINER_CADDY_SHA`. Then validate with the
    same pinned Caddy 2.10.2, run `caddy reload`, and repeat every step-9 acceptance check plus the
    `/api` regression. If restore, validation, reload, or regression verification fails, keep SSH
    available, STOP, and escalate.

    ```bash
    # Confirm the current path remains the same regular file and original inode first.
    test -f "${current}" && test ! -L "${current}"
    test "$(stat -c '%d' "${current}")" = "${original_device}"
    test "$(stat -c '%i' "${current}")" = "${original_inode}"

    # Restore original metadata on the same inode, then restore bytes in place.
    chown "${original_uid}:${original_gid}" "${current}"
    chmod "${original_mode}" "${current}"
    write_contents_in_place "${last_known_good}" "${current}" \
      "${original_sha256}" "${original_bytes}"

    test "$(stat -c '%d' "${current}")" = "${original_device}"
    test "$(stat -c '%i' "${current}")" = "${original_inode}"
    test "$(stat -c '%U' "${current}")" = "${original_owner}"
    test "$(stat -c '%G' "${current}")" = "${original_group}"
    test "$(stat -c '%a' "${current}")" = "${original_mode}"
    backup_sha="$(sha256sum "${last_known_good}" | awk '{print $1}')"
    host_caddy_sha="$(sha256sum "${current}" | awk '{print $1}')"
    container_caddy_sha="$(docker exec "${edge_container}" sha256sum /etc/caddy/Caddyfile | awk '{print $1}')"
    test "${host_caddy_sha}" = "${backup_sha}"
    test "${container_caddy_sha}" = "${backup_sha}"

    docker exec "${edge_container}" \
      caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
    docker exec "${edge_container}" \
      caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
    # Repeat every step-9 acceptance check and the /api regression here.
    ```

    If a bad JP allow-list rejects every request from Japan, do not attempt a public-path fix. Use the
    pre-established SSH session to restore last-known-good in-place in the same inode, validate, reload, and
    verify the regression. Do not begin an apply until SSH rollback has been confirmed.

11. **Fail closed on GeoLite download/render/validate/update failure.** GeoLite acquisition is operator-
    side only. On any failure, keep the current last-known-good production Caddyfile in place. Do not
    put raw CSV or the bcrypt input hash file on the VPS, and never fall back to an empty candidate,
    empty CIDRs, default routes, or allow-all (`0.0.0.0/0` / `::/0`). Record the failure and STOP.

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
