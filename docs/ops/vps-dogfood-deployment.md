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
| `/admin`、`/admin/*`、`/setup`、`/setup/*` | IPdeny aggregated JP CIDR **かつ** Caddy Basic Auth | Caddy の `Authorization` は除去し、Mailer 自身の Admin/Setup 認証を維持 |
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
- Caddy の `/admin` と `/setup` は、renderer が IPdeny aggregated JP zone から導出した JP
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

`infra/deploy` で `.env` を作成します。Caddyfile は単純にコピーせず、IPdeny zone と hash を
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

IPdeny の aggregated JP zone は operator または `agent-dev01` 側で HTTPS 取得します。
renderer 自身は download を行わず、zone の入力・parse・validate・normalize・collapse と
Caddy candidate の生成だけを offline で行います。canonical source は次の2つです。

- IPv4: `https://www.ipdeny.com/ipblocks/data/aggregated/jp-aggregated.zone`
- IPv6: `https://www.ipdeny.com/ipv6/ipaddresses/aggregated/jp-aggregated.zone`

zone は operator / `agent-dev01` の secure runtime workspace にだけ置き、VPS や Git へ
転送・commit しません。取得時は source URL、download timestamp、input bytes、input SHA-256 を
provenance metadata として記録します。HTTP `Last-Modified` が返る場合は
記録してよいものとし、header がないことだけでは fail にしません。

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

`caddy-admin` は非秘密の例です。deploymentごとに選んだ username を渡します。空白、改行、
Caddyfile token injection になる文字を含む username は renderer が拒否します。各 zone は one CIDR per line
(1行1 CIDR) とし、空白だけの行だけを無視します。コメント、extra token、invalid CIDR、wrong address family、
IPv4/IPv6 の `/0`、missing/empty zone、または片方の CIDR 0件は fail-closed
で停止します。重複削除・隣接 CIDR collapse と数値順の deterministic output を行い、hash の
値や zone contents はログ・summary に表示しません。

`Caddyfile.vps-dogfood` は `.gitignore` 済みの runtime artifact です。VPS へ渡してよいのは、
別途承認された generated Caddy candidate と、値を含まない IPv4/IPv6 CIDR count、bytes、
SHA-256 だけです。実 password の生成や実 hash の作成はこの source stage では行いません。

### Caddy Basic Auth の credential boundary

実運用では、Caddy Basic Auth password は password manager または承認済みの CSPRNG で十分に
強いランダム値を新規生成します。この rework では実 password や実 hash を生成しません。
生成した Caddy Basic Auth password は Mailer Admin password、Setup bootstrap token と別の
資格情報にし、どの値も再利用しません。IPdeny zone の取得環境・provenance metadata とも
分離して扱います。

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
| A. OPERATOR / agent-dev01 | IPdeny zone inputs、provenance metadata、bcrypt hash input、renderer、operator-side candidate、CIDR counts、bytes、SHA-256 | raw IPdeny zone と bcrypt input hash はここにだけ残す。VPSへ送らない。 |
| B. REMOTE VPS READ-ONLY | deploy user の SSH 内で Compose label resolution、container inspect、image/version/mount確認、read-only stat、stdin pre-validation | remote VPS read-only body の docker ps、docker inspect、docker exec だけ。production mutation はしない。 |
| C. REMOTE VPS PRIVILEGED LIVE MUTATION | Human approval 後の backup、same-inode write、SHA guards、validate、reload、rollback | interactive sudo approval 後の explicit Bash transaction 内だけ。sudoers、SSH、root login は変更しない。 |

Pre-validation までは candidate は operator に保持し、candidate bytes を SSH stdin stream で remote process に渡します。
この段階は production mutation=false、candidate file persisted on VPS=false、sudo不要です。Human approval 後の
live mutation では、generated Caddyfile candidate only を deploy user の temporary location へ転送します。
PERSISTENT_VPS_STAGING_REQUIRED=false、TEMPORARY_VPS_CANDIDATE_REQUIRED=true です。raw IPdeny zone data、
bcrypt input hash file、plaintext password は VPS へ転送しません。IPdeny raw data transferred=false、
bcrypt input file transferred=false です。

Fresh topology は Compose project=amane-platform-edge、service=proxy、current
/srv/platform/edge/Caddyfile、container path=/etc/caddy/Caddyfile、single-file read-only bind mount、
mount source=/srv/platform/edge/Caddyfile、destination=/etc/caddy/Caddyfile、RW=false、
pinned Caddy 2.10.2 です。observed container name は参考値で、毎回 label から exactly one に
resolve します。Fresh baseline の root:root / 0644 は参考値で、0644 is Fresh baseline only です。

