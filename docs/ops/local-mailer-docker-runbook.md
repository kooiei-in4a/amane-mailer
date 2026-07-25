[English](local-mailer-docker-runbook.en.md) | [Linux/macOS bash 版](local-mailer-docker-runbook-bash.md)

# ローカル Mailer Docker Runbook

ローカル PC の Docker で Mailer と Mailpit を起動し、Mailer 管理画面、Mailpit 受信、
ACS 切替、Dead Letter を確認するための手順です。Consumer アプリ本体の `app` / `db` は対象外です。

Linux / macOS の bash と curl で Mailpit local smoke を確認する場合は
[Linux/macOS 向け local Mailer + Mailpit runbook](local-mailer-docker-runbook-bash.md) を参照してください。

Deploy host 向け deploy compose（3 tenant / 共有ネットワーク）の rehearsal は
[local-deploy-rehearsal-runbook.md](local-deploy-rehearsal-runbook.md) を参照してください。

## Admin UI について

Admin UI（`/admin`）は **内部ネットワーク向け・experimental** な運用補助ツールです。
公開インターネットへの直接公開は想定していません。ローカル確認以外では必ず reverse proxy、
firewall、または Docker port publish 制限をネットワーク境界として設定してください。

現時点の制約（[ADR 0013](../adr/0013-admin-threat-model-and-pii-policy.md) / [ADR 0014](../adr/0014-admin-session-tenant-throttle-audit-design.md)）:

- login throttle は SQLite 正本（再起動後も lock 維持）
- server-side session store あり（資格情報 hash 変更時の即時失効、明示 logout、期限切れ、同時 session 上限）
- 管理者ごとの tenant scope あり（scoped / break-glass 認可。2+ effective tenant + Admin 有効時は scoped または break-glass 管理者がいないと startup fail-closed）。env bootstrap 管理者は初回 seed 時に全設定 tenant scope を付与（break-glass ではない）
- scoped / break-glass 作成 CLI（`admin user create`）あり（`admin hash-password` で hash 生成）
- audit retention sweep あり（`MAILER_ADMIN_AUDIT_RETENTION_DAYS`、既定 180 日。worker 起動時と日次タイマーで batch delete。明示 purge は `db admin-audit purge --older-than-days <days>`）
- `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true` 時は raw IP を DB に保存せず keyed hash を使用（鍵未設定時は startup fail-closed）

## Admin tenant scope 運用

