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
| `/admin`, `/admin/*`, `/setup`, `/setup/*` | IPdeny aggregated JP CIDR **and** Caddy Basic Auth | Caddy `Authorization` is removed; Mailer's own Admin/Setup authentication remains |
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
  the renderer from IPdeny aggregated JP zones and to pass Caddy Basic Auth. Non-JP
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
from the IPdeny zones and the hash input.

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

Obtain the IPdeny aggregated JP zones over HTTPS only on the operator side or on
`agent-dev01`. The renderer itself is offline: it performs no download and only
parses, validates, normalizes, collapses, and renders the supplied inputs. The
canonical sources are:

- IPv4: `https://www.ipdeny.com/ipblocks/data/aggregated/jp-aggregated.zone`
- IPv6: `https://www.ipdeny.com/ipv6/ipaddresses/aggregated/jp-aggregated.zone`

Keep the zone files only in a secure operator / `agent-dev01` runtime workspace; do
not commit or transfer them to the VPS. Record the source URLs, download timestamp,
input bytes, and input SHA-256 as provenance metadata. Record HTTP `Last-Modified`
when present if useful; its absence alone is not a failure. The `operator_input_dir`
and `caddy_hash_file` below are paths on the operator / `agent-dev01`, never
`/srv/platform/edge`.

```bash
# Run on operator / agent-dev01
set -Eeuo pipefail
umask 077
operator_input_dir='/path/on/operator-or-agent-dev01/ipdeny/current'
caddy_hash_file='/path/on/operator-or-agent-dev01/caddy-admin.bcrypt'
mkdir -p "$operator_input_dir"
ipv4_url='https://www.ipdeny.com/ipblocks/data/aggregated/jp-aggregated.zone'
ipv6_url='https://www.ipdeny.com/ipv6/ipaddresses/aggregated/jp-aggregated.zone'
ipv4_headers="$operator_input_dir/jp-ipv4.headers"
ipv6_headers="$operator_input_dir/jp-ipv6.headers"

curl --fail --show-error --silent --location --proto '=https' --tlsv1.2 \
  --output "$operator_input_dir/jp-ipv4.zone" --dump-header "$ipv4_headers" "$ipv4_url"
curl --fail --show-error --silent --location --proto '=https' --tlsv1.2 \
  --output "$operator_input_dir/jp-ipv6.zone" --dump-header "$ipv6_headers" "$ipv6_url"

download_timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
{
  printf 'ipv4_url: %s\nipv6_url: %s\ndownload_timestamp: %s\n' "$ipv4_url" "$ipv6_url" "$download_timestamp"
  printf 'ipv4_bytes: '; stat -c '%s' "$operator_input_dir/jp-ipv4.zone"
  printf 'ipv4_sha256: '; sha256sum "$operator_input_dir/jp-ipv4.zone" | awk '{print $1}'
  printf 'ipv6_bytes: '; stat -c '%s' "$operator_input_dir/jp-ipv6.zone"
  printf 'ipv6_sha256: '; sha256sum "$operator_input_dir/jp-ipv6.zone" | awk '{print $1}'
  printf 'ipv4_last_modified: '; awk 'BEGIN {IGNORECASE=1} /^Last-Modified:/ {sub(/^[^:]*:[[:space:]]*/, ""); print; exit}' "$ipv4_headers"
  printf 'ipv6_last_modified: '; awk 'BEGIN {IGNORECASE=1} /^Last-Modified:/ {sub(/^[^:]*:[[:space:]]*/, ""); print; exit}' "$ipv6_headers"
} > "$operator_input_dir/provenance.txt"

python3 render-vps-management-edge.py \
  --ipv4-zone "$operator_input_dir/jp-ipv4.zone" \
  --ipv6-zone "$operator_input_dir/jp-ipv6.zone" \
  --basic-auth-username caddy-admin \
  --basic-auth-hash-file "$caddy_hash_file" \
  --template Caddyfile.vps-dogfood.example \
  --output ./Caddyfile.vps-dogfood.candidate
```