1. **OPERATOR / agent-dev01 で candidate を生成する。** raw IPdeny zone、bcrypt input hash file、
   plaintext password は VPS に置きません。production Caddyfile はこの段階で
   変更しません。

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
   転送してよいのは generated Caddyfile candidate only です。IPdeny raw zone、provenance metadata、
   bcrypt input hash file、plaintext Basic Auth password、Mailer Admin password、Setup bootstrap token
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

6. **PHASE 2 — candidate transfer / SHA確認後に standalone interactive SSH TTY を開く。** live mutation 開始前に、
   operator workstation で transaction HEREDOC を SSH の stdin に接続せず、remote deploy shell 用の TTY を
   開きます。これは独立した interactive SSH phase です。

   ~~~bash
   # Run on operator workstation after Phase 1 candidate transfer and verification.
   # This is a standalone TTY; do not append a HEREDOC to this ssh command.
   ssh -t "$VPS_ALIAS"
   ~~~

   ここで Human は remote deploy shell に入ります。interactive shell に入る前に必要な
   `candidate_remote`、`candidate_sha256`、`candidate_bytes` は non-secret path / metadata です。shell injection を
   起こさないよう、operator workstation で Bash の `%q` を使って shell-quoted assignment として出力し、remote
   Bash prompt へその3行だけをそのまま貼り付けます。candidate bytes、sudo password、secret は貼り付けません。

   ~~~bash
   # Run on operator workstation before opening the TTY; metadata only.
   case "$candidate_sha256" in
     ''|*[!0-9a-fA-F]*) echo 'invalid candidate SHA-256' >&2; exit 1 ;;
   esac
   case "$candidate_bytes" in
     ''|*[!0-9]*) echo 'invalid candidate byte count' >&2; exit 1 ;;
   esac
   printf 'candidate_remote=%q\ncandidate_sha256=%q\ncandidate_bytes=%q\n' \
     "$candidate_remote" "$candidate_sha256" "$candidate_bytes"
   ~~~

   `%q` の出力は remote の Bash prompt で編集せずに貼り付けます。remote shell では `candidate_remote` を
   `$HOME/.amane-caddy-744.*` の deploy-owned regular file、not symlink、mode 0600 として再確認し、値を
   `sudo` の password や stdin として扱いません。Phase 2 の TTY は transaction stdin ではありません。

   remote shell 内で次を実行します。

   ~~~text
   sudo -v = interactive authentication
   sudo -n true = post-authentication credential-cache verification only
   sudo -n bash = already-authenticated transaction execution only
   ~~~

   `sudo -v` は interactive authentication です。Human は VPS terminal の sudo prompt に password を直接入力
   します。sudo password is entered only at the interactive sudo prompt and is never supplied by script/stdin. sudo
   password は chat、Issue、log、script、environment variable、candidate stream のいずれにも載せません。
   `sudo -n` は `sudo -v` 成功後だけに使用し、最初の認証には使いません。

#### Privilege boundary / 権限境界

| context | 許可される操作 |
| --- | --- |
| deploy / non-privileged | temporary candidate receive、candidate の size / SHA-256 / regular-file / owner / mode verification、現在許可されている Docker read/inspect、public/read-only checks、`sudo -v` invocation |
| root / sudo Bash | Human approval 後だけの last-known-good backup、production Caddyfile same-inode write、metadata restoration、host/container SHA guards、Caddy validate/reload、rollback |

sudoers、SSH config、root login、Docker topology、Caddy container recreation、firewall は変更しません。

7. **PHASE 3 / PHASE 4 — REMOTE VPS PRIVILEGED LIVE MUTATION で preflight / backup を行う。** Phase 2 の
   `sudo -v` と Phase 3 の `sudo -n true` が同じ remote TTY/session で成功した後にだけ transaction を開始します。
   Phase 3 の `sudo -n true` は credential cache の有効性を fail-closed に確認するだけで、認証用途ではありません。
   `sudo -n bash` は already-authenticated transaction execution only です。deploy は read-only user なので、
   root-owned Caddyfile への unprivileged write は禁止です。backup directory、root-owned last-known-good backup、
   current write、chown、chmod restoration、reload、rollback は explicit Bash transaction 内だけで行います。
   transaction uses Bash ERR trap / pipefail semantics; do not execute it through /bin/sh. Fresh の root:root / 0644 を
   再設定せず、live に再取得した owner、group、mode、device、inode、uid、gid、bytes、SHA-256 を preserve します。
   #744 は mode hardening をしません。candidate は `$candidate_remote` から読み、transaction script の stdin や
   sudo password の stdin には載せません。

   Phase 2 の remote shell で、次の Phase 3 check を実行します。失敗したら production mutation は開始せず、
   temporary candidate を cleanup して不存在を確認し、STOP します。

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

   `sudo -v` failure、`sudo -n true` failure、または後続の `sudo -n bash` start failure は、production mutation
   開始前なら rollback 不要です。いずれも temporary candidate を `rm -f -- "$candidate_remote"` で cleanup し、
   `test ! -e` と `test ! -L` で不存在を確認して STOP します。`sudo -n` は prompt を出さないため、失敗時に
   password を stdin へ流して再試行してはいけません。