shared Mailer + multi-tenant + Admin 有効時の認可境界と推奨運用です。挙動の正本は `tests/Amane.Mailer.Tests/MailerAdminTenantScopeTests.cs` と [ADR 0014 D-02](../adr/0014-admin-session-tenant-throttle-audit-design.md#d-02-per-admin-tenant-scope-の要否と導入条件) です。

### 用語

| 種別 | DB 上の特徴 | 認可 |
|------|-------------|------|
| **scoped admin** | `is_break_glass=0`、`admin_user_tenant_scopes` に 1 件以上 | 許可 tenant の mail request / dead letter のみ。service-wide backup は不可 |
| **break-glass admin** | `is_break_glass=1`、scope 行なし | 全 tenant 横断。login / 本文閲覧は強化監査 |
| **bootstrap admin** | env `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH` から **空 DB 初回** seed | 設定済み全 tenant scope を付与（`is_break_glass=0`）。**break-glass ではない** |

### effective tenant 数

`tenants.json` の tenant 件数と `SELECT COUNT(DISTINCT tenant_id) FROM mail_requests` の **大きい方**を用います。設定から tenant を削除しても、DB に 2 件以上の distinct `tenant_id` が残る場合は multi-tenant 扱いです（[restore-verification](restore-verification.md) 参照）。

### startup fail-closed

effective tenant が 2 件以上かつ Admin 有効時、有効な scoped admin または break-glass 管理者が 1 名もいないと Mailer は起動しません。migration `006_admin_users_and_tenant_scopes.sql` 適用後、scoped / break-glass 管理者を用意してから Admin を有効化してください。

### 推奨運用（shared multi-tenant 本番）

1. **bootstrap 管理者の継続利用を避ける。** 初回 seed で全 tenant scope が付与されても break-glass 監査は付きません。
2. tenant 境界ごとに **scoped admin** を用意する（develop / staging / production の誤閲覧防止）。
3. service-wide backup を Admin UI から使う場合は、**break-glass** または全 effective tenant scope を持つ管理者を別途用意する。
4. bootstrap 資格情報はローテーションし、日常運用は scoped 管理者に移行する。

### scoped / break-glass 管理者の作成

1. パスワード hash を生成する（平文パスワードは stdout に出さない）:

```powershell
$adminPassword = [System.Net.NetworkCredential]::new(
  "",
  (Read-Host "Mailer admin password" -AsSecureString)
).Password
$hash = @($adminPassword, $adminPassword) |
  docker compose -f infra/docker/docker-compose.local.yml run --rm -T --no-deps mailer admin hash-password 2>$null |
  Select-Object -Last 1

if ($hash -notlike "pbkdf2:sha256:*") {
  throw "Failed to generate AMANE_ADMIN_PASSWORD_HASH."
}
```

2. scoped 管理者を作成する（`--tenant-id` は 1 件以上、複数指定可）:

```powershell
docker compose -f infra/docker/docker-compose.local.yml run --rm -T --no-deps mailer `
  admin user create `
  --username tenant-admin-example `
  --password-hash $hash `
  --tenant-id 00000000-0000-0000-0000-000000000101
```

3. break-glass 管理者を作成する（`--tenant-id` は指定しない）:

```powershell
docker compose -f infra/docker/docker-compose.local.yml run --rm -T --no-deps mailer `
  admin user create `
  --username break-glass-example `
  --password-hash $hash `
  --break-glass
```

Mailer コンテナは `ConnectionStrings__Mailer` で同一 SQLite DB を参照する必要があります。scoped 管理者の再作成（同一 username）は tenant scope を更新し、対象管理者の全 session を即時失効します（ADR 0013 D-04）。

## Admin boolean / numeric environment values

Admin UI の boolean / 正の数値 env は **strict parse** です。ただし **Admin UI 用の値は `AMANE_ADMIN_ENABLED=true` のときだけ** 検証します（`Validate()` と同様）。Admin が無効のとき、mask / login limit などの typo は配送本体の起動を止めません。

| 種別 | 許容値 | 未設定 | 不正値 |
|------|--------|--------|--------|
| `AMANE_ADMIN_ENABLED` / `MAILER_ADMIN_ENABLED` | `true` / `false`（`bool.TryParse` 互換） | `false` | **常に起動失敗** |
| その他 Admin UI boolean（mask / hash-network / db-ops / `ALLOW_HTTP` 等） | `true` / `false` | 既存 default | **Admin 有効時のみ起動失敗**（Admin 無効時は `ALLOW_HTTP` の typo も無視） |
| Admin UI 正の整数（login failure limit 等） | `1` 以上の整数 | 既存 default | **Admin 有効時のみ起動失敗** |
| audit retention 数値（`MAILER_ADMIN_AUDIT_RETENTION_*`） | 範囲内の整数（Days 1–3650、SweepHours 1–168、SweepSeconds 1–604800、BatchSize 1–1000） | 既存 default（例: 180 日） | **常に起動失敗**（worker の audit sweep が参照するため。Admin UI の on/off とは独立） |

### Worker / Metrics / Mailpit boolean・host／port（#358 / #356）

`Mailer__Worker__Enabled` / `Mailer__Metrics__Enabled` / `Mailer__Mailpit__UseSsl`（および `MAILPIT_SMTP_USE_SSL`）も同じ **strict boolean** 規則（未設定は既定、empty／whitespace／typo は起動失敗）。Mailpit SMTP host（`Mailer__Mailpit__SmtpHost` / `MAILPIT_SMTP_HOST`）と port（`Mailer__Mailpit__SmtpPort` / `MAILPIT_SMTP_PORT`、**1–65535**）は、effective provider が `mailpit` のテナントがあるときのみ startup 検証する（#356）。ACS のみの構成では未使用の host／port typo は起動を止めない。`UseSsl` は #358 どおり常に Load 時検証。primary key が存在する（値がある／空含む）場合は fallback より優先し、empty を未設定扱いしない。

### Worker / Webhook / Sweep / Retention / Healthcheck 数値

`Mailer__Worker__*` / `Mailer__Webhook__*` / `Mailer__Sweep__*` / `Mailer__Retention__*` / `Mailer__Healthcheck__*` は **strict validation**（#329）。未設定は既定値。空文字・形式不正・0／負数・上限超過は起動失敗（clamp しない）。許容範囲は [サービス仕様 5.2](../service-spec.md#52-worker--sweep--retention環境変数) を参照。起動失敗時はログのキー名と range を確認する（secret は出ない）。

typo（例: `tru`、`yes`、`abc`）は silent default にせず、上記の適用範囲で startup 検出します。

## Admin audit identifier hash key rotation

`AMANE_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true`（または `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true`）の環境では、
`AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY`（fallback: `MAILER_ADMIN_AUDIT_IDENTIFIER_HASH_KEY`）に
32 バイト以上のランダム値を base64 で設定する（placeholder 禁止）。

鍵ローテーション手順:

1. 新しい 32 バイト以上のランダム鍵を生成し、base64 文字列として用意する。
2. Mailer を停止する。
3. 環境変数 `AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY` を新鍵に更新する。
4. Mailer を再起動する。

運用上の影響:

- `admin_login_throttle` の既存行は旧鍵で計算された key 成分を含むため、**実質的に throttle 状態がリセット**される。
- 過去の `admin_audit_events.source_ip` に保存された hash は新鍵では照合できない（**過去 IP 相関不可**）。
- ローテーション後は通常どおり login throttle と auth 監査が新鍵で記録される。

鍵生成例（PowerShell）:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

## Admin audit retention

`MAILER_ADMIN_AUDIT_RETENTION_DAYS`（fallback: `AMANE_ADMIN_AUDIT_RETENTION_DAYS`）で `admin_audit_events` の保持日数を設定します（既定 180 日）。worker 有効時、起動直後と日次タイマーで保持期間を過ぎた行を batch delete します。`mail_requests` や本文 payload には触れません。

30 日未満の保持期間は `ASPNETCORE_ENVIRONMENT=Development` のローカル開発以外では startup fail-closed です。

明示 purge（runbook / 手動運用）:

```powershell
dotnet Amane.Mailer.dll db admin-audit purge --older-than-days 180
```

purge 出力と sweep ログは削除件数と日数のみを含み、actor・target・メール payload は含みません。`db backup` スナップショットには purge 前の監査行が含まれます（[restore-verification](restore-verification.md) 参照）。

## 前提

- Docker Desktop が起動していること。
- コマンドはリポジトリ root で実行すること。
- 以下は Windows PowerShell 前提です。
- local compose は `infra/docker/docker-compose.local.yml` です。
- Mailpit は local-only helper として既定で `axllent/mailpit:latest` を使います。
  特定 build で再現する場合は `MAILPIT_IMAGE` で tag / digest を上書きします
  （方針: [container image pinning policy](container-image-pinning.md)）。
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
`AMANE_ADMIN_ALLOW_HTTP=true` は **Development 専用**です。Admin 有効時に Production／Staging で
`true` を指定すると Mailer は startup failure になります。ローカル Docker HTTP 確認では
`ASPNETCORE_ENVIRONMENT=Development` も合わせて設定してください。
本番・develop deploy host では HTTP 許可や PII 表示を有効にしないでください。
手順 5 以降の切替手順は、同じ PowerShell セッションで実行する前提です。
別セッションで再開する場合は、手順 4 で `$hash` を作り直してから管理画面 env も再設定してください。

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"  # required with ALLOW_HTTP=true (#341)
$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"       # Development-only local Docker HTTP
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
表示・保存される error message は分類・サニタイズ済みのサマリです（connection string・token・URL query・メールアドレス等はマスクされ、原因分類用の stable `error_code`（`MailDeliveryErrorCodes`）は残ります）。raw provider response や exception 型名は `error_code` に使いません。詳細は [SECURITY.md](../../SECURITY.md) の "Provider Error Sanitization" を参照してください。

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
$env:ASPNETCORE_ENVIRONMENT = "Development"  # required with ALLOW_HTTP=true (#341)
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
