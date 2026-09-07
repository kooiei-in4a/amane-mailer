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

ここは production の live edge を更新するための runbook です。以下の実 VPS command は手順の
記述だけであり、この rework 中には実行しません。Source Stage の build / validation の承認は
live mutation の承認を兼ねません。production を変更する前に、別の Human approval（change ID、
対象 hostname、candidate digest、実行者、rollback owner を含む）を記録します。

Fresh-confirmed な実VPS topology は次のとおりです。`amane-platform-edge-proxy-1` は確認時に
観測された名前ですが、shared host の runbook では Docker CLI の selector として名前を
hardcode せず、Compose label から毎回1件に解決します。

| item | 正本 |
| --- | --- |
| Compose project / service | `amane-platform-edge` / `proxy` |
| edge working directory / compose | `/srv/platform/edge` / `/srv/platform/edge/compose.yml` |
| current Caddyfile | `/srv/platform/edge/Caddyfile` |
| observed running container name | `amane-platform-edge-proxy-1`（参考値。label resolutionを使用） |
| current ownership / mode baseline | `root:root` / `0644`（Fresh確認値。liveでは再取得して保存・維持） |
| container path / mount | `/etc/caddy/Caddyfile`、single-file read-only bind mount |
| mount source / destination | `/srv/platform/edge/Caddyfile` → `/etc/caddy/Caddyfile`、RW=false |
| pinned image | `caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d` |

1. **operator側で real GeoLite input から candidate を生成する。** reviewed source checkout の
   root と operator / `agent-dev01` の secure input path で、self-test fixture ではない現行
   GeoLite2 Country の IPv4 blocks、IPv6 blocks、locations-en CSV、既生成 bcrypt hash file を
   使います。raw CSV、MaxMind credential、bcrypt input hash file、plaintext password は VPS に
   置きません。production の `/srv/platform/edge/Caddyfile` はこの段階では変更しません。

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

   `caddy-admin` は非秘密の例です。実運用では deployment 固有の username を使います。render
   summary は hash / password を含まない value-free record として operator 側で保護します。

2. **candidate の件数・サイズ・digest を operator 側で記録する。** renderer の summary と
   candidate 自体を照合し、change record に `IPv4 CIDR count`、`IPv6 CIDR count`、`bytes`、
   `SHA-256` を記録します。count 0、想定外の件数、bytes / SHA-256 の不一致、summary の記録
   不能は STOP です。raw GeoLite CSV、password、bcrypt hash を change record にコピーしません。

   ```bash
   candidate_bytes="$(stat -c '%s' "${candidate}")"
   candidate_sha256="$(sha256sum "${candidate}" | awk '{print $1}')"
   {
     grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "${render_record}"
     printf 'candidate bytes: %s\n' "${candidate_bytes}"
     printf 'candidate SHA-256: %s\n' "${candidate_sha256}"
   } >>"${render_record}"
   ```

3. **production Caddyfile を変更する前に、labelで解決した running Caddy 2.10.2 へ stdin で
   validate する。** candidate はまだ VPS へ転送せず、実際に running な対象 container の Caddy
   binary へ stdin で渡します。0件または2件以上なら STOP です。

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

   # resolve/inspectはVPS側のSSH sessionで行い、candidateのbytesだけをoperator側からstdinで送る。
   if ! ssh vps docker exec -i "${edge_container}" \
     caddy validate --config - --adapter caddyfile <"${candidate}"; then
     echo 'STOP: actual running Caddy rejected candidate; production Caddyfile was not changed.' >&2
     exit 1
   fi
   ```

   `caddy validate --config -` が failure なら必ず STOP し、candidate を転送せず、production を
   変更せず、allow-all CIDR へ fallback しません。stdin validate により GeoLite CSV と bcrypt
   input hash file を VPS へ置かずに actual Caddy binary を検証できます。

4. **別の Human approval 後にだけ live mutation を行う。** Source Stage の approval、renderer
   self-test、CI pass、または step 3 の actual Caddy validate pass は、live mutation の Human
   approval ではありません。step 1/2 の candidate digest、count、bytes、step 3 の validate
   PASS、対象 hostname、実行者、SSH rollback owner を確認した別 approval を記録します。approval
   がない、digest が変わった、または rollback owner / SSH session がない場合は STOP します。

5. **live preflightで current の metadataをFresh取得し、last-known-goodを作る。** approval 後に
   `/srv/platform/edge/Caddyfile` が regular file で symlink でなく、上記 mount source と一致する
   ことを確認します。Fresh-confirmed baseline は `root:root` / `0644` ですが、これは事前に観測した
   値であり、`0644` への再設定を意味しません。live preflightでは owner、group、mode、inode、
   device、SHA-256、bytes を取得して記録し、その値をそのまま維持します。異なる値なら STOP して
   別Human approvalを得ます。mode変更を #744 live edge apply に混ぜません。

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

   backup は current とは別 inode でよいですが、記録する metadata は original current の
   owner、group、mode、inode、device、SHA-256、bytes です。`install` は last-known-good backup
   の作成にだけ使い、mounted current path を別 inode のファイルで置き換える用途には使いません。

6. **Human approval後に generated Caddy candidate だけを protected stagingへ転送する。** operator
   側から `/srv/platform/edge/staging/<change-id>/Caddyfile` へ candidate だけを転送します。staging
   は public、Git managed、Compose config ではなく、generated Caddy 以外を置かない protected
   directory とします。raw CSV、MaxMind credential、bcrypt input hash file、plaintext password は
   転送しません。転送後に size と SHA-256 を operator 側の記録と照合し、不一致なら STOP します。

   ```bash
   # operator側。Human approval後にだけ実行する。
   scp -- "${candidate}" \
     "vps:/srv/platform/edge/staging/${change_id}/Caddyfile"

   # VPS側。既存の protected staging directoryを使い、candidateだけを確認する。
   staging_candidate="/srv/platform/edge/staging/${change_id}/Caddyfile"
   test -f "${staging_candidate}" && test ! -L "${staging_candidate}"
   chown "${original_uid}:${original_gid}" "${staging_candidate}"
   chmod "${original_mode}" "${staging_candidate}"
   test "$(stat -c '%s' "${staging_candidate}")" = "${candidate_bytes}"
   test "$(sha256sum "${staging_candidate}" | awk '{print $1}')" = "${candidate_sha256}"
   ```

7. **single-file bind mountの同一inodeを保ったまま、currentのcontentsだけをin-place更新する。**
   candidate を current path の別 inode として置き換えません。単純な `cp candidate current` に
   依存せず、candidate 全 bytes を read し、current を write mode で開き、original device / inode /
   owner / group / mode を guard してから truncate、exact bytes write、flush、`os.fsync()`、close
   します。current の device または inode が変わっていれば write せず STOP します。

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
   # HOST_CADDY_SHA == CANDIDATE_SHA == CONTAINER_CADDY_SHA を満たさない限り STOP。
   ```

   上記の host current と container `/etc/caddy/Caddyfile` の SHA-256 equality を確認するまで
   reloadしません。single-file read-only bind mount でも container-visible bytes が candidate と
   一致していることを必須 guard にします。

