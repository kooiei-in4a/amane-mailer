[English](local-mailer-docker-runbook-bash.en.md) | [Windows PowerShell 版](local-mailer-docker-runbook.md)

# Linux/macOS 向け local Mailer + Mailpit runbook

Linux / macOS の bash と curl で、ローカル Docker の Mailer + Mailpit を起動し、
health / ready、正常 POST、Mailpit 到着、冪等再送、conflict、Admin UI を確認する手順です。
Windows PowerShell で同じローカル compose を確認する場合は
[ローカル Mailer Docker runbook](local-mailer-docker-runbook.md) を使ってください。

この runbook は Mailpit によるローカル smoke に絞ります。ACS 実送信や Dead Letter の追加確認は
Windows PowerShell 版 runbook の該当節、または deploy / release smoke runbook を参照してください。

## PowerShell 版との主な差分

- この手順は Linux / macOS の `bash` と `curl` を前提にします。
- `payload_hash` は `sha256sum`、`shasum -a 256`、`openssl` のいずれかで計算します。
- JSON の確認に `jq` は必須ではありません。インストール済みならレスポンス整形に使えます。
- Linux / macOS の fresh checkout では Docker の bind mount 作成権限に注意が必要です。通常は compose の
  `data-init` が `data/mailer` を準備するため、追加操作は不要です。
- Admin UI の環境変数は `export` で設定します。`AMANE_ADMIN_ALLOW_HTTP=true` と
  `AMANE_ADMIN_PII_LIST_MODE=visible` はローカル確認専用です。
- ACS 実送信 / Dead Letter の追加確認は Windows PowerShell 版、deploy runbook、release smoke runbook を参照します。

## 前提

- Docker Desktop または Docker Engine が起動していること。
- `docker compose` plugin が使えること。
- コマンドはリポジトリ root で実行すること。
- `bash`、`curl`、`awk`、`grep`、`tail`、`uuidgen` が使えること。
- `sha256sum`、`shasum`、`openssl` のいずれかが使えること。
- 既定の host port `5280`（Mailer）と `8025`（Mailpit）が空いていること。

`jq` は任意です。この runbook のコマンドは `jq` なしで動きます。
Debian / Ubuntu で `uuidgen` が無い場合は `sudo apt install uuid-runtime` で追加できます。

## 1. 共通変数を設定する

```bash
set -Eeuo pipefail
set +x

COMPOSE_FILE="infra/docker/docker-compose.local.yml"
MAILER_URL="http://127.0.0.1:5280"
MAILPIT_URL="http://127.0.0.1:8025"

TENANT_ID="00000000-0000-0000-0000-000000000101"
SOURCE_SERVICE="example-service"
TO_EMAIL="smoke@example.com"
PURPOSE="local-docker-smoke"
TEXT_BODY="Hello from local Docker Mailer smoke."
SUBJECT_OK="Local Mailer Docker bash smoke"
SUBJECT_CONFLICT="Local Mailer Docker bash smoke conflict"
MAIL_SERVICE_TOKEN="local-mail-service-token"
```

## 2. Mailer を停止し、必要なら DB を初期化する

```bash
docker compose -f "$COMPOSE_FILE" down
rm -f data/mailer/mailer.db data/mailer/mailer.db-wal data/mailer/mailer.db-shm
```

この操作はローカル Mailer の送信依頼履歴を削除します。本番・develop deploy host では実行しないでください。

Linux / macOS の fresh checkout で bind mount 権限だけを検証したい場合は、専用 script も使えます。

```bash
bash scripts/local-compose-fresh-data-check.sh
```

## 3. イメージをビルドする

```bash
docker compose -f "$COMPOSE_FILE" build mailer mailer-migrate
```

## 4. Admin UI パスワード hash を作る

管理画面は `AMANE_ADMIN_PASSWORD_HASH` が必要です。ローカル検証用の任意のパスワードを入力します。

