[English](vps-dogfood-deployment.en.md)

# VPS dogfood deployment（Issue #744 Source Stage）

この runbook は、Issue #744 の source-stage reference deployment です。Caddy が host の
80/443 だけを受け、Mailer は Docker network 内の HTTP backend として動きます。
Mailer の 8080 は host に publish しません。

この文書の範囲は deployment security boundary と fresh setup の経路確認です。
ACS の実送信、公式 smoke client、複数 Sender / API Key dogfood、revoke、restart dogfood
は別の検証範囲です。PR3 で追加された full backup / restore は、下記の専用 runbook と
helper を使い、Mailer の archive と Caddy state を混ぜません。

## 構成

```text
Internet / operator
        │ HTTPS :443（Caddy の automatic HTTPS）
        ▼
proxy（Caddy、host :80/:443 のみ）
        │ vps_proxy network
        ▼
mailer:8080（host port なし）
```

edge の path contract は次の通りです。

| Path | Caddy edge boundary | Mailer へ渡す認証 |
|---|---|---|
| `/api/*`、`/healthz`、`/readyz` | public | path に応じた既存の認証 |
| `/admin`、`/admin/*`、`/setup`、`/setup/*` | GeoLite2 の JP CIDR **かつ** Caddy Basic Auth | Caddy の `Authorization` は除去し、Mailer 自身の Admin/Setup 認証を維持 |
| `/metrics` | `MAILER_MANAGEMENT_ALLOWED_CIDRS` の operator CIDR | 既存の Mailer metrics bearer |
| その他 | 404 | upstream へ渡さない |

Admin/Setup は、JP CIDR に一致しない場合は Basic Auth challenge を返す前に edge で 404
になります。JP から Caddy Basic Auth に成功した場合だけ Mailer へ reverse proxy されます。

`compose.vps-dogfood.yml` は base の `mailer` service を次のように overlay します。

- `mailer` は `internal` と専用 `vps_proxy` だけに参加します。base の consumer 用
  `mailer` network はこの profile では置き換えられ、proxy bypass を残しません。
- `proxy` と `mailer` は専用 network 上の固定 IPv4 を使います。Mailer が信頼する
  forwarded header source は proxy の固定 IPv4 一つだけです。trusted network 全体や
  `0.0.0.0/0` は設定しません。
- 専用 network は ACS と Caddy ACME の outbound 通信が必要なため Docker の
  `internal: true` にはしていません。参加 service は `proxy` と `mailer` に限定し、
  host port は proxy だけにします。
- Caddy の `/admin` と `/setup` は、renderer が GeoLite2 Country CSV から導出した JP
  IPv4/IPv6 CIDR と Caddy Basic Auth の両方を要求します。JP 以外には challenge 前に 404
  を返し、成功した Caddy Basic credential は `header_up -Authorization` で Mailer に渡しません。
- `/metrics` は既存の `MAILER_MANAGEMENT_ALLOWED_CIDRS` operator restriction と Mailer の
  metrics bearer を維持します。Japan 全体へ公開するためにこの値を使い回しません。
