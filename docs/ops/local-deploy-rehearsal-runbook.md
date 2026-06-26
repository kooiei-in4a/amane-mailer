[English](local-deploy-rehearsal-runbook.en.md)

# ローカル deploy rehearsal runbook

ローカル PC の Docker で、deploy host 向け `infra/deploy/compose.yml` と同じ形の shared Mailer
スタックを再現する手順です。Consumer アプリ本体や deploy host、本番 ACS 実送信は対象外です。

Mailpit 付きの開発用スタックは
[local-mailer-docker-runbook.md](local-mailer-docker-runbook.md) を参照してください。
こちらは deploy テンプレート（3 tenant / `MAILER_NETWORK_NAME` ネットワーク）の rehearsal 用です。

## 何を確認するか

| 項目 | 確認方法 |
|------|----------|
| compose 設定 | `docker compose ... config --quiet` |
| DB マイグレーション | `mailer-migrate`（profile `ops`） |
| コンテナ起動 | `mailer` が healthy |
| HTTP | `GET /healthz` → `{"healthy":true}`、`GET /readyz` → `{"ready":true}` |
| CLI | `/app/Amane.Mailer healthcheck` → exit 0 |
| Admin UI | `AMANE_ADMIN_*` を設定し、`/admin/login` とログイン後の `/admin/mail-requests` を確認 |
| tenant token | `tenants.json` の `token_env` と `.env` の `MAIL_SERVICE_TOKEN_*` が container 内で一致 |
| 共有ネットワーク | Docker network `MAILER_NETWORK_NAME`（`.env` で設定）、`mailer` サービスが alias `mailer` で名前解決可能 |
| internal ネットワーク | `amane-mailer_internal` が `internal=true`（外向き不可） |

## 前提

- Docker Desktop（Linux engine）が起動していること
- コマンド例は Windows PowerShell 5.1+ / PowerShell 7+ 前提
- `infra/deploy` 配下の `.env` と `tenants.json` は **コミットしない**

## クイックスタート（推奨）

GHCR 認証なしで再現する場合は、リポジトリからイメージをビルドします。

```powershell
cd infra/deploy
.\scripts\local-rehearsal.ps1 -Build
```

No-send shared Mailer smoke（`mail-05a-no-send-smoke.sh`）は **デフォルトでは実行しません**。必要なときだけ `-RunSmoke` を付けます
（bash / python3 と、PowerShell と同じ Docker コンテキストが必要）。

```powershell
.\scripts\local-rehearsal.ps1 -Build -RunSmoke
```

既に GHCR から pull 済みの `sha-*` タグを使う場合:

```powershell
cd infra/deploy
copy .env.example .env
copy ..\..\config\mailer\tenants.shared.example.json tenants.json
# .env の MAILER_IMAGE_TAG と各 MAIL_SERVICE_TOKEN_* を編集
$env:MAILER_PULL_POLICY = 'never'   # または .env に記載
.\scripts\local-rehearsal.ps1
```

スクリプトは既存の `.env` / `tenants.json` を上書きしません。

## 手動手順

### 1. 作業ディレクトリとファイル

```powershell
cd infra/deploy
New-Item -ItemType Directory -Force -Path data | Out-Null
```

| ファイル | 元テンプレート |
|----------|----------------|
| `.env` | `.env.example` |
| `tenants.json` | `config/mailer/tenants.shared.example.json` |

### 2. tenant `token_env` と `.env` の対応

`tenants.shared.example.json` は 3 tenant それぞれに別の `token_env` を持ちます。

| tenant | `token_env` | `.env` の変数 |
|--------|-------------|---------------|
| `example-develop` | `MAIL_SERVICE_TOKEN_DEVELOP` | `MAIL_SERVICE_TOKEN_DEVELOP` |
| `example-staging` | `MAIL_SERVICE_TOKEN_STAGING` | `MAIL_SERVICE_TOKEN_STAGING` |
| `example-production` | `MAIL_SERVICE_TOKEN_PRODUCTION` | `MAIL_SERVICE_TOKEN_PRODUCTION` |

3 つの token は **互いに異なる値** にしてください（本番と同じ運用を rehearsal します）。
`MAIL_SERVICE_TOKEN` は単一 tenant 用の互換変数です。shared 3 tenant 構成では
プレースホルダーで構いません。

Consumer アプリが使うトークン値は、対応する `MAIL_SERVICE_TOKEN_*` と同じ値にします
（本番切替時。ローカル rehearsal では Mailer 側だけ揃えれば十分です）。

### 3. イメージの選び方

**A. ローカルビルド（GHCR 不要）**

```powershell
docker compose --env-file .env `
  -f compose.yml `
  -f compose.local-rehearsal.yml `
  -f compose.local-rehearsal.build.yml `
  build mailer mailer-migrate
```

