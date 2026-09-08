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
# Run on operator / agent-dev01
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

This is the production live-edge runbook. The VPS commands below are documentation only and are not executed
during this rework. Source Stage build/validation approval is not live-mutation approval. Before production
change, record a separate Human approval containing the change ID, hostname, candidate digest, executor, and
rollback owner.

#### Execution contexts

The live procedure has exactly three separate execution contexts:

| context | work | boundary |
| --- | --- | --- |
| A. OPERATOR / agent-dev01 | GeoLite inputs, bcrypt hash input, renderer, operator-side candidate, CIDR counts, bytes, and SHA-256 | Raw GeoLite CSV and the bcrypt input hash remain here and never go to the VPS. |
| B. REMOTE VPS READ-ONLY | Compose label resolution, container inspect, image/version/mount checks, read-only stat, and candidate stdin pre-validation as deploy | docker ps, docker inspect, and docker exec occur only inside SSH remote command bodies. No production mutation. |
| C. REMOTE VPS PRIVILEGED LIVE MUTATION | Human-approved backup, same-inode write, SHA guards, Caddy validate, reload, and rollback | Only inside an explicit Bash transaction after interactive sudo approval. No sudoers, SSH, or root-login change. |

Until pre-validation, the candidate remains on the operator and candidate bytes are consumed by the remote process
from SSH stdin. That stage is production mutation=false, candidate file persisted on VPS=false, and does not require
sudo. After Human approval, live mutation transfers only the generated Caddyfile candidate to a deploy-owned temporary
location. PERSISTENT_VPS_STAGING_REQUIRED=false and TEMPORARY_VPS_CANDIDATE_REQUIRED=true. Raw GeoLite data, MaxMind
credentials, the bcrypt input hash file, and plaintext passwords are not transferred to the VPS. GeoLite raw data transferred=false
and bcrypt input file transferred=false.

Fresh topology is Compose project=amane-platform-edge, service=proxy, current
/srv/platform/edge/Caddyfile, container path=/etc/caddy/Caddyfile, single-file read-only bind mount,
mount source=/srv/platform/edge/Caddyfile, destination=/etc/caddy/Caddyfile, RW=false, and pinned Caddy 2.10.2.
The observed container name is reference-only; resolve by labels every time and require exactly one.
The Fresh baseline root:root / 0644 is reference data: 0644 is Fresh baseline only.

1. **OPERATOR / agent-dev01: generate the real GeoLite candidate.** Do not put raw GeoLite CSV, MaxMind
   credentials, the bcrypt input hash file, or the plaintext password on the VPS. Production Caddyfile is
   unchanged at this stage.

   ~~~bash
   # Run on operator / agent-dev01
   set -Eeuo pipefail
   umask 077
   operator_input_dir=/path/on/operator-or-agent-dev01/geolite/current
   caddy_hash_file=/path/on/operator-or-agent-dev01/caddy-admin.bcrypt
   candidate=$PWD/infra/deploy/Caddyfile.vps-dogfood
   render_record=$PWD/caddy-render.txt
   python3 infra/deploy/render-vps-management-edge.py \
     --ipv4-blocks "$operator_input_dir/GeoLite2-Country-Blocks-IPv4.csv" \
     --ipv6-blocks "$operator_input_dir/GeoLite2-Country-Blocks-IPv6.csv" \
     --locations "$operator_input_dir/GeoLite2-Country-Locations-en.csv" \
     --basic-auth-username caddy-admin \
     --basic-auth-hash-file "$caddy_hash_file" \
     --template infra/deploy/Caddyfile.vps-dogfood.example \
     --output "$candidate" >"$render_record"
   test -f "$candidate" && test ! -L "$candidate"
   ~~~

2. **OPERATOR / agent-dev01: record candidate counts, bytes, and SHA.** Reconcile IPv4 CIDR count,
   IPv6 CIDR count, bytes, and SHA-256 with the renderer summary. Zero/unexpected counts, any mismatch,
   or inability to record the summary is STOP.

   ~~~bash
   # Run on operator / agent-dev01
   candidate_bytes="$(stat -c '%s' "$candidate")"
   candidate_sha256="$(sha256sum "$candidate" | awk '{print $1}')"
   grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "$render_record"
   printf 'candidate bytes: %s\ncandidate SHA-256: %s\n' "$candidate_bytes" "$candidate_sha256"
   ~~~