- `/api/*`、`/healthz`、`/readyz` だけを public path として proxy し、それ以外は 404 です。
- base compose に残る legacy tenant JSON bind と
  `MAILER_TENANTS_PATH`、`MAIL_SERVICE_TOKEN*`、`MAILER_PROVIDER` は、この overlay の
  Compose merge (`!override` / `!reset`) で `mailer` と `mailer-migrate` の実効設定から
  除去されます。VPS managed-v2 の migration / first-run に tenant JSON、tenant token、
  v1 provider 設定は必要ありません。

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` は operator の client IP ではありません。Mailer が
proxy から受ける request の `Connection.LocalIpAddress`（この profile では Mailer の
専用 network 側アドレス）を許可するための設定です。operator CIDR は Caddy 側の
`remote_ip` matcher で別に制限します。

## 初回準備

Docker Engine と Compose plugin（`!override` と `!reset` をサポートするバージョン）、公開 DNS、host firewall の設定は事前に用意します。
Mailer は Docker や firewall、DNS、TLS account を自動設定しません。

`infra/deploy` で `.env` を作成します。Caddyfile は単純にコピーせず、GeoLite2 と hash を
入力に renderer で生成します。

```bash
cp .env.vps-dogfood.example .env
```

`.env` の VPS placeholder を deploy host の値へ置き換えます。少なくとも次を確認します。

- `MAILER_IMAGE_REPOSITORY` と `MAILER_IMAGE_TAG` は公開済みの検証済み Mailer image。
- `MAILER_DATA_PATH` は SQLite managed state を保存する persistent directory。
- `./secrets/acs` と `./secrets/bounce-queue` は mode 0700 の protected directory として
  用意する。これは Compose の read-only compatibility / manual registration mount です。
  Browser setup の managed-v2 provider authority は `MAILER_DATA_PATH/secrets/acs` に
  保存されます。ACS provider secret は承認済みの file-based register flow で保存し、`.env`
  や tenant token には置かない。metrics を有効にする場合だけ、private な
  `MAILER_METRICS_BEARER_TOKEN` を deploy host の `.env` に追加する。
- `MAILER_PUBLIC_HOSTNAME` は実際の DNS name。
- `MAILER_MANAGEMENT_ALLOWED_CIDRS` は `/metrics` 用に VPN / firewall で決めた operator
  source IP/CIDR です。`.env.example` の `192.0.2.0/24` は文書用 TEST-NET であり、そのまま
  使用しません。複数値は空白区切りで、例は `"192.0.2.0/24 2001:db8:1234::/48"` です。
- `MAILER_VPS_PROXY_NETWORK_SUBNET` と固定 IPv4 が host 上の既存 network と衝突しない。
  変更する場合は subnet、proxy address、Mailer address の関係を保ちます。
- `MAILER_TENANTS_HOST_PATH`、`MAILER_TENANTS_CONTAINER_PATH`、
  `MAIL_SERVICE_TOKEN`、`MAIL_SERVICE_TOKEN_DEVELOP`、`MAIL_SERVICE_TOKEN_STAGING`、
  `MAIL_SERVICE_TOKEN_PRODUCTION`、`MAILER_PROVIDER` は設定しない。fresh VPS 用の
  `tenants.json` も作成しない。

### VPS managed-v2 first-run の正本

この reference path の contract は次の通りです。

- `SQLite managed state` = product configuration authority（provider、instance owner、
  sender、API key の正本）。
- `provider secret` = protected file。browser setup が保存する canonical path は
  `MAILER_DATA_PATH/secrets/acs/acs_connection_string`（container 内では
  `/app/data/secrets/acs/acs_connection_string`）です。ACS secret は file-based
  registration と setup の定められた protected path だけで扱います。
- `bootstrap token` = transient protected file。初回表示後は password や他の secret と
  同じ扱いにし、不要になった file は保護した上で削除します。
- `tenants.json` / `MAIL_SERVICE_TOKEN*` = legacy/manual path。VPS v2 reference
  deployment では不要であり、初回 setup の active product configuration source of truth
  ではありません。

共通の `infra/deploy/.env.example` は base compose の manual / compatibility path 用です。
VPS では上記の `.env.vps-dogfood.example` を使うため、共通 template にある legacy
placeholder を設定する必要はありません。

### Caddy edge artifact の生成

real GeoLite2 Country CSV からの candidate 生成は operator または `agent-dev01` 側だけで行います。
renderer は MaxMind へ接続せず、download、license key、account ID を扱いません。次の入力は
operator / `agent-dev01` の secure input path にだけ置き、VPS へ置きません。

- GeoLite raw CSV（IPv4 blocks、IPv6 blocks、locations-en）
- MaxMind account ID / license key
- bcrypt input hash file
- plaintext Basic Auth password

VPS へ渡してよいのは、別途承認された後の generated Caddy candidate と、値を含まない
IPv4/IPv6 CIDR count、bytes、SHA-256 だけです。実 password の生成や実 hash の作成はこの
source stage では行いません。以下の `operator_input_dir` と `caddy_hash_file` は operator /
`agent-dev01` 側だけの path であり、`/srv/platform/edge` ではありません。

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

`caddy-admin` は非秘密の例です。deploymentごとに選んだ username を渡します。空白、改行、
Caddyfile token injection になる文字を含む username は renderer が拒否します。

`Caddyfile.vps-dogfood` は `.gitignore` 済みの runtime artifact です。renderer は
`network.geoname_id → locations.geoname_id → country_iso_code == JP` の経路だけを使い、
`registered_country_geoname_id` と `represented_country_geoname_id` へ fallback しません。
empty/unknown geoname は JP とせず、CIDR 0件、invalid/default CIDR、必須列欠落、conflicting
mapping、空/invalid hash は fail-closed で停止します。出力へ GeoLite raw CSV は保存せず、
hash の値もログや summary に表示しません。

### Caddy Basic Auth の credential boundary

実運用では、Caddy Basic Auth password は password manager または承認済みの CSPRNG で十分に
強いランダム値を新規生成します。この rework では実 password や実 hash を生成しません。
生成した Caddy Basic Auth password は Mailer Admin password、Setup bootstrap token と別の
資格情報にし、どの値も再利用しません。MaxMind / GeoLite の download credential、account ID、
license key とも無関係です。

- `Caddyfile` の Basic Auth credential material は bcrypt hash のみです。plaintext password を
  repository、production `Caddyfile`、`.env`、Issue、log のいずれにも保存しません。
- renderer に渡すのは既生成 hash のみです。`--basic-auth-hash-file` を使い、plaintext password
  や password generator を renderer に渡しません。hash 自体も log や証跡へ表示しません。
- 実運用の password / bcrypt hash は operator の承認済み secret path で別途扱います。この
  rework は実 credential を生成・記録しません。

### 安全な live-edge apply lifecycle（production runbook）

ここは production の live edge を更新する runbook です。以下の実 VPS command は手順の記述だけであり、
この rework 中には実行しません。Source Stage の build / validation の承認は live mutation の承認では
ありません。production 変更前に change ID、hostname、candidate digest、実行者、rollback owner を含む
別の Human approval を記録します。

#### Execution contexts / 実行コンテキスト

| context | 実行内容 | boundary |
| --- | --- | --- |
| A. OPERATOR / agent-dev01 | GeoLite inputs、bcrypt hash input、renderer、operator-side candidate、CIDR counts、bytes、SHA-256 | raw GeoLite CSV と bcrypt input hash はここにだけ残す。VPSへ送らない。 |
| B. REMOTE VPS READ-ONLY | deploy user の SSH 内で Compose label resolution、container inspect、image/version/mount確認、read-only stat、stdin pre-validation | remote VPS read-only body の docker ps、docker inspect、docker exec だけ。production mutation はしない。 |
| C. REMOTE VPS PRIVILEGED LIVE MUTATION | Human approval 後の backup、same-inode write、SHA guards、validate、reload、rollback | interactive sudo approval 後の explicit Bash transaction 内だけ。sudoers、SSH、root login は変更しない。 |

Pre-validation までは candidate は operator に保持し、candidate bytes を SSH stdin stream で remote process に渡します。
この段階は production mutation=false、candidate file persisted on VPS=false、sudo不要です。Human approval 後の
live mutation では、generated Caddyfile candidate only を deploy user の temporary location へ転送します。
PERSISTENT_VPS_STAGING_REQUIRED=false、TEMPORARY_VPS_CANDIDATE_REQUIRED=true です。raw GeoLite data、MaxMind
credential、bcrypt input hash file、plaintext password は VPS へ転送しません。GeoLite raw data transferred=false、
bcrypt input file transferred=false です。

Fresh topology は Compose project=amane-platform-edge、service=proxy、current
/srv/platform/edge/Caddyfile、container path=/etc/caddy/Caddyfile、single-file read-only bind mount、
mount source=/srv/platform/edge/Caddyfile、destination=/etc/caddy/Caddyfile、RW=false、
pinned Caddy 2.10.2 です。observed container name は参考値で、毎回 label から exactly one に
resolve します。Fresh baseline の root:root / 0644 は参考値で、0644 is Fresh baseline only です。

1. **OPERATOR / agent-dev01 で candidate を生成する。** raw GeoLite CSV、MaxMind credential、
   bcrypt input hash file、plaintext password は VPS に置きません。production Caddyfile はこの段階で
   変更しません。

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

2. **OPERATOR / agent-dev01 で counts / bytes / SHA を記録する。** IPv4 CIDR count、IPv6 CIDR count、
   bytes、SHA-256 の不一致、count 0、summary の記録不能は STOP です。

   ~~~bash
   # Run on operator / agent-dev01
   candidate_bytes="$(stat -c '%s' "$candidate")"
   candidate_sha256="$(sha256sum "$candidate" | awk '{print $1}')"
   grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "$render_record"
   printf 'candidate bytes: %s\ncandidate SHA-256: %s\n' "$candidate_bytes" "$candidate_sha256"
   ~~~

3. **REMOTE VPS READ-ONLY で label resolution を行う。** operator shell で local Docker を操作せず、
   deploy user の SSH body 内だけで project label、service label、Config.Image、Caddy version、mount
   type/source/destination、RW=false を確認します。0件または複数件は STOP です。

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

4. **OPERATOR → SSH stdin で pre-validation する。** candidate bytes だけを stdin で渡し、remote 側で
   label resolution を再実行して、同じ running container に caddy validate --config - を実行します。
   これは production mutation=false、candidate file persisted on VPS=false、sudo不要の read-only 操作です。
   pre-validation 中は candidate file を VPS に保存しません。

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

   pre-validation failure は STOP、production Caddyfile mutation=false、candidate file persisted on VPS=false
   です。allow-all CIDR fallback はしません。live approval 前なので sudo は要求しません。

5. **Human approval 後にだけ temporary candidate を VPS へ転送する。** Source Stage approval、renderer
   self-test、CI、stdin validation PASS は live approval ではありません。digest、counts、bytes、hostname、
   executor、rollback owner、SSH rollback session を確認し、欠落や digest 変更なら STOP します。VPS へ
   転送してよいのは generated Caddyfile candidate only です。GeoLite raw CSV、MaxMind account ID / license
   key、bcrypt input hash file、plaintext Basic Auth password、Mailer Admin password、Setup bootstrap token
   は転送しません。candidate には bcrypt hash が含まれるため protected file として扱います。

   temporary location は deploy user が安全に書ける `$HOME/.amane-caddy-744.XXXXXX` を使います。`umask 077`
   と `mktemp` で作成し、owner=deploy、mode=0600、regular file、not symlink を確認します。この path は
   not Git managed file、Compose configuration、`/srv/platform/edge` production path ではありません。persistent
   VPS staging は作らず、PERSISTENT_VPS_STAGING_REQUIRED=false、TEMPORARY_VPS_CANDIDATE_REQUIRED=true です。

   ~~~bash
   # Run on operator workstation after separate Human live approval.
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

   candidate transfer / regular-file / owner / mode / bytes / SHA-256 verification failure is STOP. In that case
   run `rm -f -- "$candidate_remote"` as deploy and do not perform production mutation. The temporary candidate
   is not a persistent VPS staging area.

6. **candidate transfer / SHA確認後に interactive sudo を取得する。** live mutation 開始前に、operator
   workstation から `ssh -t` で deploy user の interactive TTY shell を開きます。その remote shell で `sudo -v`
   を実行し、Human が VPS terminal の sudo prompt に password を直接入力します。sudo credential cache を
   取得できなければ STOP、sudoers は変更しません。sudo password is entered only at the interactive sudo prompt and is never supplied by script/stdin. sudo password は interactive sudo prompt にだけ Human が直接
   入力し、script/stdin から決して供給しません。password は chat、Issue、log、script、environment variable、
   candidate stream のいずれにも載せません。sudo -v が失敗した場合も live mutation を開始せず、temporary
   candidate cleanup を実施します。

#### Privilege boundary / 権限境界

| context | 許可される操作 |
| --- | --- |
| deploy / non-privileged | temporary candidate receive、candidate の size / SHA-256 / regular-file / owner / mode verification、現在許可されている Docker read/inspect、public/read-only checks、`sudo -v` invocation |
| root / sudo Bash | Human approval 後だけの last-known-good backup、production Caddyfile same-inode write、metadata restoration、host/container SHA guards、Caddy validate/reload、rollback |

sudoers、SSH config、root login、Docker topology、Caddy container recreation、firewall は変更しません。

7. **REMOTE VPS PRIVILEGED LIVE MUTATION で preflight / backup を行う。** deploy は read-only user
   なので、root-owned Caddyfile への unprivileged write は禁止です。backup directory、root-owned
   last-known-good backup、current write、chown、chmod restoration、reload、rollback は `sudo bash` で
   起動した explicit Bash transaction 内だけで行います。transaction uses Bash ERR trap / pipefail semantics;
   do not execute it through /bin/sh. Fresh の root:root / 0644 を再設定せず、live に再取得した owner、
   group、mode、device、inode、uid、gid、bytes、SHA-256 を preserve します。#744 は mode hardening を
   しません。candidate は `$candidate_remote` から読み、transaction script の stdin や sudo password の
   stdin には載せません。

8. **transaction / failure handler を先に準備する。** backup と original SHA の検証後にだけ
   mutation_started=true とし、初期値は mutation_started=false、rollback_in_progress=false とします。
   candidate short read / short write、Python exception、fsync failure、host SHA mismatch、
   container SHA mismatch、inode drift、owner/mode drift、post-write caddy validate failure、
   caddy reload failure、post-reload acceptance failure の ANY FAILURE は automatic rollback 対象です。
   trap ERR は rollback_in_progress guard で再帰を防ぎ、rollback_current_in_place を一度だけ呼びます。

   ~~~bash
   # Run from operator workstation after candidate transfer and read-only SHA verification.
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
     # Approved no-send checks: /healthz /readyz /api /admin /setup /:8080 /SSH.
     run_approved_value_free_acceptance_checks
   ROOT_BASH
   REMOTE_LIVE
   ~~~

   production current は same inode の in-place write として fd に truncate、exact write、fsync します。path replacement や
   live current を別 inode にする操作、container recreate は使いません。container-visible SHA は
   candidate SHA と一致しなければなりません。

9. **temporary candidate の cleanup。** acceptance 成功後は deploy user で temporary candidate を `rm` し、
   その後 `存在しない` ことを確認します。automatic rollback が成功した場合も同じ cleanup を rollback 完了後に
   実施します。

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

   rollback 自体が失敗した場合は SSH recovery を優先し、candidate cleanup で rollback evidence や復旧を
   妨げません。incident 対応終了後には protected candidate を残さず、上記の `rm` と不存在確認を実施します。
   secure-delete / shred 保証は要求せず、通常の `rm` で十分です。

10. **automatic rollback の完了条件。** validate failure、reload failure、post-reload acceptance failure
   を含む ANY FAILURE は rollback on validate failure / rollback on reload failure として扱います。
   disk 上の candidate を戻さず exit してはいけません。rollback は last-known-good を同じ inode に
   restore し、device、inode、uid、gid、owner、group、mode、HOST_SHA == ORIGINAL_SHA ==
   CONTAINER_SHA を確認します。その後 old config の caddy validate、必要な caddy reload、/healthz、
   /readyz、/api regression、/admin、/setup、:8080 unreachable、SSH を再確認します。rollback 自体が
   failure なら SSH を維持して STOP し、operator に escalate します。inode drift は path を置換せず
   STOP します。rollback success を確認してから temporary candidate cleanup を行います。

11. **fail-closed と既存 security contract。** GeoLite download / render / validate / update は operator
   側だけで行い、raw CSV、MaxMind credential、bcrypt input hash file、plaintext password を VPS に
   置きません。空 CIDR、default route、allow-all (0.0.0.0/0 / ::/0) へ fallback しません。
   /admin /setup は JP CIDR + Caddy Basic Auth + Mailer own auth、non-JP/unknown は challenge 前の
   404、/metrics は MAILER_MANAGEMENT_ALLOWED_CIDRS、/api/* /healthz /readyz は public、Caddy
   Authorization は Mailer upstream へ forwardしない、Mailer :8080 は host unpublished を維持します。
   #744 は edge hardening、#745 の fresh setup / real ACS send / UX dogfood は開始しません。
管理経路を SSH tunnel のみにする場合は、Caddy の host bind を `127.0.0.1` に変更し、
remote host の 80/443 を公開しません。public API も tunnel 経由だけになります。通常の
public API + private management 構成では、public 80/443 を firewall で運用し、management
CIDR の制限を Caddy と host/VPN の両方で確認します。

## Compose 検証と起動

profile は明示的に指定します。

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

`config --quiet` が失敗する場合は placeholder、必須の hostname、metrics CIDR、network の固定
address、生成済み Caddyfile を確認します。rendered config の `mailer` / `mailer-migrate` に tenant JSON
mount、`MAILER_TENANTS_PATH`、`MAIL_SERVICE_TOKEN*`、`MAILER_PROVIDER` がないことも確認
します。起動後の確認:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

curl -fsS https://MAILER_PUBLIC_HOSTNAME/healthz
curl -i https://MAILER_PUBLIC_HOSTNAME/readyz
```