`caddy-admin` is a non-secret example. Choose a deployment-specific username; the
renderer rejects empty, whitespace, newline, and Caddyfile-token-injection values.
Each zone must contain one CIDR per line; only whitespace-only lines may be ignored.
Comments, extra tokens, invalid CIDRs, a wrong address family, either IPv4/IPv6 `/0`,
a missing or empty zone, or zero CIDRs on either side stop the render fail-closed.
Duplicate removal, adjacent CIDR collapse, and deterministic numeric ordering are
performed offline. Zone contents and the hash value are not printed in logs or the
summary.

`Caddyfile.vps-dogfood` is an ignored runtime artifact. The only item that may cross
to the VPS after separate approval is the generated Caddy candidate, plus value-free
IPv4/IPv6 CIDR counts, bytes, and SHA-256 metadata. This source stage does not
generate a real password or a real hash.

### Caddy Basic Auth credential boundary

For live operations, generate a new, sufficiently strong random Caddy Basic Auth password
with an approved password manager or CSPRNG. This rework does not generate a real password
or real hash. The Caddy Basic Auth password is a separate credential from the Mailer Admin
password and from the Setup bootstrap token; never reuse any of them. Keep it separate
from the IPdeny zone acquisition workspace and provenance metadata.

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
| A. OPERATOR / agent-dev01 | IPdeny zone inputs, provenance metadata, bcrypt hash input, renderer, operator-side candidate, CIDR counts, bytes, and SHA-256 | Raw IPdeny zones and the bcrypt input hash remain here and never go to the VPS. |
| B. REMOTE VPS READ-ONLY | Compose label resolution, container inspect, image/version/mount checks, read-only stat, and candidate stdin pre-validation as deploy | docker ps, docker inspect, and docker exec occur only inside SSH remote command bodies. No production mutation. |
| C. REMOTE VPS PRIVILEGED LIVE MUTATION | Human-approved backup, same-inode write, SHA guards, Caddy validate, reload, and rollback | Only inside an explicit Bash transaction after interactive sudo approval. No sudoers, SSH, or root-login change. |

Until pre-validation, the candidate remains on the operator and candidate bytes are consumed by the remote process
from SSH stdin. That stage is production mutation=false, candidate file persisted on VPS=false, and does not require
sudo. After Human approval, live mutation transfers only the generated Caddyfile candidate to a deploy-owned temporary
location. PERSISTENT_VPS_STAGING_REQUIRED=false and TEMPORARY_VPS_CANDIDATE_REQUIRED=true. Raw IPdeny zone data,
the bcrypt input hash file, and plaintext passwords are not transferred to the VPS. IPdeny raw data transferred=false
and bcrypt input file transferred=false.

Fresh topology is Compose project=amane-platform-edge, service=proxy, current
/srv/platform/edge/Caddyfile, container path=/etc/caddy/Caddyfile, single-file read-only bind mount,
mount source=/srv/platform/edge/Caddyfile, destination=/etc/caddy/Caddyfile, RW=false, and pinned Caddy 2.10.2.
The observed container name is reference-only; resolve by labels every time and require exactly one.
The Fresh baseline root:root / 0644 is reference data: 0644 is Fresh baseline only.

1. **OPERATOR / agent-dev01: generate the real IPdeny candidate.** Do not put raw IPdeny zones,
   provenance metadata, the bcrypt input hash file, or the plaintext password on the VPS. Production Caddyfile is
   unchanged at this stage.

   ~~~bash
   # Run on operator / agent-dev01
   set -Eeuo pipefail
   umask 077
   operator_input_dir=/path/on/operator-or-agent-dev01/ipdeny/current
   caddy_hash_file=/path/on/operator-or-agent-dev01/caddy-admin.bcrypt
   candidate=$PWD/infra/deploy/Caddyfile.vps-dogfood
   render_record=$PWD/caddy-render.txt
   python3 infra/deploy/render-vps-management-edge.py \
     --ipv4-zone "$operator_input_dir/jp-ipv4.zone" \
     --ipv6-zone "$operator_input_dir/jp-ipv6.zone" \
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
   allowed to cross to the VPS is the generated Caddyfile candidate. Do not transfer IPdeny raw zones, provenance
   metadata, the bcrypt input hash file, plaintext Basic Auth password, Mailer Admin password, or Setup
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