3. **REMOTE VPS READ-ONLY: resolve the Compose container by labels.** Do not run local docker ps, docker
   inspect, or docker exec on the operator. Inside the deploy SSH body, verify project label, service label,
   Config.Image, Caddy version, mount type/source/destination, and RW=false. Zero or multiple matches is STOP.

   ~~~bash
   # Run on VPS via SSH, read-only
   ssh "$VPS_ALIAS" 'bash -s' <<'REMOTE_READ_ONLY'
   set -Eeuo pipefail
   project=amane-platform-edge
   service=proxy
   current=/srv/platform/edge/Caddyfile
   image=caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d
   ids="$(docker ps --quiet --filter label=com.docker.compose.project=$project --filter label=com.docker.compose.service=$service --filter status=running)"
   count="$(printf '%s\n' "$ids" | awk 'NF {n++} END {print n+0}')"
   test "$count" -eq 1
   container="$(printf '%s\n' "$ids" | awk 'NF {print; exit}')"
   test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$container")" = "$project"
   test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "$container")" = "$service"
   test "$(docker inspect --format '{{.Config.Image}}' "$container")" = "$image"
   mount="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/etc/caddy/Caddyfile"}}{{.Type}}|{{.Source}}|{{.Destination}}|{{.RW}}{{end}}{{end}}' "$container")"
   test "$mount" = 'bind|/srv/platform/edge/Caddyfile|/etc/caddy/Caddyfile|false'
   docker exec "$container" caddy version | grep -F v2.10.2 >/dev/null
   test -f "$current" && test ! -L "$current"
   test "$(stat -c '%F' "$current")" = 'regular file'
   REMOTE_READ_ONLY
   ~~~

4. **OPERATOR → SSH stdin: pre-validate candidate bytes remotely.** Stream only the operator candidate to
   the actual running container after resolving labels again on the VPS. This is a read-only operation with
   production mutation=false, candidate file persisted on VPS=false, and no sudo. The candidate is never persisted
   on the VPS during pre-validation.

   ~~~bash
   # Run on operator / agent-dev01; only candidate bytes cross SSH stdin.
   cat "$candidate" | ssh "$VPS_ALIAS" '
     # Run on VPS via SSH, read-only
     set -Eeuo pipefail
     project=amane-platform-edge
     service=proxy
     ids="$(docker ps --quiet --filter label=com.docker.compose.project=$project --filter label=com.docker.compose.service=$service --filter status=running)"
     test "$(printf "%s\n" "$ids" | awk "NF {n++} END {print n+0}")" -eq 1
     container="$(printf "%s\n" "$ids" | awk "NF {print; exit}")"
     test "$(docker inspect --format "{{.Config.Image}}" "$container")" = "caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d"
     docker exec "$container" caddy version | grep -F v2.10.2 >/dev/null
     docker exec -i "$container" caddy validate --config - --adapter caddyfile
   '
   ~~~

   A caddy validate --config - failure is STOP: do not persist the candidate, mutate production, or fall
   back to allow-all CIDRs. This proves production Caddyfile mutation=false and candidate file persisted on
   VPS=false. No sudo is requested before live approval.