tenant JSON と `MAIL_SERVICE_TOKEN*` のない fresh state でも migration は成功し、migration
後の `/readyz` は `503`（uninitialized）です。これは失敗ではなく setup 前の期待値です。
JP の approved source CIDR から Caddy Basic Auth と Mailer 自身の認証を通して `/setup` が
利用でき、非JP source は challenge 前に 404 です。`/metrics` は別途 operator CIDR と
metrics bearer を要求し、`mailer` の直接 `http://host:8080` は接続できません。

## Browser Setup

bootstrap token は container 内から一度だけ表示する transient protected file の値であり、
password / provider secret と同じく secret として扱います。値を shell history、log、Issue、
chat に貼りません。これは tenant token ではありません。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

`https://MAILER_PUBLIC_HOSTNAME/setup` を、生成済み Caddyfile の JP CIDR から開き、まず
Caddy Basic Auth、続いて Mailer 自身の bootstrap 認証、provider secret の file-based 登録、instance owner、sender、finalize を
既存の FirstRunSetup の順序で実行します。`/setup` は HTTPS が必要です。Caddy の `X-Forwarded-Proto` は
専用 proxy IP からのものだけを Mailer が信頼するため、Secure cookie と antiforgery の
HTTPS contract を維持できます。

finalize 後は `initialized_at` が不可逆の gate です。Mailer を再起動して `/readyz` が
ready になることを確認します。初期化済み instance では runtime が `/setup` を map せず、
古い bootstrap token file が残っていても `/setup` に戻りません。Admin は同じ
management route の `/admin` から利用します。