6. **PHASE 2 — open a standalone interactive SSH TTY after candidate transfer and SHA verification.** Before live
   mutation starts, open a TTY for the remote deploy shell without connecting a transaction HEREDOC to SSH stdin. This
   is a separate interactive SSH phase.

   ~~~bash
   # Run on the operator workstation after Phase 1 candidate transfer and verification.
   # This is a standalone TTY; do not append a HEREDOC to this ssh command.
   ssh -t "$VPS_ALIAS"
   ~~~

   The Human enters the remote deploy shell here. Before entering the interactive shell, the non-secret
   `candidate_remote`, `candidate_sha256`, and `candidate_bytes` metadata must be handled safely. On the operator
   workstation, use Bash `%q` to print shell-quoted assignments, then paste only those three lines unchanged at the
   remote Bash prompt. Do not paste candidate bytes, the sudo password, or any secret.

   ~~~bash
   # Run on the operator workstation before opening the TTY; metadata only.
   case "$candidate_sha256" in
     ''|*[!0-9a-fA-F]*) echo 'invalid candidate SHA-256' >&2; exit 1 ;;
   esac
   case "$candidate_bytes" in
     ''|*[!0-9]*) echo 'invalid candidate byte count' >&2; exit 1 ;;
   esac
   printf 'candidate_remote=%q\ncandidate_sha256=%q\ncandidate_bytes=%q\n' \
     "$candidate_remote" "$candidate_sha256" "$candidate_bytes"
   ~~~

   Paste the `%q` output at the remote Bash prompt without editing it. Re-check `candidate_remote` there as the
   deploy-owned `$HOME/.amane-caddy-744.*` regular file, not a symlink, with mode 0600. Never treat these values as a
   sudo password or as candidate stdin. The Phase 2 TTY is not transaction stdin.

   In the remote shell, the command meanings are:

   ~~~text
   sudo -v = interactive authentication
   sudo -n true = post-authentication credential-cache verification only
   sudo -n bash = already-authenticated transaction execution only
   ~~~

   `sudo -v` is interactive authentication. The Human enters the password directly at the VPS terminal's sudo prompt.
   sudo password is entered only at the interactive sudo prompt and is never supplied by script/stdin. Never put the
   password in chat, an issue, a log, a script, an environment variable, or a candidate stream. Use `sudo -n` only
   after `sudo -v` succeeds; never use it for initial authentication.

#### Privilege boundary

| context | permitted work |
| --- | --- |
| deploy / non-privileged | receive the temporary candidate, verify candidate size / SHA-256 / regular-file / owner / mode, perform currently-permitted Docker read/inspect, public/read-only checks, and invoke `sudo -v` |
| root / sudo Bash | only after Human approval: last-known-good backup, production Caddyfile same-inode write, metadata restoration, host/container SHA guards, Caddy validate/reload, and rollback |

Do not change sudoers, SSH config, root login, Docker topology, Caddy container recreation, or the firewall.

