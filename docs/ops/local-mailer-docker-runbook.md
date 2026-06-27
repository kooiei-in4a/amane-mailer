[English](local-mailer-docker-runbook.en.md)

# ローカル Mailer Docker Runbook

ローカル PC の Docker で Mailer と Mailpit を起動し、Mailer 管理画面、Mailpit 受信、
ACS 切替、Dead Letter を確認するための手順です。Consumer アプリ本体の `app` / `db` は対象外です。

Deploy host 向け deploy compose（3 tenant / 共有ネットワーク）の rehearsal は
[local-deploy-rehearsal-runbook.md](local-deploy-rehearsal-runbook.md) を参照してください。

## Admin UI について

Admin UI（`/admin`）は **内部ネットワーク向け・experimental** な運用補助ツールです。
公開インターネットへの直接公開は想定していません。ローカル確認以外では必ず reverse proxy、
firewall、または Docker port publish 制限をネットワーク境界として設定してください。

現時点の制約（[ADR 0013](../adr/0013-admin-threat-model-and-pii-policy.md) の方針に対して未実装）:

- login throttle は in-memory のみ（プロセス再起動でリセット）
- server-side session store なし（cookie auth のみ）、管理者無効化・認証情報変更時の即時 session 失効は未実装
- 管理者ごとの tenant scope なし（単一 `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`）
- audit log は body view と login 成功/失敗を `admin_audit_events` SQLite テーブルに永続化（stdout にもミラー）。logout / session expired / login rate limited、retention sweep、network identifier の hash 化は未実装（[#6](https://github.com/kooiei-in4a/amane-mailer/issues/6) で追跡中）

## 前提

- Docker Desktop が起動していること。
- コマンドはリポジトリ root で実行すること。
- 以下は Windows PowerShell 前提です。
- local compose は `infra/docker/docker-compose.local.yml` です。
- Mailpit は local-only helper として既定で `axllent/mailpit:latest` を使います。特定 build で再現する場合は `MAILPIT_IMAGE` で tag / digest を上書きします（方針: [container image pinning policy](container-image-pinning.md)）。
- 既定は `MAILER_PROVIDER=mailpit` です。ACS 実送信は、承認済み ACS リソース、送信元アドレス、送信先アドレスがある場合だけ実行します。
- `config/mailer/tenants.local*.json` は `.gitignore` 対象です。実送信用の tenant JSON や接続文字列をコミットしないでください。

## データディレクトリ権限（fresh checkout）

local compose の SQLite は `data/mailer/` を bind mount します（named volume ではありません）。
`data/mailer` は `.gitignore` 対象のため、clone 直後の checkout には存在しません。

| 環境 | fresh checkout での挙動 |
|------|-------------------------|
| Linux / macOS | Docker が bind mount 先を **root 所有 mode 755** で自動作成する。Mailer イメージは non-root ユーザのため、このままでは SQLite を作成できない（`SQLite Error 14: unable to open database file`）。 |
| Windows Docker Desktop | ホスト側ディレクトリが permissive に作成されることが多く、手動 setup なしでも migrate が通る場合がある。 |

`infra/docker/docker-compose.local.yml` の `data-init` サービスが migrate 前に
`data/mailer` を world-writable（mode 777）にし、non-root コンテナから SQLite を
作成できるようにします。通常の `docker compose up` / runbook 手順では追加操作は不要です。

Linux/macOS で fresh checkout を検証する場合（`data/mailer` が無ければそのまま実行可）:

```bash
bash scripts/local-compose-fresh-data-check.sh
```

既存の `data/mailer` がある場合、スクリプトは誤削除を防ぐため abort します。
履歴を削除して検証する場合のみ、明示的に reset フラグを付けてください:

```bash
LOCAL_FRESH_DATA_RESET=1 bash scripts/local-compose-fresh-data-check.sh
```

検証後も `data/mailer` を残す場合は `LOCAL_FRESH_DATA_KEEP=1` を併用できます。

手動で bind mount 先を用意する場合（`data-init` を使わない場合）:

```bash
mkdir -p data/mailer
chmod 0777 data/mailer
```

release smoke（公開 GHCR イメージ + named volume）の `data-init` については
[release-image-smoke.md](release-image-smoke.md) を参照してください。

## 1. Mailer を停止する

```powershell
docker compose -f infra/docker/docker-compose.local.yml down
```

## 2. Mailer DB を初期化する

Mailer の SQLite は Docker volume ではなく `data/mailer/` の bind mount です。
空の状態から確認したい場合は、ローカル DB ファイルを削除します。

```powershell
$mailerDbFiles = @(
  ".\data\mailer\mailer.db",
  ".\data\mailer\mailer.db-wal",
  ".\data\mailer\mailer.db-shm"
)

Remove-Item -LiteralPath $mailerDbFiles -Force -ErrorAction SilentlyContinue
```

この操作はローカル Mailer の送信依頼履歴を削除します。本番・develop deploy host では実行しないでください。

## 3. イメージをビルドする

```powershell
docker compose -f infra/docker/docker-compose.local.yml build mailer mailer-migrate
```

## 4. 管理画面パスワード hash を作る

管理画面は `AMANE_ADMIN_PASSWORD_HASH` が必要です。パスワードは任意のローカル検証用の値にしてください。

```powershell
$adminPassword = Read-Host "Mailer admin password"
$hash = @($adminPassword, $adminPassword) |
  docker compose -f infra/docker/docker-compose.local.yml run --rm -T --no-deps mailer admin hash-password 2>$null |
  Select-Object -Last 1

if ($hash -notlike "pbkdf2:sha256:*") {
  throw "Failed to generate AMANE_ADMIN_PASSWORD_HASH."
}
```

## 5. Mailer / Mailpit を起動する

`.env` に ACS 用の値が入っていても、以下の PowerShell セッションでは Mailpit 固定で上書きします。
Docker の port publish 経由で管理画面へ入るため、`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` を指定します。
これは `/admin` request の `Connection.LocalIpAddress` allowlist であり、socket bind ではありません。
実際の host 側公開範囲は compose の `ports`（この runbook では `127.0.0.1:5280:8080`）で制限します。
旧 `AMANE_ADMIN_BIND` / `MAILER_ADMIN_BIND` は deprecated alias として残っています。
`AMANE_ADMIN_ALLOW_HTTP=true` と `AMANE_ADMIN_PII_LIST_MODE=visible` はローカル確認専用です。
本番・develop deploy host では HTTP 許可や PII 表示を有効にしないでください。
手順 5 以降の切替手順は、同じ PowerShell セッションで実行する前提です。
別セッションで再開する場合は、手順 4 で `$hash` を作り直してから管理画面 env も再設定してください。

```powershell
$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"       # local Docker HTTP only
$env:AMANE_ADMIN_PII_LIST_MODE = "visible" # local UI verification only

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.example.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAIL_SERVICE_TOKEN = "local-mail-service-token"
$env:MAILPIT_SMTP_HOST = "mailpit"
$env:MAILPIT_SMTP_PORT = "1025"
$env:MAILPIT_SMTP_USE_SSL = "false"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --wait mailer
```

## 6. 起動確認

```powershell
docker compose -f infra/docker/docker-compose.local.yml ps

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/healthz |
  Select-Object -ExpandProperty Content

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5280/readyz |
  Select-Object -ExpandProperty Content
```

期待値:

```json
{"healthy":true}
{"ready":true}
```

ブラウザで以下を開きます。

- Mailer 管理画面: <http://127.0.0.1:5280/admin/login>
- Mailpit UI: <http://127.0.0.1:8025/>

管理画面のログインは、username が `admin`、password が手順 4 で入力した値です。

## 7. テストメールを投入する

以下は `example-develop` テナントに 1 件投入する smoke test です。
`payload_hash` は配送対象フィールドだけを正規化した SHA-256 です。

```powershell
$tenantId = "00000000-0000-0000-0000-000000000101"
$sourceService = "example-service"
$to = "smoke@example.com"
$subject = "Local Mailer Docker smoke"
$textBody = "Hello from local Docker Mailer smoke."
$purpose = "local-docker-smoke"

$canonical = ([ordered]@{
  purpose = $purpose
  source_service = $sourceService
  subject = $subject
  text_body = $textBody
  to = @([ordered]@{ email = $to })
} | ConvertTo-Json -Depth 6 -Compress)

$sha = [System.Security.Cryptography.SHA256]::Create()
$payloadHash = [System.BitConverter]::ToString(
  $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical))
).Replace("-", "").ToLowerInvariant()

$requestId = [guid]::NewGuid().ToString()
$request = [ordered]@{
  tenant_id = $tenantId
  source_service = $sourceService
  mail_request_id = $requestId
  purpose = $purpose
  to = @(@{ email = $to })
  subject = $subject
  text_body = $textBody
  payload_hash = $payloadHash
}

$json = $request | ConvertTo-Json -Depth 6 -Compress

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:5280/internal/mail-requests" `
  -Headers @{ Authorization = "Bearer local-mail-service-token" } `
  -ContentType "application/json" `
  -Body $json
```

期待値:

```json
{
  "mail_request_id": "<request id>",
  "status": "accepted"
}
```

## 8. 管理画面と Mailpit を確認する

Mailer 管理画面:

1. <http://127.0.0.1:5280/admin/login> にアクセスします。
2. `admin` / 手順 4 のパスワードでログインします。
3. `/admin/mail-requests` に遷移し、`Local Mailer Docker smoke` の行が `Delivered` になっていることを確認します。

Mailpit:

1. <http://127.0.0.1:8025/> にアクセスします。
2. 件名 `Local Mailer Docker smoke` のメールが 1 件届いていることを確認します。

## 9. ACS 実送信用 tenant を用意する

ACS 実送信を検証する場合だけ実行します。送信元は ACS で承認済みの sender/domain にしてください。

```powershell
Copy-Item `
  -LiteralPath .\config\mailer\tenants.local-acs.json.example `
  -Destination .\config\mailer\tenants.local-acs.json `
  -ErrorAction Stop
```

`config/mailer/tenants.local-acs.json` を編集し、少なくとも以下を実値にします。

- `name`
- `source_services`
- `default_from.email`
- `default_from.display_name`

このファイルは `config/mailer` の bind mount で `/app/config/mailer/` から読まれます。
イメージを再ビルドせず、`MAILER_TENANTS_PATH` の切替だけで利用できます。

## 10. ACS に切り替えて実送信する

手順 9 の tenant JSON と同じ `source_service`、実際に受信確認できる宛先を使います。

```powershell
$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.local-acs.json"
$env:MAILER_PROVIDER = "acs"
$env:ACS_CONNECTION_STRING = "<ACS connection string>"
$env:MAILPIT_SMTP_HOST = "mailpit"

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

手順 7 の `$sourceService`、`$to`、`$subject`、`$textBody` を ACS 検証用に変更して投入します。
例:

```powershell
$sourceService = "<tenants.local-acs.json の source_services に含めた値>"
$to = "<受信確認できるメールアドレス>"
$subject = "Local Mailer ACS smoke"
$textBody = "Hello from local Docker Mailer via ACS."
```

投入後、管理画面 `/admin/mail-requests` で `Local Mailer ACS smoke` が `Delivered` になることを確認します。
ACS 側で拒否された場合は `Failed` または retry 後の `DeadLettered` になり、詳細画面の attempt に provider error が表示されます。
表示・保存される error message は分類・サニタイズ済みのサマリです（connection string・token・URL query・メールアドレス等はマスクされ、原因分類用の `error_code` は残ります）。raw provider response は保存しません。詳細は [SECURITY.md](../../SECURITY.md) の "Provider Error Sanitization" を参照してください。

## 11. Dead Letter を確認する

資格情報なしで Dead Letter 表示だけを確認する場合は、Mailpit provider を使い、SMTP 宛先を意図的に失敗させます。
履歴を分けたい場合は手順 2 で DB を初期化してから実行してください。

```powershell
@'
{
  "version": 1,
  "environment": "develop",
  "tenants": [
    {
      "tenant_id": "00000000-0000-0000-0000-000000000101",
      "name": "example-deadletter",
      "source_services": ["example-service"],
      "default_from": {
        "email": "noreply@example.com",
        "display_name": "Example Service"
      },
      "token_env": "MAIL_SERVICE_TOKEN",
      "provider": "mailpit",
      "live_sending": false,
      "metadata_max_bytes": 4096,
      "retry": {
        "max_attempts": 1,
        "initial_delay_seconds": 1,
        "max_delay_seconds": 1
      }
    }
  ]
}
'@ | Set-Content -LiteralPath .\config\mailer\tenants.local-deadletter.json -Encoding UTF8

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.local-deadletter.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAILPIT_SMTP_HOST = "127.0.0.1"
$env:MAILPIT_SMTP_PORT = "1025"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

手順 7 の `$subject` を `Local Mailer Dead Letter smoke` に変更して投入します。数秒待ってから状態を確認します。
`127.0.0.1:1025` は mailer コンテナ内の loopback を指すため、Mailpit ではなく未待受の SMTP 宛先として即時失敗します。

```powershell
Start-Sleep -Seconds 5
docker compose -f infra/docker/docker-compose.local.yml exec -T mailer /app/Amane.Mailer db stats
```

期待値:

```text
status_dead_lettered=1
dead_lettered_total=1
```

管理画面では `/admin/dead-letters` に遷移し、`Local Mailer Dead Letter smoke` の行が表示されることを確認します。

## 12. ACS / Dead Letter 検証後に Mailpit へ復帰する

手順 5 と同じ PowerShell セッションで実行するか、手順 4 で `$hash` を作り直してから以下の管理画面 env も再設定してください。

```powershell
$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"
$env:AMANE_ADMIN_PII_LIST_MODE = "visible"

$env:MAILER_TENANTS_PATH = "/app/config/mailer/tenants.example.json"
$env:MAILER_PROVIDER = "mailpit"
$env:MAIL_SERVICE_TOKEN = "local-mail-service-token"
$env:MAILPIT_SMTP_HOST = "mailpit"
$env:MAILPIT_SMTP_PORT = "1025"
$env:MAILPIT_SMTP_USE_SSL = "false"
$env:ACS_CONNECTION_STRING = ""

docker compose -f infra/docker/docker-compose.local.yml up -d --force-recreate --wait mailer
```

手順 7 をもう一度実行し、管理画面で `Delivered`、Mailpit UI で受信を確認します。
これで ACS または Dead Letter 検証後に Mailpit へ戻せていることを確認できます。

## 13. 後片付け

コンテナだけ止める場合:

```powershell
docker compose -f infra/docker/docker-compose.local.yml down
```

Dead Letter 検証で作成したローカル tenant JSON を削除する場合:

```powershell
Remove-Item -LiteralPath .\config\mailer\tenants.local-deadletter.json -Force -ErrorAction SilentlyContinue
```

送信依頼履歴も含めて初期化する場合は、手順 2 の DB ファイル削除も実行します。
