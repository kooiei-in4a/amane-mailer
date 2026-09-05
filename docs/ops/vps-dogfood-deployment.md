[English](vps-dogfood-deployment.en.md)

# VPS dogfood deployment（PR1）

この runbook は、Issue #733 の PR1 reference deployment です。Caddy が host の
80/443 だけを受け、Mailer は Docker network 内の HTTP backend として動きます。
Mailer の 8080 は host に publish しません。

この PR1 の範囲は deployment security boundary と fresh setup の経路確認です。ACS の
実送信、公式 smoke client、複数 Sender / API Key dogfood、revoke、restart dogfood、
backup / restore は含みません。

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

`compose.vps-dogfood.yml` は base の `mailer` service を次のように overlay します。

- `mailer` は `internal` と専用 `vps_proxy` だけに参加します。base の consumer 用
  `mailer` network はこの profile では置き換えられ、proxy bypass を残しません。
- `proxy` と `mailer` は専用 network 上の固定 IPv4 を使います。Mailer が信頼する
  forwarded header source は proxy の固定 IPv4 一つだけです。trusted network 全体や
  `0.0.0.0/0` は設定しません。
- 専用 network は ACS と Caddy ACME の outbound 通信が必要なため Docker の
  `internal: true` にはしていません。参加 service は `proxy` と `mailer` に限定し、
  host port は proxy だけにします。
- Caddy の `/admin`、`/setup`、`/metrics` は `MAILER_MANAGEMENT_ALLOWED_CIDRS` の
  client source IP/CIDR からだけ通します。それ以外の管理経路は edge で 404 です。
  `/api/*`、`/healthz`、`/readyz` だけを public path として proxy します。

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` は operator の client IP ではありません。Mailer が
proxy から受ける request の `Connection.LocalIpAddress`（この profile では Mailer の
専用 network 側アドレス）を許可するための設定です。operator CIDR は Caddy 側の
`remote_ip` matcher で別に制限します。

## 初回準備

Docker Engine と Compose plugin（`!override` をサポートするバージョン）、公開 DNS、host firewall の設定は事前に用意します。
Mailer は Docker や firewall、DNS、TLS account を自動設定しません。

`infra/deploy` で次を行います。

```bash
cp .env.example .env
cp Caddyfile.vps-dogfood.example Caddyfile.vps-dogfood
```

`.env` の placeholder を deploy host の値へ置き換えます。少なくとも次を確認します。

- `MAILER_IMAGE_REPOSITORY` と `MAILER_IMAGE_TAG` は公開済みの検証済み Mailer image。
- `MAILER_TENANTS_HOST_PATH` が実 tenant JSON を指し、token 値は `.env` にだけ置かれる。
- `MAILER_METRICS_BEARER_TOKEN` は production 用の private value。
- `MAILER_PUBLIC_HOSTNAME` は実際の DNS name。
- `MAILER_MANAGEMENT_ALLOWED_CIDRS` は VPN / firewall で決めた operator source IP/CIDR。
  `.env.example` の `192.0.2.0/24` は文書用 TEST-NET であり、そのまま使用しません。
  複数値は空白区切りで、例は `"192.0.2.0/24 2001:db8:1234::/48"` です。
- `MAILER_VPS_PROXY_NETWORK_SUBNET` と固定 IPv4 が host 上の既存 network と衝突しない。
  変更する場合は subnet、proxy address、Mailer address の関係を保ちます。

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
  --profile vps-dogfood up -d
```

`config --quiet` が失敗する場合は placeholder、必須の hostname/CIDR、network の固定
address を確認します。起動後の確認:

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

curl -fsS https://MAILER_PUBLIC_HOSTNAME/healthz
curl -i https://MAILER_PUBLIC_HOSTNAME/readyz
```

fresh state では migration 後も `/readyz` は `503`（uninitialized）です。これは失敗では
なく setup 前の期待値です。`mailer` の直接 `http://host:8080` は接続できません。

## Browser Setup

bootstrap token は container 内から一度だけ表示し、password / ACS secret / token と同じ
く secret として扱います。値を shell history、log、Issue、chat に貼りません。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

`https://MAILER_PUBLIC_HOSTNAME/setup` を、Caddy に設定した operator network から開き、
bootstrap 認証、provider、instance owner、sender、finalize を既存の FirstRunSetup の
順序で実行します。`/setup` は HTTPS が必要です。Caddy の `X-Forwarded-Proto` は
専用 proxy IP からのものだけを Mailer が信頼するため、Secure cookie と antiforgery の
HTTPS contract を維持できます。

finalize 後は `initialized_at` が不可逆の gate です。Mailer を再起動して `/readyz` が
ready になることを確認します。初期化済み instance では runtime が `/setup` を map せず、
古い bootstrap token file が残っていても `/setup` に戻りません。Admin は同じ
management route の `/admin` から利用します。

## 運用上の境界

- public consumer request は `https://MAILER_PUBLIC_HOSTNAME/api/...` を使います。
  backend の Docker name/port を consumer の public contract にしません。
- `/admin` と `/setup` は Caddy の CIDR制限だけに依存せず、VPN/firewall/SSH tunnel と
  instance owner の認証を組み合わせます。Mailer application 単体で public Admin を
  安全にする構成ではありません。
- `/metrics` も management path として扱います。metrics bearer が設定されていても、
  edge restriction を省略しません。
- `infra/deploy/compose.yml` の `MAILER_TENANTS_PATH`、`MAIL_SERVICE_TOKEN_*`、
  `MAILER_PROVIDER` は manual / fresh compatibility のため残っています。managed v2
  初期化後の provider、Admin、Sender、API key の正本は SQLite です。二つの設定を v2 の
  source-of-truth として競合させないでください。legacy cleanup はこの PR1 の対象外です。
- Caddy の `caddy_data` / `caddy_config` named volume と Mailer の data volume は
  persistent deployment state です。backup / restore 手順の全面更新は PR3 の対象であり、
  この profile は volume を削除するコマンドを提供しません。

## 停止

データを削除しない停止は次のコマンドです。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood down
```

`down -v` は Mailer DB と Caddy certificate state を削除し得るため、この PR1 runbook
では案内しません。