7. **PHASE 3 / PHASE 4 — privileged preflight and last-known-good backup.** Start the transaction only after `sudo -v`
   and `sudo -n true` have both succeeded in the same remote TTY/session. Phase 3 `sudo -n true` is only a fail-closed
   credential cache validity check, not authentication. `sudo -n bash` is already-authenticated transaction execution
   only. deploy is a read-only user; an unprivileged deploy write to the root-owned production Caddyfile is forbidden.
   Backup-directory creation, root-owned last-known-good backup, current write, chown, chmod restoration, reload, and
   rollback run only inside an explicit Bash transaction. Transaction uses Bash ERR trap / pipefail semantics; do not
   execute it through /bin/sh. Do not change sudoers, SSH settings, or root login. Re-read and preserve current device,
   inode, uid, gid, owner, group, mode, bytes, and SHA-256. Do not reset the Fresh root:root / 0644 baseline; 0644 is
   Fresh baseline only. Read the candidate from `$candidate_remote`; candidate bytes are not placed on the transaction
   script's stdin or on the sudo password's stdin.

   From the Phase 2 remote shell, run the Phase 3 check below. If it fails, do not start production mutation; clean up
   the temporary candidate, verify that it is absent, and STOP.

   ~~~bash
   # Run in the already-open remote deploy shell. No HEREDOC is used for authentication.
   set -Eeuo pipefail
   acceptance_origin="https://${MAILER_PUBLIC_HOSTNAME:?MAILER_PUBLIC_HOSTNAME must be set to the approved public hostname}"
   # The privileged helper uses this public HTTPS origin; never point it at mailer:8080.
   cleanup_temporary_candidate() {
     rm -f -- "$candidate_remote"
     test ! -e "$candidate_remote"
     test ! -L "$candidate_remote"
   }
   case "$candidate_remote" in
     "$HOME"/.amane-caddy-744.*) ;;
     *) echo 'candidate is outside the deploy-owned temporary location' >&2; cleanup_temporary_candidate; exit 1 ;;
   esac
   if ! sudo -v; then
     echo 'sudo -v failed; no production mutation; cleaning temporary candidate' >&2
     cleanup_temporary_candidate
     exit 1
   fi
   if ! sudo -n true; then
     echo 'sudo -n true failed after sudo -v; no production mutation; cleaning temporary candidate' >&2
     cleanup_temporary_candidate
     exit 1
   fi
   ~~~

   A `sudo -v` failure, `sudo -n true` failure, or `sudo -n bash` start failure before production mutation needs no
   rollback. In each case remove the temporary candidate with `rm -f -- "$candidate_remote"`, verify it with `test
   ! -e` and `test ! -L`, and STOP. Because `sudo -n` never prompts, never send a password on stdin to retry it.

