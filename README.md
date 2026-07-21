# Amane Mailer

[English README](README.en.md)

Amane Mailer は汎用メール送信マイクロサービスです。送信依頼を受け付けて永続化し、
バックグラウンド Worker が Azure Communication Services (ACS) または Mailpit 経由で
非同期に配送します。Consumer アプリは本文・宛先・件名を組み立てて送信依頼を POST するだけです。

## 構成

- `src/Amane.Mailer`: ASP.NET Core / Native AOT の Mailer サービス。
- `src/Amane.Mailer.Contracts`: HTTP 契約の正本となる DTO、error constants、payload hash helper の NuGet パッケージ。
- `tests/`: Mailer と Contracts のテストスイート。
- `config/mailer`: 安全な tenant example と JSON schema。
- `infra/docker`: ローカル Docker build と Mailpit compose。
- `infra/deploy`: 本番向け deploy-time compose template。
- `docs/`: API spec、ADR、runbook。

## 前提ツール

- [.NET SDK](https://dotnet.microsoft.com/download) — `global.json` で指定したバージョン（現在 10.0.301）
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## ローカル検証

リポジトリ root で実行します。

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

## Mailpit で起動する

**初めて 1 通届くところまで確認する**場合は、Admin 不要の
[Zero-Admin 初回メール quickstart](docs/ops/first-mail-quickstart.md) [(en)](docs/ops/first-mail-quickstart.en.md)
から始めてください。PowerShell なら `.\scripts\local-first-mail-smoke.ps1`、bash なら `bash scripts/local-first-mail-smoke.sh` で同じ確認を自動実行できます。

local compose は Mailer イメージを build し、Mailpit を起動します。

```powershell
docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
```

ローカル URL:

- Mailer health: <http://127.0.0.1:5280/healthz>
- Mailer readiness: <http://127.0.0.1:5280/readyz>
- Mailpit UI: <http://127.0.0.1:8025/>

既定のローカルトークンは `local-mail-service-token` です。安全な example tenant は、
ローカルの `config/mailer/tenants.example.json` bind mount から読み込まれます。
Admin UI setup、ACS 切替、Dead Letter 確認を含む smoke 手順は
[ローカル Mailer Docker runbook](docs/ops/local-mailer-docker-runbook.md) [(en)](docs/ops/local-mailer-docker-runbook.en.md) を参照してください。
Linux / macOS の bash と curl で Mailpit 到着、冪等再送、conflict まで確認する手順は
[Linux/macOS 向け local Mailer + Mailpit runbook](docs/ops/local-mailer-docker-runbook-bash.md) [(en)](docs/ops/local-mailer-docker-runbook-bash.en.md) を参照してください。

## Admin UI

`AMANE_ADMIN_ENABLED=true` を設定すると `/admin` が有効になります（既定は無効）。
管理画面は **内部ネットワーク向け・experimental** な運用補助ツールです。公開インターネットへの
直接公開は想定していません。production では reverse proxy、firewall、または Docker port publish
制限をネットワーク境界として設定してください。

**現時点の制約（[ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md) / [ADR 0014](docs/adr/0014-admin-session-tenant-throttle-audit-design.md)）**

- login throttle は SQLite 正本（再起動後も lock 維持）
- server-side session store あり（資格情報 hash 変更時の即時失効、明示 logout、期限切れ、同時 session 上限）
- 管理者ごとの tenant scope あり（`admin_users` / `admin_user_tenant_scopes`）。scoped admin は許可 tenant のみ閲覧・操作。break-glass 管理者は全 tenant 横断（強化監査）。2 件以上の effective tenant + Admin 有効時は scoped または break-glass 管理者がいないと startup fail-closed
- env bootstrap 管理者（`AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`）は初回 DB 作成時に `admin_users` へ seed され、**設定済み全 tenant の scope** を付与する（`is_break_glass=false`。**break-glass 扱いではない**）。multi-tenant 本番では bootstrap 管理者の継続利用を避け、tenant 別 scoped 管理者を用意する（[runbook](docs/ops/local-mailer-docker-runbook.md#admin-tenant-scope-運用)）
- scoped / break-glass 管理者は `admin user create` で作成（`admin hash-password` で hash 生成）
- audit log は body view と auth イベント（login / logout / session expired / account locked / login rate limited）を `admin_audit_events` に永続化（stdout にもミラー）。retention sweep は `MAILER_ADMIN_AUDIT_RETENTION_DAYS`（既定 180 日）で自動削除。明示 purge は `db admin-audit purge --older-than-days <days>`
- `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true` 時は raw IP を DB に保存せず keyed hash を使用（鍵未設定時は startup fail-closed）

## デプロイ時の注意

runtime image には安全な example と tenant schema だけを含めます。実 tenant JSON は
deploy-time input として用意し、container へ mount してください。

- Deploy compose: `infra/deploy/compose.yml`
- 安全な env template: `infra/deploy/.env.example`
- Tenant schema: `config/mailer/tenants.schema.json`

実 tenant token、ACS connection string、production sender address、deploy host の `.env` は
commit しないでください。

運用 runbook:

- [ローカル deploy rehearsal](docs/ops/local-deploy-rehearsal-runbook.md) [(en)](docs/ops/local-deploy-rehearsal-runbook.en.md)
- [ACS secret / platform-owned sender 登録 CLI](docs/ops/register-acs-cli-runbook.md) [(en)](docs/ops/register-acs-cli-runbook.en.md)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [リストア手順](docs/ops/restore-procedure.md) [(en)](docs/ops/restore-procedure.en.md)
- [リストア検証](docs/ops/restore-verification.md) [(en)](docs/ops/restore-verification.en.md)

v0.4.0 publish 後の GHCR イメージ（既定 `ghcr.io/kooiei-in4a/amane-mailer:v0.4.0`）を clean state から
pull して Mailer + Mailpit を起動し、`/healthz`・`/readyz`・正常 POST・Mailpit 到着・冪等再送・
conflict・401・403 を自動 smoke するには `scripts/release-smoke.sh`（Linux / macOS / Git Bash）または
`scripts/release-smoke.ps1`（Windows / PowerShell + Docker Desktop）を使います。手順と設定は
[公開 release イメージ smoke](docs/ops/release-image-smoke.md) [(en)](docs/ops/release-image-smoke.en.md) を参照してください。

v0.4.0 release では、既定 smoke tag `v0.4.0` の GHCR runtime image は publish 後
**multi-arch**（`linux/amd64` と
`linux/arm64`）です。smoke では release notes または Docker manifest の platform を確認し、
`MAILER_IMAGE_PLATFORM=linux/amd64` または `MAILER_IMAGE_PLATFORM=linux/arm64` を指定してください。
amd64 emulation のみ利用可能なホストでは `linux/amd64` を明示してください。

```bash
bash scripts/release-smoke.sh
```

```powershell
.\scripts\release-smoke.ps1
```

`infra/deploy/drills/` 配下の no-send / ACS deploy drill helper script（`mail-05a-*`）は、
SQLite Mailer CLI（`healthcheck`、`db stats`、`db request-state`）と一時的な curl compose client を使います。
詳細は [docs/ops/drills/mail-05a-drill-guide.html](docs/ops/drills/mail-05a-drill-guide.html)
を参照してください。ACS 実送信なしの local deploy rehearsal は
[ローカル deploy rehearsal runbook](docs/ops/local-deploy-rehearsal-runbook.md) [(en)](docs/ops/local-deploy-rehearsal-runbook.en.md) を使います。

## Contracts パッケージ

`Amane.Mailer.Contracts` は nuget.org で公開します。
version の公開は [`.github/workflows/publish-contracts.yml`](.github/workflows/publish-contracts.yml)
から手動で行います（release tag ref から実行。version は tag から導出され、csproj `<Version>` と一致することを検証します）。

HTTP 契約のコード上の正本は `src/Amane.Mailer.Contracts/` です。Mailer runtime は同じ DTO / constants を参照し、[OpenAPI](docs/api/openapi.yaml) は Consumer 向け HTTP reference / 公開 schema として同期します。service release / Docker image tag / NuGet package / OpenAPI `info.version` はすべて同一の `X.Y.Z` を使用します（詳細: [バージョニングポリシー](docs/service-spec.md#バージョニングポリシー)）。

Contracts package は consumer 互換のため `net8.0` を target します。Mailer runtime は `net10.0` ですが、リリース version の同期と target framework は別問題です。詳細は [`src/Amane.Mailer.Contracts/README.md`](src/Amane.Mailer.Contracts/README.md) の Target Framework 節を参照してください。

## Consumer クイックスタート

起動した Mailer にメール送信依頼を POST し、必要に応じて配送ステータスを GET するための最低限の情報です。

### 送信依頼（POST）

- **エンドポイント**: `POST http://mailer:8080/internal/mail-requests`
- **認証**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - ローカル既定トークン: `local-mail-service-token`
- **必須フィールド**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `to`, `subject`, `payload_hash`
- **`payload_hash`**: 配送フィールドの canonical JSON SHA-256。
  .NET は `Amane.Mailer.Contracts` の `MailPayloadHasher` を使用。
  Python / JavaScript / Go の実装例: [examples/payload-hash/](examples/payload-hash/README.md)
  自分の request JSON を検証: `python examples/payload-hash/python/verify_request.py request.json`
  アルゴリズム仕様・エラーコード・冪等性: [docs/api/openapi.yaml](docs/api/openapi.yaml)

ローカル compose 起動後は、host から次の smoke request を実行できます。
`mail_request_id` は冪等キーなので、同じ依頼として再送したい場合以外は毎回新しい UUID を使います。
`uuidgen` がない環境では、`request_id` に任意の UUID 文字列を設定してください。

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

期待レスポンスは `202 Accepted` と、生成した `request_id` を含む次の JSON です。

```json
{
  "mail_request_id": "<request_id>",
  "status": "accepted"
}
```

同じ `request_id` と同じ JSON をもう一度 POST すると、新規受付ではなく冪等再送として
`202 Accepted` / `status: "already_accepted"` になります。新規 request と再送は
レスポンス body の `status` が `accepted` か `already_accepted` かで見分けます。

conflict を安全に試す場合はローカル環境でのみ、同じ `request_id` のまま `subject` など
hash 対象フィールドを変更し、その payload に合わせて `payload_hash` を再計算してから POST してください。
期待結果は `409 Conflict` / `IDEMPOTENCY_CONFLICT` です。

### 配送ステータスの照会（GET）

`202 Accepted` は「依頼を受け付けた」ことだけを示します。Worker による実際の配送結果（`delivered` / `failed` など）は GET で確認します。

- **エンドポイント**: `GET http://mailer:8080/internal/mail-requests/{mail_request_id}?tenant_id={uuid}&source_service={name}`
- **認証**: POST と同じ Bearer トークン
- **必須 query**: `tenant_id`, `source_service`（POST body と同じ値）
- **返却フィールド**: `mail_request_id`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `accepted_at`, `delivered_at`, `last_error_code`
- **PII なし**: 宛先・件名・本文は返しません

`status` の値は Worker 配送状態です（`queued`, `processing`, `delivered`, `failed`, `dead_lettered`, `cancelled`）。POST レスポンスの `accepted` / `already_accepted` とは別物です。

存在しない ID、または他 tenant の ID に対しては **404 `NOT_FOUND`** を返します（存在有無を漏らしません）。

POST 直後の例（同じ `request_id` / `tenant_id` / `source_service` を使う）:

```bash
curl -fsS "http://127.0.0.1:5280/internal/mail-requests/${request_id}?tenant_id=00000000-0000-0000-0000-000000000101&source_service=example-service" \
  -H "Authorization: Bearer local-mail-service-token"
```

期待レスポンス（受付直後）:

```json
{
  "mail_request_id": "<request_id>",
  "status": "queued",
  "attempt_count": 0,
  "max_attempts": 3,
  "accepted_at": "2026-07-21T12:00:00Z"
}
```

Worker が配送を完了すると `status` は `delivered` などに変わります。詳細なエラーコード一覧・HTTP ステータス表は [docs/api/openapi.yaml](docs/api/openapi.yaml) と [サービス仕様](docs/service-spec.md#配送ステータス照会get) を参照してください。

Consumer アプリの compose ネットワーク接続例は [infra/deploy/compose.yml](infra/deploy/compose.yml) のコメントを参照してください。

.NET Consumer の full runnable sample（`Amane.Mailer.Contracts` 使用、`payload_hash` 計算、
`accepted` / `already_accepted` / `IDEMPOTENCY_CONFLICT` の分岐を含む）は
[examples/consumer-dotnet/](examples/consumer-dotnet/README.md) を参照してください。
Python Consumer の full runnable sample（既存 Python `payload_hash` helper 使用、local Mailer への
POST、`accepted` / `already_accepted` / `IDEMPOTENCY_CONFLICT` の分岐を含む）は
[examples/consumer-python/](examples/consumer-python/README.md) を参照してください。
Node.js Consumer の full runnable sample（既存 JavaScript `payload_hash` helper 使用、local Mailer への
POST、`accepted` / `already_accepted` / `IDEMPOTENCY_CONFLICT` の分岐を含む）は
[examples/consumer-node/](examples/consumer-node/README.md) を参照してください。

## ブランチ戦略と CI

作業は `feature/**` / `fix/**` → `develop` → `main` の順で進めます。`main`
マージ後は `main` を `develop` に手動同期します。CI はブランチ経路ごとに
重み付けされ、feature push では build/test のみ、`develop` 向け PR では OpenAPI
検証と Native AOT publish smoke まで、`main` 向け PR では amd64 Docker と compose
smoke を含むフル CI が走ります（arm64 Docker は `main` push）。詳細は
[ブランチ戦略と CI 重み付け](docs/ops/branch-and-ci-workflow.md)
[(en)](docs/ops/branch-and-ci-workflow.en.md) と [CONTRIBUTING.md](CONTRIBUTING.md)
を参照してください。

## 主要ドキュメント

- [ブランチ戦略と CI 重み付け](docs/ops/branch-and-ci-workflow.md) [(en)](docs/ops/branch-and-ci-workflow.en.md)
- [サービス仕様](docs/service-spec.md) [(en)](docs/service-spec.en.md)
- [OpenAPI HTTP reference](docs/api/openapi.yaml)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [GHCR image publish 手順](docs/ops/ghcr-image-publish.md) [(en)](docs/ops/ghcr-image-publish.en.md)
- [Release artifact verification](docs/ops/release-artifact-verification.md) [(en)](docs/ops/release-artifact-verification.en.md)
- [設定 README](config/mailer/README.md) [(en)](config/mailer/README.en.md)
- [セキュリティポリシー](SECURITY.md)
