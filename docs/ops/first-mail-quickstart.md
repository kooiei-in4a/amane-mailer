[English](first-mail-quickstart.en.md)

# Zero-Admin 初回メール quickstart（local Mailpit）

fresh clone 直後に、**Admin UI を有効にせず** Mailer + Mailpit で 1 通届くところまでを最短で確認する手順です。
ACS 実送信、Dead Letter、backup / restore、deploy rehearsal、multi-tenant 共有 Mailer は対象外です。

より詳しい smoke（冪等再送、conflict、Admin UI など）は次を参照してください。

- [ローカル Mailer Docker runbook](local-mailer-docker-runbook.md) [(en)](local-mailer-docker-runbook.en.md)
- [Linux/macOS 向け local Mailer + Mailpit runbook](local-mailer-docker-runbook-bash.md) [(en)](local-mailer-docker-runbook-bash.en.md)

## 前提

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) または Docker Engine が起動していること。
- リポジトリ root でコマンドを実行すること。
- host port `5280`（Mailer）と `8025`（Mailpit）が空いていること。
- 手順 1–2 は `curl` があれば可（Windows では PowerShell の `curl.exe` でも可）。
- 手順 3–4 は **bash** と `curl` が必要です（Windows では [Git Bash](https://gitforwindows.org/) を使ってください。PowerShell のみでは heredoc、`uuidgen`、`seq` が使えません）。

Admin 用の環境変数（`AMANE_ADMIN_*`）は **設定不要** です。local compose の既定値で Mailpit 配送になります。

## 1. Mailer + Mailpit を起動する

```bash
docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
```

初回はイメージ build のため数分かかることがあります。

## 2. health / ready を確認する

```bash
curl -fsS http://127.0.0.1:5280/healthz
printf '\n'
curl -fsS http://127.0.0.1:5280/readyz
printf '\n'
```

期待値:

```json
{"healthy":true}
{"ready":true}
```

## 3. 1 通 POST する

`mail_request_id` は冪等キーです。毎回新しい UUID を使ってください。
`uuidgen` がない環境では、任意の UUID 文字列を `request_id` に設定してください。

```bash
request_id="$(uuidgen)"

curl -i -X POST http://127.0.0.1:5280/internal/mail-requests \
  -H "Authorization: Bearer local-mail-service-token" \
  -H "Content-Type: application/json" \
  -d @- <<JSON
{
  "tenant_id": "00000000-0000-0000-0000-000000000101",
  "mail_request_id": "${request_id}",
  "source_service": "example-service",
  "purpose": "FormResponseNotification",
  "to": [
    { "email": "admin@example.com" }
  ],
  "subject": "New response",
  "text_body": "A new response arrived.",
  "payload_hash": "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9"
}
JSON
```

期待レスポンスは `HTTP/1.1 202 Accepted` と、次の JSON です。

```json
{
  "mail_request_id": "<request_id>",
  "status": "accepted"
}
```

`payload_hash` の計算方法は [Consumer クイックスタート](../../README.md#consumer-クイックスタート) または [examples/payload-hash/](../../examples/payload-hash/README.md) を参照してください。

## 4. Mailpit で到着を確認する

### ブラウザ

<http://127.0.0.1:8025/> を開き、件名 **New response** のメールが 1 件届いていることを確認します。

### API（curl）

Worker 配送に数秒かかることがあるため、最大 30 秒待ちます。

```bash
subject="New response"
mailpit_found=0
for i in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:8025/api/v1/messages | grep -F "$subject"; then
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

## 失敗したときに見る場所

次の 5 点を順に確認してください。

1. **コンテナ状態** — `docker compose -f infra/docker/docker-compose.local.yml ps` で `mailer` と `mailpit` が `running` / `healthy` か。
2. **Mailer ログ** — `docker compose -f infra/docker/docker-compose.local.yml logs mailer --tail 50` で startup エラーや DB 初期化失敗がないか。
3. **port 競合** — `5280` / `8025` を別プロセスが使用していないか。競合時は compose 起動が失敗するか、`curl` が接続拒否になる。
4. **POST が 401 / 403** — `Authorization: Bearer local-mail-service-token` と `tenant_id`（example tenant `00000000-0000-0000-0000-000000000101`）が正しいか。
5. **Mailpit に届かない** — 手順 2 の `readyz` が `{"ready":true}` だったか。数秒待ってから Mailpit UI / API を再確認する。

## 片付け

コンテナだけ止める場合:

```bash
docker compose -f infra/docker/docker-compose.local.yml down
```