`.env` 例:

```dotenv
MAILER_IMAGE_REPOSITORY=amane-mailer
MAILER_IMAGE_TAG=local-rehearsal
MAILER_PULL_POLICY=never
```

**B. GHCR の published `sha-*` タグ**

```dotenv
MAILER_IMAGE_REPOSITORY=ghcr.io/YOUR_GITHUB_ORG/amane-mailer
MAILER_IMAGE_TAG=sha-<git-sha>
MAILER_PULL_POLICY=always
```

ローカルにキャッシュ済みなら `MAILER_PULL_POLICY=never` で再認証を避けられます。

手動で手順 4 以降に進む前に、ローカルビルド（3-A）では **必ず** `.env` を上記の
`amane-mailer` / `local-rehearsal` / `never` に直してください。`.env.example` の
GHCR placeholder（`sha-replace-with-published-git-sha`）のままだと、
`compose.local-rehearsal.build.yml` を付けない限り image pull に失敗します。
`local-rehearsal.ps1 -Build` は新規 `.env` 作成時にこれらを埋めますが、既存 `.env` は
上書きしません。

### 4. compose 検証 → migrate → 起動

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml config --quiet

docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml up -d --wait mailer
```

新しいイメージでは **必ず** `mailer-migrate` を先に成功させてから `mailer` を起動してください。

### 5. HTTP / CLI ヘルス

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/healthz | Select-Object -ExpandProperty Content
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/readyz | Select-Object -ExpandProperty Content

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  exec -T mailer /app/Amane.Mailer healthcheck
```

ポート `5281` は `compose.local-rehearsal.yml` の公開用です。
`infra/docker/docker-compose.local.yml`（Mailpit 開発用）の `5280` と競合しません。

### 6. Admin UI

Admin UI を確認する場合は、`local-rehearsal.ps1` を起動する PowerShell セッションで
`AMANE_ADMIN_*` を設定します。Docker の port publish 経由で管理画面へ入るため、
ローカル rehearsal では `AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=0.0.0.0` と
`AMANE_ADMIN_ALLOW_HTTP=true` を明示します。
`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` は `/admin` request の `Connection.LocalIpAddress`
allowlist であり、socket bind ではありません。実際の host 側公開範囲は
`compose.local-rehearsal.yml` の `ports`（`127.0.0.1:5281`）で制限します。
旧 `AMANE_ADMIN_BIND` / `MAILER_ADMIN_BIND` は deprecated alias として残っています。
これはローカル HTTP 確認専用です。deploy host では HTTPS リバースプロキシ前提で
`AMANE_ADMIN_ALLOW_HTTP=false` を維持してください。

```powershell
$composeFiles = @("-f", "compose.yml", "-f", "compose.local-rehearsal.yml")
# local-rehearsal.ps1 -Build で確認する場合だけ次の行を実行します。
$composeFiles += "-f", "compose.local-rehearsal.build.yml"

$adminPassword = [System.Net.NetworkCredential]::new(
  "",
  (Read-Host "Mailer admin password" -AsSecureString)
).Password
$hash = @($adminPassword, $adminPassword) |
  docker compose --env-file .env @composeFiles run --rm -T --no-deps mailer admin hash-password 2>$null |
  Select-Object -Last 1

if ($hash -notlike "pbkdf2:sha256:*") {
  throw "Failed to generate AMANE_ADMIN_PASSWORD_HASH."
}

$env:AMANE_ADMIN_ENABLED = "true"
$env:AMANE_ADMIN_USERNAME = "admin"
$env:AMANE_ADMIN_PASSWORD_HASH = $hash
$env:AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS = "0.0.0.0"
$env:AMANE_ADMIN_ALLOW_HTTP = "true"
$env:AMANE_ADMIN_PII_LIST_MODE = "masked"

.\scripts\local-rehearsal.ps1 -Build
```

GHCR から pull 済みの image で確認する場合は、`compose.local-rehearsal.build.yml` を
`$composeFiles` に足さず、最後のコマンドも `.\scripts\local-rehearsal.ps1` にします。

起動後、ブラウザで <http://127.0.0.1:5281/admin/login> を開き、`admin` と上で入力した
パスワードでログインします。ログイン後に `/admin/mail-requests` へ遷移できれば確認完了です。

ヘッドレスに最低限確認する場合:

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/login |
  Select-Object -ExpandProperty StatusCode