```bash
read -r -s -p "Mailer admin password: " admin_password
printf '\n'

hash="$(
  printf '%s\n%s\n' "$admin_password" "$admin_password" |
    docker compose -f "$COMPOSE_FILE" run --rm -T --no-deps mailer admin hash-password 2>/dev/null |
    tail -n 1
)"

case "$hash" in
  pbkdf2:sha256:*) ;;
  *) echo "Failed to generate AMANE_ADMIN_PASSWORD_HASH." >&2; exit 1 ;;
esac

unset admin_password
```

## 5. Mailer / Mailpit / Admin UI を起動する

手順 1 以降は、同じ bash セッションで順に実行する前提です。別セッションで再開する場合は、
手順 4 で `hash` を作り直してから Admin UI と Mailpit の env も再設定してください。

`.env` に ACS 用の値が入っていても、この shell では Mailpit 固定で上書きします。
`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` は `/admin` request の
`Connection.LocalIpAddress` allowlist であり、socket bind ではありません。実際の host 側公開範囲は
compose の `ports`（`127.0.0.1:5280:8080`）で制限されます。

```bash
export AMANE_ADMIN_ENABLED="true"
export AMANE_ADMIN_USERNAME="admin"
export AMANE_ADMIN_PASSWORD_HASH="$hash"
export AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS="0.0.0.0"
export AMANE_ADMIN_ALLOW_HTTP="true"
export AMANE_ADMIN_PII_LIST_MODE="visible"

export MAILER_TENANTS_PATH="/app/config/mailer/tenants.example.json"
export MAILER_PROVIDER="mailpit"
export MAIL_SERVICE_TOKEN="$MAIL_SERVICE_TOKEN"
export MAILPIT_SMTP_HOST="mailpit"
export MAILPIT_SMTP_PORT="1025"
export MAILPIT_SMTP_USE_SSL="false"
export ACS_CONNECTION_STRING=""

docker compose -f "$COMPOSE_FILE" up -d --wait mailer
```

`AMANE_ADMIN_ALLOW_HTTP=true` と `AMANE_ADMIN_PII_LIST_MODE=visible` はローカル確認専用です。
本番・develop deploy host では HTTP 許可や PII 表示を有効にしないでください。
Admin UI の公開範囲と PII 方針は [ADR 0013](../adr/0013-admin-threat-model-and-pii-policy.md) を参照してください。

## 6. health / ready を確認する

```bash
docker compose -f "$COMPOSE_FILE" ps

curl -fsS "$MAILER_URL/healthz"
printf '\n'

curl -fsS "$MAILER_URL/readyz"
printf '\n'
```

期待値:

```json
{"healthy":true}
{"ready":true}
```

ブラウザで以下を開けます。

- Mailer 管理画面: <http://127.0.0.1:5280/admin/login>
- Mailpit UI: <http://127.0.0.1:8025/>

管理画面のログインは username が `admin`、password が手順 4 で入力した値です。

## 7. payload_hash と request JSON を作る

`payload_hash` は配送対象フィールドだけを canonical JSON にした SHA-256 です。ここでは
MailPayloadHasher と同じキー順の JSON を固定生成します。値に引用符や改行などを入れる場合は、
`examples/payload-hash/` の Python / JavaScript / Go helper を使ってください。