## 運用上の境界

- public consumer request は `https://MAILER_PUBLIC_HOSTNAME/api/...` を使います。
  backend の Docker name/port を consumer の public contract にしません。
- `/admin` と `/setup` は GeoLite2-derived JP CIDR と Caddy Basic Auth の両方を要求します。
  non-JP には Basic challenge 前に 404 を返します。これだけに依存せず、VPN/firewall/SSH
  tunnel と instance owner の認証も組み合わせます。Mailer application 単体で public Admin
  を安全にする構成ではありません。
- `/metrics` は Japan CIDR ではなく `MAILER_MANAGEMENT_ALLOWED_CIDRS` の operator boundary
  と Mailer metrics bearer を使います。
- `infra/deploy/compose.yml` の `MAILER_TENANTS_PATH`、`MAIL_SERVICE_TOKEN_*`、
  `MAILER_PROVIDER` は baseline の manual / v1 compatibility path のため残っています。
  ただし `compose.vps-dogfood.yml` は両 service からそれらを除去します。VPS managed-v2
  では `SQLite managed state` が product configuration authority、provider secret は
  protected file、bootstrap token は transient protected file です。`tenants.json` /
  `MAIL_SERVICE_TOKEN*` は VPS v2 reference deployment では不要です。
- Caddy の `caddy_data` / `caddy_config` named volume と Mailer の data volume は
  persistent deployment state です。Mailer の full instance backup は
  `MAILER_DATA_PATH/mailer.db`、canonical provider secret、
  `attachment-spool/committed` を停止点から取得します。Caddy volume、bootstrap token、
  logs、staging、external `/run/secrets/acs` compatibility mount は archive に混ぜません。
  詳細は [`backup-operations.md`](backup-operations.md)、[`restore-procedure.md`](restore-procedure.md)、
  [`restore-verification.md`](restore-verification.md) を参照してください。

## 停止

データを削除しない停止は次のコマンドです。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood down
```

`down -v` は Mailer DB と Caddy certificate state を削除し得るため、この source-stage runbook
では案内しません。