8. **container-visible bytes の確認後、actual containerで validate、続いて `caddy reload` を行う。**
   validate failure は STOP して reload せず、container restart / recreate もしません。この lifecycle
   では `docker restart`、`docker compose restart`、`docker stop/start`、`up -d --force-recreate`、
   container recreate を使用しません。

   ```bash
   if ! docker exec "${edge_container}" \
     caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile; then
     echo 'STOP: mounted candidate validation failed; do not reload.' >&2
     exit 1
   fi
   docker exec "${edge_container}" \
     caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

9. **reload 後の acceptance をすべて確認する。** 既存の value-free acceptance record に、次を
   各々記録します。`/admin` と `/setup` は JP source から Caddy Basic Auth の fail/success boundary
   と Mailer 自身の auth boundary を確認し、non-JP source は challenge 前の 404 を確認します。
   `/api` regression は実送信を伴わない approved regression check を使います。

   - `/healthz`
   - `/readyz`
   - `/api` regression
   - `/admin`
   - `/setup`
   - public host `:8080` が unreachable（Mailer backend の port を公開していない）
   - SSH login / rollback session

10. **failure 時は old Caddyfile を same inode のまま restore → validate → reload → regression
    verification の順で戻す。** reload failure、acceptance failure、JP allow-list の誤生成、ownership
    / mode drift のいずれでも、手順を省略して再編集しません。事前に確保した SSH session から
    last-known-good の bytes を current へ in-place restore し、original owner / group / mode も
    同じ inode 上で復元します。current の device / inode が original と異なる場合は path を置換せず
    STOP して escalate します。

    restore後も必ず `device unchanged`、`inode unchanged`、`owner restored`、`group restored`、
    `mode restored`、`HOST_CADDY_SHA == BACKUP_SHA == CONTAINER_CADDY_SHA` を確認します。その後、
    同じ pinned Caddy 2.10.2 で `caddy validate`、pass 後に `caddy reload`、step 9 の全 acceptance
    と `/api` regression を再確認します。restore、validate、reload、regression のどこかが失敗
    したら SSH を維持して STOP し、operator に escalate します。

    ```bash
    # current pathが同じ regular file / original inodeであることを先に確認する。
    test -f "${current}" && test ! -L "${current}"
    test "$(stat -c '%d' "${current}")" = "${original_device}"
    test "$(stat -c '%i' "${current}")" = "${original_inode}"

    # metadata driftがあれば、同じinodeのままoriginal metadataを復元する。
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
    # ここでstep 9の全acceptanceと/api regressionを再実行する。
    ```

    特に JP allow-list を誤生成して日本から `/admin` / `/setup` が全拒否になっても、公開経路で
   直そうとせず、事前に確保した SSH session から last-known-good を same inode に restore、
   validate、reload できます。SSH rollback が確認できない apply は開始しません。

11. **GeoLite download / render / validate / update が失敗した場合の fail-closed。** GeoLite の取得は
    operator側だけで行い、失敗時はその時点の current last-known-good production Caddyfile を維持
    します。raw CSV、MaxMind credential、bcrypt input hash file を VPSへ置かず、空の candidate、
    空 CIDR、default route、allow-all (`0.0.0.0/0` / `::/0`) へ fallback せず、失敗を記録して STOP
    します。

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