```bash
SHA256_CMD=""
if command -v sha256sum >/dev/null 2>&1; then
  SHA256_CMD="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
  SHA256_CMD="shasum -a 256"
elif command -v openssl >/dev/null 2>&1; then
  SHA256_CMD="openssl"
else
  echo "sha256sum, shasum, or openssl is required." >&2
  exit 1
fi

canonical_payload() {
  subject="$1"
  printf '{"purpose":"%s","source_service":"%s","subject":"%s","text_body":"%s","to":[{"email":"%s"}]}' \
    "$PURPOSE" "$SOURCE_SERVICE" "$subject" "$TEXT_BODY" "$TO_EMAIL"
}

payload_hash() {
  canonical="$1"
  if [ "$SHA256_CMD" = "openssl" ]; then
    printf '%s' "$canonical" | openssl dgst -sha256 | awk '{print $NF}'
  else
    printf '%s' "$canonical" | $SHA256_CMD | awk '{print $1}'
  fi
}

request_json() {
  request_id="$1"
  subject="$2"
  hash_value="$3"
  printf '{"tenant_id":"%s","source_service":"%s","mail_request_id":"%s","purpose":"%s","to":[{"email":"%s"}],"subject":"%s","text_body":"%s","payload_hash":"%s"}' \
    "$TENANT_ID" "$SOURCE_SERVICE" "$request_id" "$PURPOSE" "$TO_EMAIL" "$subject" "$TEXT_BODY" "$hash_value"
}

if command -v uuidgen >/dev/null 2>&1; then
  request_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
elif [ -r /proc/sys/kernel/random/uuid ]; then
  request_id="$(cat /proc/sys/kernel/random/uuid)"
else
  echo "uuidgen is required. On Debian/Ubuntu, install it with: sudo apt install uuid-runtime" >&2
  exit 1
fi

canonical_ok="$(canonical_payload "$SUBJECT_OK")"
hash_ok="$(payload_hash "$canonical_ok")"
json_ok="$(request_json "$request_id" "$SUBJECT_OK" "$hash_ok")"
```

## 8. 正常 POST を投入する

```bash
post_mail_request() {
  json="$1"
  curl -sS -w '\nHTTP_STATUS=%{http_code}\n' \
    -X POST "$MAILER_URL/internal/mail-requests" \
    -H "Authorization: Bearer $MAIL_SERVICE_TOKEN" \
    -H "Content-Type: application/json" \
    --data "$json"
}

post_mail_request "$json_ok"
```

期待値は `HTTP_STATUS=202` と `status: "accepted"` です。

```json
{"mail_request_id":"<request id>","status":"accepted"}
HTTP_STATUS=202
```

## 9. Mailpit と Admin UI を確認する

Mailpit API で件名が見つかることを確認します。Worker 配送に少し時間がかかる場合があるため、
最大 30 秒待ちます。

```bash
mailpit_found=0
for i in $(seq 1 30); do
  if curl -fsS "$MAILPIT_URL/api/v1/messages" | grep -F "$SUBJECT_OK"; then
    mailpit_found=1
    break
  fi
  sleep 1
done

if [ "$mailpit_found" -ne 1 ]; then
  echo "Mailpit message was not found within 30 seconds." >&2
  exit 1
fi
```

ブラウザでは <http://127.0.0.1:8025/> を開き、件名 `Local Mailer Docker bash smoke` のメールが
1 件届いていることを確認します。

Admin UI では <http://127.0.0.1:5280/admin/login> に `admin` / 手順 4 のパスワードでログインし、
`/admin/mail-requests` で同じ件名の行が `Delivered` になっていることを確認します。

## 10. 冪等再送を確認する

同じ `mail_request_id` と同じ payload をもう一度 POST します。

```bash
post_mail_request "$json_ok"
```

期待値は `HTTP_STATUS=202` と `status: "already_accepted"` です。

```json
{"mail_request_id":"<request id>","status":"already_accepted"}
HTTP_STATUS=202
```

## 11. conflict を確認する

同じ `mail_request_id` のまま、件名を変えて `payload_hash` も再計算します。

```bash
canonical_conflict="$(canonical_payload "$SUBJECT_CONFLICT")"
hash_conflict="$(payload_hash "$canonical_conflict")"
json_conflict="$(request_json "$request_id" "$SUBJECT_CONFLICT" "$hash_conflict")"

post_mail_request "$json_conflict"
```

期待値は `HTTP_STATUS=409` と `IDEMPOTENCY_CONFLICT` です。

```json
{"code":"IDEMPOTENCY_CONFLICT"}
HTTP_STATUS=409
```

## 12. 後片付け

コンテナだけ止める場合:

```bash
docker compose -f "$COMPOSE_FILE" down
```

送信依頼履歴も含めて初期化する場合:

```bash
rm -f data/mailer/mailer.db data/mailer/mailer.db-wal data/mailer/mailer.db-shm
```
