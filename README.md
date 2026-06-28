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

## Admin UI

`AMANE_ADMIN_ENABLED=true` を設定すると `/admin` が有効になります（既定は無効）。
管理画面は **内部ネットワーク向け・experimental** な運用補助ツールです。公開インターネットへの
直接公開は想定していません。production では reverse proxy、firewall、または Docker port publish
制限をネットワーク境界として設定してください。

**現時点の制約（[ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md) の方針に対して未実装）**

- login throttle は in-memory のみ（プロセス再起動でリセット）
- server-side session store なし（cookie auth のみ）、管理者無効化・認証情報変更時の即時 session 失効は未実装
- 管理者ごとの tenant scope なし（単一 `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`）
- audit log は body view と login 成功/失敗を `admin_audit_events` SQLite テーブルに永続化（stdout にもミラー）。logout / session expired / login rate limited、retention sweep、network identifier の hash 化は未実装（[#6](https://github.com/kooiei-in4a/amane-mailer/issues/6) で追跡中）

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
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [リストア手順](docs/ops/restore-procedure.md) [(en)](docs/ops/restore-procedure.en.md)
- [リストア検証](docs/ops/restore-verification.md) [(en)](docs/ops/restore-verification.en.md)

公開済みの GHCR イメージ（既定 `ghcr.io/kooiei-in4a/amane-mailer:v0.1.1`）を clean state から
pull して Mailer + Mailpit を起動し、`/healthz`・`/readyz`・正常 POST・Mailpit 到着・冪等再送・
conflict・401・403 を自動 smoke するには `scripts/release-smoke.sh` を使います。手順と設定は
[公開 release イメージ smoke](docs/ops/release-image-smoke.md) [(en)](docs/ops/release-image-smoke.en.md) を参照してください。

既定タグ `v0.1.1` の公開 GHCR runtime image は **`linux/amd64` only** です。Apple Silicon /
ARM Linux ホストでは、Docker Desktop などの amd64 emulation が使える場合に限り
`MAILER_IMAGE_PLATFORM=linux/amd64 bash scripts/release-smoke.sh` のように明示して検証できます。
multi-arch release では Docker manifest または release notes の platform / runtime manifest digest
を確認し、対象 platform ごとに `MAILER_IMAGE_PLATFORM=linux/amd64` または
`MAILER_IMAGE_PLATFORM=linux/arm64` を指定して smoke してください。

```bash
bash scripts/release-smoke.sh
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

## Consumer クイックスタート

起動した Mailer にメール送信依頼を POST するための最低限の情報です。

- **エンドポイント**: `POST http://mailer:8080/internal/mail-requests`
- **認証**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - ローカル既定トークン: `local-mail-service-token`
- **必須フィールド**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `to`, `subject`, `payload_hash`
- **`payload_hash`**: 配送フィールドの canonical JSON SHA-256。
  .NET は `Amane.Mailer.Contracts` の `MailPayloadHasher` を使用。
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

Consumer アプリの compose ネットワーク接続例は [infra/deploy/compose.yml](infra/deploy/compose.yml) のコメントを参照してください。

## 主要ドキュメント

- [サービス仕様](docs/service-spec.md) [(en)](docs/service-spec.en.md)
- [OpenAPI HTTP reference](docs/api/openapi.yaml)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [GHCR image publish 手順](docs/ops/ghcr-image-publish.md) [(en)](docs/ops/ghcr-image-publish.en.md)
- [Release artifact verification](docs/ops/release-artifact-verification.md) [(en)](docs/ops/release-artifact-verification.en.md)
- [設定 README](config/mailer/README.md) [(en)](config/mailer/README.en.md)
- [セキュリティポリシー](SECURITY.md)