5. **Transfer a temporary candidate to the VPS only after separate Human approval.** Source Stage approval,
   renderer self-test, CI, and stdin validation PASS are not live approval. Confirm digest, counts, bytes, hostname,
   executor, rollback owner, and an SSH rollback session; missing approval or changed digest is STOP. The only item
   allowed to cross to the VPS is the generated Caddyfile candidate. Do not transfer GeoLite raw CSV, MaxMind account
   ID / license key, the bcrypt input hash file, plaintext Basic Auth password, Mailer Admin password, or Setup
   bootstrap token. Transfer allowlist: generated Caddyfile candidate only. The candidate contains a bcrypt hash and
   must be handled as a protected file.

   Use a deploy-owned temporary location such as `$HOME/.amane-caddy-744.XXXXXX`. Create it with `umask 077` and
   `mktemp`, and verify owner=deploy, mode=0600, regular file, and not symlink. The path is not Git managed, not
   Compose configuration, and not under the `/srv/platform/edge` production path. Do not create persistent VPS
   staging: PERSISTENT_VPS_STAGING_REQUIRED=false and TEMPORARY_VPS_CANDIDATE_REQUIRED=true.

   ~~~bash
   # Run on the operator workstation after separate Human live approval.
   set -Eeuo pipefail
   candidate_remote="$(ssh "$VPS_ALIAS" 'umask 077; mktemp "$HOME/.amane-caddy-744.XXXXXX"')"
   remove_temporary_candidate() {
     ssh "$VPS_ALIAS" 'bash -s' -- _ "$candidate_remote" <<'REMOTE_CANDIDATE_REMOVE'
   set -Eeuo pipefail
   candidate_remote=$1
   rm -f -- "$candidate_remote"
   test ! -e "$candidate_remote"
   REMOTE_CANDIDATE_REMOVE
   }
   if ! scp -- "$candidate" "${VPS_ALIAS}:${candidate_remote}"; then
     remove_temporary_candidate
     exit 1
   fi
   if ! ssh "$VPS_ALIAS" 'bash -s' -- _ "$candidate_remote" "$candidate_bytes" "$candidate_sha256" <<'REMOTE_CANDIDATE_VERIFY'
   set -Eeuo pipefail
   candidate_remote=$1
   expected_bytes=$2
   expected_sha256=$3
   case "$candidate_remote" in
     "$HOME"/.amane-caddy-744.*) ;;
     *) echo 'candidate is outside the deploy-owned temporary location' >&2; exit 1 ;;
   esac
   case "$candidate_remote" in
     /srv/platform/edge/*|*/compose*.yml|*/docker-compose*.yml)
       echo 'candidate path is a production or Compose path' >&2
       exit 1
       ;;
   esac
   test -f "$candidate_remote"
   test ! -L "$candidate_remote"
   test "$(stat -c '%F' "$candidate_remote")" = 'regular file'
   test "$(stat -c '%a' "$candidate_remote")" = '600'
   test "$(stat -c '%u' "$candidate_remote")" = "$(id -u)"
   test "$(stat -c '%g' "$candidate_remote")" = "$(id -g)"
   if [ -d "$HOME/.git" ] && git -C "$HOME" ls-files --error-unmatch -- "$candidate_remote" >/dev/null 2>&1; then
     echo 'candidate must not be Git managed' >&2
     exit 1
   fi
   remote_bytes="$(stat -c '%s' "$candidate_remote")"
   remote_sha256="$(sha256sum "$candidate_remote" | awk '{print $1}')"
   test "$remote_bytes" = "$expected_bytes"
   test "$remote_sha256" = "$expected_sha256"
   printf 'candidate bytes: %s\ncandidate SHA-256: %s\n' "$remote_bytes" "$remote_sha256"
   REMOTE_CANDIDATE_VERIFY
   then
     remove_temporary_candidate
     exit 1
   fi
   ~~~

   A candidate transfer, regular-file, owner, mode, bytes, or SHA-256 verification failure is STOP. In that case,
   run `rm -f -- "$candidate_remote"` as deploy and do not perform production mutation. The temporary candidate is
   not a persistent VPS staging area.

6. **Acquire interactive sudo after candidate transfer and SHA verification.** Before live mutation starts, open an
   interactive TTY deploy-user shell from the operator workstation with `ssh -t`. In that remote shell run `sudo -v`;
   the Human enters the sudo password directly at the VPS terminal prompt. If the sudo credential cache cannot be
   acquired, STOP; do not change sudoers. sudo password is entered only at the interactive sudo prompt and is never supplied by script/stdin. Never place the password in chat, an issue, a log, a script, an environment variable, or a candidate stream. If `sudo -v` fails, do not start live mutation; perform temporary candidate cleanup.

#### Privilege boundary

| context | permitted work |
| --- | --- |
| deploy / non-privileged | receive the temporary candidate, verify candidate size / SHA-256 / regular-file / owner / mode, perform currently-permitted Docker read/inspect, public/read-only checks, and invoke `sudo -v` |
| root / sudo Bash | only after Human approval: last-known-good backup, production Caddyfile same-inode write, metadata restoration, host/container SHA guards, Caddy validate/reload, and rollback |