```

期待値は `200` です。

ログインまで確認する場合:

```powershell
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginPage = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/login -WebSession $session
$csrf = [regex]::Match($loginPage.Content, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
if (-not $csrf) { throw "CSRF token not found." }

Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/api/login `
  -WebSession $session `
  -Method Post `
  -Body @{
    __RequestVerificationToken = $csrf
    username = "admin"
    password = $adminPassword
  } | Out-Null

$mailRequests = Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5281/admin/mail-requests -WebSession $session
if ($mailRequests.StatusCode -ne 200) {
  throw "Unexpected /admin/mail-requests status: $($mailRequests.StatusCode)"
}
```

### 7. token が container に入っているか

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  exec -T mailer /bin/sh -c 'test -n "$MAIL_SERVICE_TOKEN_DEVELOP" && test -n "$MAIL_SERVICE_TOKEN_STAGING" && test -n "$MAIL_SERVICE_TOKEN_PRODUCTION" && echo TOKENS_PRESENT'
```

### 8. 共有ネットワークと alias `mailer`

compose が `MAILER_NETWORK_NAME`（`.env` で設定）で外部ネットワークを作成し、
`mailer` サービスに alias `mailer` を付けます。Consumer app compose が同じ
ネットワークに join すると `http://mailer:8080` で到達できます。

```powershell
$networkName = (Get-Content .env | Select-String '^MAILER_NETWORK_NAME=') -replace '^MAILER_NETWORK_NAME=',''
docker network inspect $networkName --format '{{.Name}} internal={{.Internal}}'

docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml `
  run --rm --no-deps --network $networkName curlimages/curl:8.11.1 `
  -fsS http://mailer:8080/healthz
```

`amane-mailer_internal`（compose project の internal ネットワーク）が
`internal: true` であることも確認します。

```powershell
docker network inspect amane-mailer_internal --format 'internal={{.Internal}}'
```

### 9. 後片付け

```powershell
docker compose --env-file .env -f compose.yml -f compose.local-rehearsal.yml down
```

DB を空にし直す場合は `infra/deploy/data/` を削除してから手順 4 をやり直します。

## 任意: no-send shared Mailer smoke

送信なしの API / 認証 / SQLite DB 検証です。ACS は不要（`ACS_CONNECTION_STRING` は空のまま）。
**クイックスタートでは実行しません。** 明示的に `-RunSmoke` を付けたときだけ
`local-rehearsal.ps1` から呼ばれます。

手動実行する場合:

```powershell
# PowerShell rehearsal と同じ compose override を渡す（-Build 時は build override も）
$env:MAIL05A_COMPOSE_DIR = (Resolve-Path .).Path
$env:MAIL05A_COMPOSE_EXTRA = "compose.local-rehearsal.build.yml"   # -Build 時のみ
bash ./drills/mail-05a-no-send-smoke.sh
```

```bash
# Git Bash（Docker Desktop と同じ docker コンテキストであること）
export MAIL05A_COMPOSE_DIR=/path/to/repo/infra/deploy
export MAIL05A_COMPOSE_EXTRA="compose.local-rehearsal.build.yml"   # ローカルビルド時
bash drills/mail-05a-no-send-smoke.sh
```

`MAIL05A_COMPOSE_EXTRA` は `local-rehearsal.ps1` が使った compose override と揃えてください。
省略すると `compose.yml` のみ参照し、`.env` の GHCR placeholder image で `mailer` を
再作成してしまうことがあります。

Windows では WSL の `docker` と Docker Desktop の `docker` が別コンテキストになることがあります。
`-RunSmoke` は bash 側で `docker compose ps` が `mailer` を見つけられない場合は失敗します。
WSL の `bash.exe` から Windows パスへ `cd` できない場合も、Git Bash 利用を促すメッセージで
失敗します。Git Bash + Docker Desktop を使うか、コンテキストを揃えてから実行してください。

スクリプトは SQLite Mailer CLI（`healthcheck`、`db stats`、`db request-state`）と
一時的な `curlimages/curl` compose サービスを使います。PostgreSQL / `psql` は不要です。

ACS 実送信ドリル（`mail-05a-acs-drill.sh`）は **ローカル rehearsal のスコープ外** です。
deploy host 上の deploy ディレクトリで実行してください。

## Rollback（rehearsal 用メモ）

ローカル rehearsal でよく使う最小手順:

| 変更種別 | 戻し方 |
|----------|--------|
| イメージ tag | `.env` の `MAILER_IMAGE_TAG` を前の tag に戻す → `pull`（必要なら）→ `mailer-migrate` → `up -d mailer` |
| tenant JSON | `tenants.json` をタイムスタンプ付きバックアップから復元 → `up -d mailer`（migrate 不要） |
| token のみ | `.env` の `MAIL_SERVICE_TOKEN_*` を戻す → `up -d mailer` |

forward-only migration を適用した DB に古いイメージ tag を当てると migrate が失敗します。
その場合は `data/` を削除するか、バックアップから SQLite を復元してから rollback してください。

## スコープ外（公開前確認の残タスク）

- Deploy host への GHCR pull / deploy rehearsal
- production `live_sending=true` と承認済み ACS sender
- Consumer 各環境 app compose からの実接続テスト