8. **transaction / failure handler を先に準備する。** backup と original SHA の検証後にだけ
   mutation_started=true とし、初期値は mutation_started=false、rollback_in_progress=false とします。
   candidate short read / short write、Python exception、fsync failure、host SHA mismatch、
   container SHA mismatch、inode drift、owner/mode drift、post-write caddy validate failure、
   caddy reload failure、post-reload acceptance failure の ANY FAILURE は automatic rollback 対象です。
   trap ERR は rollback_in_progress guard で再帰を防ぎ、rollback_current_in_place を一度だけ呼びます。

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
       mailer_ids="$(docker ps --quiet --filter label=com.docker.compose.project=$project \
         --filter label=com.docker.compose.service=mailer --filter status=running)"
       mailer_count="$(printf '%s\n' "$mailer_ids" | awk 'NF {n++} END {print n+0}')"
       test "$mailer_count" -eq 1
       mailer_container="$(printf '%s\n' "$mailer_ids" | awk 'NF {print; exit}')"
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

   production current は same inode の in-place write として fd に truncate、exact write、fsync します。path replacement や
   live current を別 inode にする操作、container recreate は使いません。container-visible SHA は
   candidate SHA と一致しなければなりません。

   **Approved value-free acceptance contract / acceptance responsibility.** `run_approved_value_free_acceptance_checks`
   は定義後にだけ呼び出し、`candidate` と `rollback` の2つの明示的な mode を受け付けます。`candidate` mode は
   `/healthz=200`、`/readyz=200`、固定 nonexistent UUID への `GET /api/mail-requests/<uuid>` が Mailer の
   `401` と `"code":"UNAUTHORIZED"` を返し、Basic challenge ではないこと、VPS self-request の non-JP/unknown
   `/admin` と `/setup` が Basic challenge 前の `404` であること、Mailer host-published `8080/tcp` が
   `none/null` であることを確認します。メール送信を起こす `POST`、secret、API key は使いません。

   `rollback` mode は固定した candidate status を再利用しません。mutation 前に取得した
   `baseline_healthz_state`、`baseline_readyz_state`、`baseline_api_state`、`baseline_admin_state`、
   `baseline_setup_state`（status と response-body SHA-256）を引数として受け、old config reload 後に同じ
   state を復元できたことを確認します。未知の mode は fail closed します。したがって
   `candidate mode` と `rollback mode` の `/admin` / `/setup` の責務は混同されません。

   REMOTE VPS transaction が証明するのは、VPS から Caddy を通った public HTTPS の liveness/readiness、Mailer
   API の no-send unauthorized contract、VPS self-request に対する non-JP/unknown management boundary、および
   Docker runtime の `Mailer :8080` host-publish 不在だけです。root helper は SSH、Windows/browser、外部 JP client
   または Internet からの `:8080` 到達不能を実行・確認したとは表示しません。

   Candidate acceptance の外部責務は Windows/operator 側にあります。JP source から `/admin` と `/setup` を確認し、
   Caddy Basic Auth boundary が先に存在し、認証されていない request が Mailer へ素通りしないことを確認します。
   non-JP/unknown source は Basic challenge 前に `404` であること、外部から Mailer `:8080` に到達できないことも
   Windows/operator 側で確認します。rollback failure の entry point は ERR trap の
   `rollback_current_in_place` です。SSH recovery は、現在開いている standalone SSH TTY を維持し、必要なら
   operator/Windows から別 SSH を確認する責務であり、root transaction 内部の helper が確認済みと偽装しません。

9. **temporary candidate の cleanup。** Phase 2 で定義した cleanup function を、同じ remote interactive
   TTY/session 内で acceptance 成功後、または automatic rollback 成功後に呼び出します。deploy user として
   temporary candidate を `rm` し、その後 `存在しない` ことを確認します。sudo failure の場合も production
   mutation 開始前に同じ cleanup を実施します。

   ~~~bash
   # Run in the same remote interactive shell after transaction success or rollback success.
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

11. **fail-closed と既存 security contract。** IPdeny download / render / validate / update は operator
   側だけで行い、raw IPdeny zone、provenance metadata、bcrypt input hash file、plaintext password を VPS に
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
- `/admin` と `/setup` は IPdeny-derived JP CIDR と Caddy Basic Auth の両方を要求します。
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