Do not change sudoers, SSH config, root login, Docker topology, Caddy container recreation, or the firewall.

7. **Privileged preflight and last-known-good backup.** deploy is a read-only user; an unprivileged deploy
   write to the root-owned production Caddyfile is forbidden. Backup-directory creation, root-owned
   last-known-good backup, current write, chown, chmod restoration, reload, and rollback run only inside an
   explicit Bash transaction started with `sudo bash`. Transaction uses Bash ERR trap / pipefail semantics; do not
   execute it through /bin/sh. Do not change sudoers, SSH settings, or root login. Re-read and preserve current
   device, inode, uid, gid, owner, group, mode, bytes, and SHA-256. Do not reset the Fresh root:root / 0644
   baseline; 0644 is Fresh baseline only. Read the candidate from `$candidate_remote`; candidate bytes are not
   placed on the transaction script's stdin or on the sudo password's stdin.

8. **Enable the transaction and failure handler before same-inode write.** Initialize mutation_started=false
   and rollback_in_progress=false. Set mutation_started=true only after backup and original SHA verification.
   Candidate short read/write, Python exception, fsync failure, host SHA mismatch, container SHA mismatch,
   inode drift, owner/mode drift, post-write caddy validate failure, caddy reload failure, and post-reload
   acceptance failure are ANY FAILURE and trigger automatic rollback after mutation starts. Guard recursive ERR
   handling with rollback_in_progress and call rollback_current_in_place exactly once.

   ~~~bash
   # Run from the operator workstation after candidate transfer and read-only SHA verification.
   ssh -t "$VPS_ALIAS" 'bash -s' -- _ "$candidate_remote" "$candidate_sha256" "$candidate_bytes" <<'REMOTE_LIVE'
   set -Eeuo pipefail
   candidate_remote=$1
   candidate_sha256=$2
   candidate_bytes=$3

   # Human enters the password only at this interactive sudo prompt on the VPS.
   sudo -v
   sudo bash -s -- _ "$candidate_remote" "$candidate_sha256" "$candidate_bytes" <<'ROOT_BASH'
     # Run on VPS via approved privileged explicit Bash transaction
     # stdin is ROOT_BASH, while candidate bytes are read from candidate_remote.
     set -Eeuo pipefail
     mutation_started=false
     rollback_in_progress=false
     current=/srv/platform/edge/Caddyfile
     container_path=/etc/caddy/Caddyfile
     candidate_remote=$1
     candidate_sha256=$2
     candidate_bytes=$3

     # Resolve project=amane-platform-edge / service=proxy here; exactly one or STOP.
     project=amane-platform-edge
     service=proxy
     image=caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d
     ids="$(docker ps --quiet --filter label=com.docker.compose.project=$project --filter label=com.docker.compose.service=$service --filter status=running)"
     test "$(printf '%s\n' "$ids" | awk 'NF {n++} END {print n+0}')" -eq 1
     container="$(printf '%s\n' "$ids" | awk 'NF {print; exit}')"
     test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$container")" = "$project"
     test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "$container")" = "$service"
     test "$(docker inspect --format '{{.Config.Image}}' "$container")" = "$image"
     mount="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/etc/caddy/Caddyfile"}}{{.Type}}|{{.Source}}|{{.Destination}}|{{.RW}}{{end}}{{end}}' "$container")"
     test "$mount" = 'bind|/srv/platform/edge/Caddyfile|/etc/caddy/Caddyfile|false'
     docker exec "$container" caddy version | grep -F v2.10.2 >/dev/null
     test -f "$current" && test ! -L "$current"
     original_device="$(stat -c '%d' "$current")"
     original_inode="$(stat -c '%i' "$current")"
     original_uid="$(stat -c '%u' "$current")"
     original_gid="$(stat -c '%g' "$current")"
     original_owner="$(stat -c '%U' "$current")"
     original_group="$(stat -c '%G' "$current")"
     original_mode="$(stat -c '%a' "$current")"
     original_bytes="$(stat -c '%s' "$current")"
     original_sha256="$(sha256sum "$current" | awk '{print $1}')"
     change_id="$(date -u +%Y%m%dT%H%M%SZ)"
     last_known_good_dir=/srv/platform/edge/last-known-good
     last_known_good="$last_known_good_dir/Caddyfile.$change_id.sha256-$original_sha256"
     # Record device, inode, uid, gid, owner, group, mode, bytes, and SHA-256.
     install -d -o root -g root -m 0700 /srv/platform/edge/last-known-good
     install -o "$original_owner" -g "$original_group" -m "$original_mode" \
       -- "$current" "$last_known_good"
     test "$(sha256sum "$last_known_good" | awk '"'"'{print $1}'"'"')" = "$original_sha256"

     write_contents_in_place() {
       source=$1
       expected_sha=$2
       expected_bytes=$3
       python3 -c "$(cat <<'PY'
   import hashlib, os, stat, sys
   target, expected_sha, expected_bytes = sys.argv[1], sys.argv[2], int(sys.argv[3])
   expected_dev, expected_ino = int(sys.argv[4]), int(sys.argv[5])
   expected_uid, expected_gid = int(sys.argv[6]), int(sys.argv[7])
   expected_mode = int(sys.argv[8], 8)
   data = sys.stdin.buffer.read(expected_bytes + 1)
   if len(data) != expected_bytes:
       raise RuntimeError("candidate short read or trailing bytes")
   if hashlib.sha256(data).hexdigest() != expected_sha:
       raise RuntimeError("candidate SHA-256 mismatch")
   fd = os.open(target, os.O_RDWR | getattr(os, "O_NOFOLLOW", 0))
   try:
       before = os.fstat(fd)
       if (before.st_dev, before.st_ino, before.st_uid, before.st_gid, stat.S_IMODE(before.st_mode)) != (expected_dev, expected_ino, expected_uid, expected_gid, expected_mode):
           raise RuntimeError("current device/inode/ownership/mode guard failed")
       os.ftruncate(fd, 0)
       written = 0
       while written < len(data):
           part = os.write(fd, data[written:])
           if part <= 0:
               raise RuntimeError("short write")
           written += part
       os.fsync(fd)
   finally:
       os.close(fd)
   PY
       ) "$current" "$expected_sha" "$expected_bytes" "$original_device" "$original_inode" \
         "$original_uid" "$original_gid" "$original_mode" <"$source"
     }

     rollback_current_in_place() {
       rollback_in_progress=true
       set +e
       test "$(stat -c '%d' "$current")" = "$original_device" || return 1
       test "$(stat -c '%i' "$current")" = "$original_inode" || return 1
       chown "$original_uid:$original_gid" "$current" || return 1
       chmod "$original_mode" "$current" || return 1
       rollback_sha="$(sha256sum "$last_known_good" | awk '"'"'{print $1}'"'"')"
       write_contents_in_place "$last_known_good" "$rollback_sha" "$original_bytes" || return 1
       test "$(stat -c '%d' "$current")" = "$original_device" || return 1
       test "$(stat -c '%i' "$current")" = "$original_inode" || return 1
       test "$(stat -c '%u' "$current")" = "$original_uid" || return 1
       test "$(stat -c '%g' "$current")" = "$original_gid" || return 1
       test "$(stat -c '%a' "$current")" = "$original_mode" || return 1
       test "$(sha256sum "$current" | awk '"'"'{print $1}'"'"')" = "$rollback_sha" || return 1
       test "$(docker exec "$container" sha256sum "$container_path" | awk '"'"'{print $1}'"'"')" = "$rollback_sha" || return 1
       docker exec "$container" caddy validate --config "$container_path" --adapter caddyfile || return 1
       docker exec "$container" caddy reload --config "$container_path" --adapter caddyfile || return 1
       run_approved_value_free_acceptance_checks || return 1
     }

     failure_handler() {
       status=$?
       if [ "$mutation_started" != true ] || [ "$rollback_in_progress" = true ]; then return "$status"; fi
       trap - ERR
       rollback_current_in_place || echo 'automatic rollback failed; keep SSH and escalate.' >&2
       exit "$status"
     }
     trap failure_handler ERR

     # Backup is complete. ANY FAILURE from this line invokes rollback.
     mutation_started=true
     write_contents_in_place "$candidate_remote" "$candidate_sha256" "$candidate_bytes"
     test "$(stat -c '%d' "$current")" = "$original_device"
     test "$(stat -c '%i' "$current")" = "$original_inode"
     test "$(stat -c '%u' "$current")" = "$original_uid"
     test "$(stat -c '%g' "$current")" = "$original_gid"
     test "$(stat -c '%a' "$current")" = "$original_mode"
     host_sha="$(sha256sum "$current" | awk '"'"'{print $1}'"'"')"
     container_sha="$(docker exec "$container" sha256sum "$container_path" | awk '"'"'{print $1}'"'"')"
     test "$host_sha" = "$candidate_sha256"
     test "$container_sha" = "$candidate_sha256"
     # HOST_CADDY_SHA == CANDIDATE_SHA == CONTAINER_CADDY_SHA before validate/reload.
     docker exec "$container" caddy validate --config "$container_path" --adapter caddyfile
     docker exec "$container" caddy reload --config "$container_path" --adapter caddyfile
     # Approved no-send checks: /healthz /readyz /api /admin /setup /:8080 /SSH.
     run_approved_value_free_acceptance_checks
   ROOT_BASH
   REMOTE_LIVE
   ~~~

   The privileged writer opens the existing current fd for an in-place write, checks device/inode/uid/gid/mode, truncates,
   exact-writes, flushes, and calls fsync. It never changes the current path to a different inode and does
   not recreate a container. The container-visible SHA must equal the candidate SHA.

