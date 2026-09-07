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

GeoLite2 Country CSV は operator が別途取得して保管します。renderer は MaxMind へ接続せず、
download、license key、account ID を扱いません。次の3 CSVと、別の安全な経路で用意した
既生成 bcrypt hash file を渡してください。実 password の生成や実 hash の作成は source stage
では行いません。

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

1. **real GeoLite input から candidate を生成する。** reviewed source checkout の root から、
   self-test fixture ではなく、operator が安全に保管した現行 GeoLite2 Country の IPv4 blocks、
   IPv6 blocks、locations-en CSV と既生成 bcrypt hash file を使います。production の
   `/srv/platform/edge/Caddyfile` はこの段階では変更しません。

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

   `caddy-admin` は非秘密の例です。実運用では deployment 固有の username を使います。render
   summary は hash / password を含まない value-free record として保護して保存します。

2. **candidate の件数・サイズ・digest を記録する。** renderer の summary と candidate 自体を
   照合し、change record に `IPv4 CIDR count`、`IPv6 CIDR count`、`bytes`、`SHA-256` を記録します。
   次の追加記録も candidate の実体を確認するためのもので、secret value は表示しません。

   ```bash
   {
     grep -E '^(IPv4 CIDR count|IPv6 CIDR count|output bytes|SHA-256):' "${render_record}"
     printf 'candidate bytes: %s\n' "$(stat -c '%s' "${candidate}")"
     printf 'candidate SHA-256: %s\n' "$(sha256sum "${candidate}" | awk '{print $1}')"
   } >>"${render_record}"
   ```

   count 0、想定外の件数、bytes / SHA-256 の不一致、または summary の記録不能は STOP です。
   raw GeoLite CSV、password、bcrypt hash を change record にコピーしません。

3. **production Caddyfile を変更する前に、pinned / running Caddy 2.10.2 で validate する。**
   running `proxy` の version と image digest を記録し、repository の pin と一致することを確認します。
   値はこの profile の pin に固定し、digest のない `caddy:2.10.2` や `latest` に置き換えません。

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

   上記の hostname / metrics CIDR は文書用 placeholder です。production の実値は表示せずに
   operator の configuration から渡します。validate failure は必ず STOP とし、candidate を
   production に置き換えたり、allow-all CIDR へ fallback したりしません。

4. **別の Human approval 後にだけ live mutation を行う。** Source Stage の approval、renderer
   self-test、CI pass、または step 3 の validate pass は、live mutation の Human approval では
   ありません。approval が change record に存在しない、対象 digest が変わった、または SSH
   rollback owner が不在なら STOP します。

5. **current を timestamp + SHA-256 付きの last-known-good として退避する。** approval 後、
   `/srv/platform/edge/Caddyfile` が symlink でなく、root ownership とこの profile の expected
   mode `0600` を持つことを確認します。異なる ownership / mode はその場で直して進まず、まず
   operator review を行います。current の SHA-256 を計算し、同じ filesystem の専用 directory
   に `Caddyfile.<UTC timestamp>.sha256-<64 hex>` の名前で退避し、backup の hash が current と
   一致することを記録します。

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

6. **candidate を atomic replace する。** candidate と current が同じ filesystem にあり、candidate
   も `root:root` / expected mode `0600` であることを確認してから、candidate を同じ directory 内で
   rename します。candidate を先に root ownership / mode に整え、replace 後にも確認します。

   ```bash
   test -f "${candidate}" && test ! -L "${candidate}"
   chown root:root "${candidate}"
   chmod "${expected_mode}" "${candidate}"
   test "$(stat -c '%d' "${candidate}")" = "$(stat -c '%d' "${current}")"
   mv -f -- "${candidate}" "${current}"
   test "$(stat -c '%U:%G %a' "${current}")" = 'root:root 600'
   ```

   directory を跨ぐ copy、編集、truncate、allow-all の一時置換は行いません。`mv` が失敗したら
   STOP して current を変更しません。

7. **container restart / recreate ではなく `caddy reload` を使う。** current の replace 後、
   Caddy が読む mounted path に対して次だけを実行します。`docker compose restart`、`up -d
   --force-recreate`、container stop/start はこの apply lifecycle では使いません。

   ```bash
   docker exec proxy caddy reload \
     --config /etc/caddy/Caddyfile --adapter caddyfile
   ```

8. **reload 後の acceptance をすべて確認する。** 既存の value-free acceptance record に、
   次を各々記録します。`/admin` と `/setup` は JP source から Caddy Basic Auth の fail/success
   boundary と Mailer 自身の auth boundary を確認し、non-JP source は challenge 前の 404 を確認
   します。`/api` regression は実送信を伴わない approved regression check を使います。

   - `/healthz`
   - `/readyz`
   - `/api` regression
   - `/admin`
   - `/setup`
   - public host `:8080` が unreachable（Mailer backend の port を公開していない）
   - SSH login / rollback session

9. **failure 時は old Caddyfile を restore → validate → reload → regression verification の順で
   戻す。** reload failure、acceptance failure、JP allow-list の誤生成、ownership / mode drift の
   いずれでも、手順を省略して再編集しません。同じ pinned Caddy 2.10.2 で last-known-good を
   validate し、pass 後に `caddy reload` し、step 8 の全 acceptance と `/api` regression を再確認
   します。restore、validate、reload、regression のどこかが失敗したら SSH を維持して STOP し、
   operator に escalate します。

   restore は current path を直接編集せず、last-known-good を同じ directory の temporary file に
   root ownership / expected mode で置いてから atomic rename します。

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

   特に JP allow-list を誤生成して日本から `/admin` / `/setup` が全拒否になっても、公開経路で
   直そうとせず、事前に確保した SSH session から last-known-good を restore、validate、reload
   できます。SSH rollback が確認できない apply は開始しません。

10. **GeoLite download / render / validate / update が失敗した場合の fail-closed。** その時点の
    current last-known-good production Caddyfile を維持します。空の candidate、空 CIDR、default
    route、allow-all (`0.0.0.0/0` / `::/0`) へ fallback せず、失敗を記録して STOP します。

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