8. **Enable the transaction and failure handler before same-inode write.** Initialize mutation_started=false
   and rollback_in_progress=false. Set mutation_started=true only after backup and original SHA verification.
   Candidate short read/write, Python exception, fsync failure, host SHA mismatch, container SHA mismatch,
   inode drift, owner/mode drift, post-write caddy validate failure, caddy reload failure, and post-reload
   acceptance failure are ANY FAILURE and trigger automatic rollback after mutation starts. Guard recursive ERR
   handling with rollback_in_progress and call rollback_current_in_place exactly once.

   ~~~bash
   # Type/paste this entire block in the already-open remote interactive TTY,
   # after interactive sudo authentication and the post-authentication cache check have succeeded.
   set -Eeuo pipefail
   if sudo -n bash -s -- \
     "$candidate_remote" \
     "$candidate_sha256" \
     "$candidate_bytes" \
     "$acceptance_origin" <<'ROOT_BASH'
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
     acceptance_origin=$4
     case "$acceptance_origin" in
       https://?*) ;;
       *) echo 'acceptance_origin must be an approved HTTPS origin' >&2; exit 1 ;;
     esac

     # The edge proxy and the mailer run in two distinct Compose projects on the VPS.
     # Resolve the proxy from edge_project / service=proxy here; exactly one or STOP.
     edge_project=amane-platform-edge
     mailer_project=amane-mailer-vps
     service=proxy
     image=caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d
     ids="$(docker ps --quiet --filter label=com.docker.compose.project=$edge_project --filter label=com.docker.compose.service=$service --filter status=running)"
     test "$(printf '%s\n' "$ids" | awk 'NF {n++} END {print n+0}')" -eq 1
     container="$(printf '%s\n' "$ids" | awk 'NF {print; exit}')"
     test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$container")" = "$edge_project"
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
     test "$(sha256sum "$last_known_good" | awk '{print $1}')" = "$original_sha256"

     capture_http_state() {
       local path=$1 body_file http_status body_sha256
       body_file="$(mktemp)"
       if ! http_status="$(curl --silent --show-error --connect-timeout 5 --max-time 10 \
         --output "$body_file" --write-out '%{http_code}' --request GET "$acceptance_origin$path")"; then
         rm -f -- "$body_file"
         return 1
       fi
       body_sha256="$(sha256sum "$body_file" | awk '{print $1}')"
       rm -f -- "$body_file"
       printf '%s|%s\n' "$http_status" "$body_sha256"
     }

     assert_http_status() {
       local path=$1 expected_status=$2 body_file http_status
       body_file="$(mktemp)"
       if ! http_status="$(curl --silent --show-error --connect-timeout 5 --max-time 10 \
         --output "$body_file" --write-out '%{http_code}' --request GET "$acceptance_origin$path")"; then
         rm -f -- "$body_file"
         return 1
       fi
       if [ "$http_status" != "$expected_status" ]; then
         rm -f -- "$body_file"
         return 1
       fi
       rm -f -- "$body_file"
     }

     assert_http_state() {
       local path=$1 expected_state=$2 actual_state
       actual_state="$(capture_http_state "$path")" || return 1
       test "$actual_state" = "$expected_state"
     }

     assert_api_no_send() {
       local expected_status=$1 body_file headers_file http_status
       body_file="$(mktemp)"
       headers_file="$(mktemp)"
       if ! http_status="$(curl --silent --show-error --connect-timeout 5 --max-time 10 \
         --dump-header "$headers_file" --output "$body_file" --write-out '%{http_code}' \
         --request GET --header 'Accept: application/json' \
         "$acceptance_origin$api_status_path")"; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       if [ "$http_status" != "$expected_status" ]; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       if ! grep -F '"code":"UNAUTHORIZED"' "$body_file" >/dev/null; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       if grep -Eiq '^www-authenticate:[[:space:]]*basic' "$headers_file"; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       rm -f -- "$body_file" "$headers_file"
     }

     assert_non_jp_management_boundary() {
       local path=$1 body_file headers_file http_status
       body_file="$(mktemp)"
       headers_file="$(mktemp)"
       if ! http_status="$(curl --silent --show-error --connect-timeout 5 --max-time 10 \
         --dump-header "$headers_file" --output "$body_file" --write-out '%{http_code}' \
         --request GET "$acceptance_origin$path")"; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       if [ "$http_status" != 404 ]; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       if grep -Eiq '^www-authenticate:[[:space:]]*basic' "$headers_file"; then
         rm -f -- "$body_file" "$headers_file"
         return 1
       fi
       rm -f -- "$body_file" "$headers_file"
     }

     assert_mailer_8080_unpublished() {
       local mailer_ids mailer_count mailer_container port_bindings
       # The mailer lives in mailer_project (amane-mailer-vps), not the edge project.
       mailer_ids="$(docker ps --quiet --filter label=com.docker.compose.project=$mailer_project \
         --filter label=com.docker.compose.service=mailer --filter status=running)"
       mailer_count="$(printf '%s\n' "$mailer_ids" | awk 'NF {n++} END {print n+0}')"
       test "$mailer_count" -eq 1
       mailer_container="$(printf '%s\n' "$mailer_ids" | awk 'NF {print; exit}')"
       test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$mailer_container")" = "$mailer_project"
       test "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "$mailer_container")" = mailer
       port_bindings="$(docker inspect --format '{{json .HostConfig.PortBindings}}' "$mailer_container")"
       case "$port_bindings" in
         null|'{}') ;;
         *)
           if printf '%s\n' "$port_bindings" | grep -F '"8080/tcp"' >/dev/null; then
             return 1
           fi
           ;;
       esac
     }

     api_status_path=/api/mail-requests/00000000-0000-0000-0000-000000000000
     baseline_healthz_state="$(capture_http_state /healthz)"
     baseline_readyz_state="$(capture_http_state /readyz)"
     assert_api_no_send 401
     baseline_api_state="$(capture_http_state "$api_status_path")"
     baseline_admin_state="$(capture_http_state /admin)"
     baseline_setup_state="$(capture_http_state /setup)"
     test "${baseline_healthz_state%%|*}" = 200
     test "${baseline_readyz_state%%|*}" = 200
     test "${baseline_api_state%%|*}" = 401
     test "${baseline_admin_state%%|*}" = 404
     test "${baseline_setup_state%%|*}" = 404

     run_approved_value_free_acceptance_checks() {
       local acceptance_mode=${1:-}
       case "$acceptance_mode" in
         candidate)
           test "$#" -eq 1
           assert_http_status /healthz 200
           assert_http_status /readyz 200
           assert_api_no_send 401
           # A VPS self-request is non-JP/unknown evidence: 404 must precede Basic challenge.
           assert_non_jp_management_boundary /admin
           assert_non_jp_management_boundary /setup
           assert_mailer_8080_unpublished
           ;;
         rollback)
           test "$#" -eq 6
           local baseline_healthz_state=$2 baseline_readyz_state=$3
           local baseline_api_state=$4 baseline_admin_state=$5 baseline_setup_state=$6
           assert_http_state /healthz "$baseline_healthz_state"
           assert_http_state /readyz "$baseline_readyz_state"
           assert_api_no_send "${baseline_api_state%%|*}"
           assert_http_state "$api_status_path" "$baseline_api_state"
           assert_http_state /admin "$baseline_admin_state"
           assert_http_state /setup "$baseline_setup_state"
           assert_mailer_8080_unpublished
           ;;
         *)
           echo 'unknown acceptance mode; fail closed' >&2
           return 1
           ;;
       esac
     }

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
       )" "$current" "$expected_sha" "$expected_bytes" "$original_device" "$original_inode" \
         "$original_uid" "$original_gid" "$original_mode" <"$source"
     }

     rollback_current_in_place() {
       rollback_in_progress=true
       set +e
       test "$(stat -c '%d' "$current")" = "$original_device" || return 1
       test "$(stat -c '%i' "$current")" = "$original_inode" || return 1
       chown "$original_uid:$original_gid" "$current" || return 1
       chmod "$original_mode" "$current" || return 1
       rollback_sha="$(sha256sum "$last_known_good" | awk '{print $1}')"
       write_contents_in_place "$last_known_good" "$rollback_sha" "$original_bytes" || return 1
       test "$(stat -c '%d' "$current")" = "$original_device" || return 1
       test "$(stat -c '%i' "$current")" = "$original_inode" || return 1
       test "$(stat -c '%u' "$current")" = "$original_uid" || return 1
       test "$(stat -c '%g' "$current")" = "$original_gid" || return 1
       test "$(stat -c '%a' "$current")" = "$original_mode" || return 1
       test "$(sha256sum "$current" | awk '{print $1}')" = "$rollback_sha" || return 1
       test "$(docker exec "$container" sha256sum "$container_path" | awk '{print $1}')" = "$rollback_sha" || return 1
       docker exec "$container" caddy validate --config "$container_path" --adapter caddyfile || return 1
       docker exec "$container" caddy reload --config "$container_path" --adapter caddyfile || return 1
       run_approved_value_free_acceptance_checks rollback \
         "$baseline_healthz_state" "$baseline_readyz_state" "$baseline_api_state" \
         "$baseline_admin_state" "$baseline_setup_state" || return 1
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
     # HOST_SHA == CANDIDATE_SHA == CONTAINER_SHA before validate/reload.
     # HOST_CADDY_SHA == CANDIDATE_SHA == CONTAINER_CADDY_SHA is the recorded equality.
     docker exec "$container" caddy validate --config "$container_path" --adapter caddyfile
     docker exec "$container" caddy reload --config "$container_path" --adapter caddyfile
     # Candidate-mode no-send checks: /healthz /readyz /api /admin /setup /:8080.
     run_approved_value_free_acceptance_checks candidate
   ROOT_BASH
   then
     cleanup_temporary_candidate
   else
     transaction_status=$?
     echo 'sudo -n bash or transaction failed; STOP and inspect rollback state' >&2
     # A sudo -n start failure cannot prompt and has not started mutation.
     # If ROOT_BASH started, its ERR trap owns automatic rollback; cleanup follows
     # successful rollback, while a rollback failure keeps SSH recovery in priority.
     cleanup_temporary_candidate
     exit "$transaction_status"
   fi
   ~~~

   The privileged writer opens the existing current fd for an in-place write, checks device/inode/uid/gid/mode, truncates,
   exact-writes, flushes, and calls fsync. It never changes the current path to a different inode and does
   not recreate a container. The container-visible SHA must equal the candidate SHA.

   **Approved value-free acceptance contract / acceptance responsibility.** Define
   `run_approved_value_free_acceptance_checks` before its first use and call it only with one of two explicit modes:
   `candidate` or `rollback`. `candidate` mode checks `/healthz=200`, `/readyz=200`, and a fixed nonexistent UUID
   `GET /api/mail-requests/<uuid>` returning Mailer's `401` with `"code":"UNAUTHORIZED"`, without a Basic challenge.
   It also checks that VPS self-requests to non-JP/unknown `/admin` and `/setup` receive `404` before a Basic challenge,
   and that Mailer host-published `8080/tcp` is `none/null`. It never sends mail: no `POST`, secret, or API key is used.
   The proxy is resolved by Compose label from the edge Compose project (`amane-platform-edge`) and the mailer from its
   own separate Compose project (`amane-mailer-vps`); each must resolve to exactly one container, and zero or multiple
   matches fail closed.

   `rollback` mode does not reuse fixed candidate statuses. It receives
   `baseline_healthz_state`, `baseline_readyz_state`, `baseline_api_state`, `baseline_admin_state`, and
   `baseline_setup_state` (status plus response-body SHA-256) captured before mutation, then verifies that the same
   state is restored after old-config reload. An unknown mode fails closed. Thus the `/admin` and `/setup`
   responsibilities of `candidate mode` and `rollback mode` cannot be conflated.

   The REMOTE VPS transaction proves only public HTTPS liveness/readiness through Caddy from the VPS, the Mailer API
   no-send unauthorized contract, the non-JP/unknown management boundary for the VPS self-request, and the Docker
   runtime absence of a Mailer `:8080` host publish. The root helper does not run SSH, Windows/browser, external JP-client,
   or Internet `:8080` reachability checks and must not print them as confirmed.

   Candidate acceptance for the external path is owned by Windows/operator acceptance. From a JP source, check `/admin`
   and `/setup` to prove that the Caddy Basic Auth boundary exists first and an unauthenticated request does not pass
   through to Mailer. From non-JP/unknown sources, check `404` before a Basic challenge; also check from outside that
   Mailer `:8080` is unreachable. The rollback failure entry point is `rollback_current_in_place` from the ERR trap.
   SSH recovery means keeping the current standalone SSH TTY open and, when needed, checking a separate SSH session
   from the operator/Windows side; the root transaction helper must never claim to have performed that check.

9. **Temporary candidate cleanup.** Invoke the cleanup function defined in Phase 2 from the same remote interactive
   TTY/session after acceptance succeeds or after automatic rollback succeeds. As deploy, `rm` the temporary candidate
   and verify that it no longer exists. Do the same before production mutation on any sudo failure.

   ~~~bash
   # Run in the same remote interactive shell after transaction success or rollback success.
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

11. **Fail closed and preserve the existing security contract.** IPdeny download/render/validate/update is
  operator-side only. Keep last-known-good on failure; never put raw IPdeny zones, provenance metadata, bcrypt input
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
- `/admin` and `/setup` require both an IPdeny-derived JP CIDR and Caddy Basic
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