9. **Temporary candidate cleanup.** After acceptance succeeds, use deploy to `rm` the temporary candidate and verify
   that it no longer exists. Do the same after automatic rollback completes successfully.

   ~~~bash
   # Define/run on the operator workstation; the remote cleanup command runs as deploy.
   cleanup_temporary_candidate() {
     ssh "$VPS_ALIAS" 'bash -s' -- _ "$candidate_remote" <<'REMOTE_CANDIDATE_CLEANUP'
   set -Eeuo pipefail
   candidate_remote=$1
   rm -f -- "$candidate_remote"
   test ! -e "$candidate_remote"
   test ! -L "$candidate_remote"
   REMOTE_CANDIDATE_CLEANUP
   }
   cleanup_temporary_candidate
   ~~~

   If rollback itself fails, prioritize SSH recovery and do not let candidate cleanup interfere with rollback
   evidence or recovery. After incident handling ends, leave no protected candidate behind: run the same `rm` and
   absence checks. Secure-delete / shred guarantees are not required; ordinary `rm` is sufficient.

10. **Automatic rollback contract.** A validate failure, reload failure, or post-reload acceptance failure
   is rollback on validate failure / rollback on reload failure; never exit while disk still contains candidate
   bytes. Restore last-known-good to the original same inode and verify device, inode, uid, gid, owner, group,
   mode, HOST_SHA == ORIGINAL_SHA == CONTAINER_SHA. Then validate old config, reload old config when needed,
   and repeat /healthz, /readyz, /api regression, /admin, /setup, :8080 unreachable, and SSH checks. If
   rollback, validation, reload, or regression fails, keep SSH open, STOP, and escalate. Never replace a path
   after inode drift. Confirm rollback success before temporary candidate cleanup.

11. **Fail closed and preserve the existing security contract.** GeoLite download/render/validate/update is
   operator-side only. Keep last-known-good on failure; never put raw CSV, MaxMind credentials, bcrypt input
   hash file, or plaintext password on the VPS; never fall back to empty CIDRs, default routes, or allow-all
   (0.0.0.0/0 / ::/0). Preserve /admin and /setup as JP CIDR + Caddy Basic Auth + Mailer own auth, non-JP
   404 before Basic challenge, /metrics MAILER_MANAGEMENT_ALLOWED_CIDRS, public /api/* /healthz /readyz,
   removal of Caddy Basic Authorization before Mailer upstream, and unpublished Mailer :8080. #744 remains
   edge hardening; do not start #745 fresh setup, real ACS send, or UX dogfood.
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
