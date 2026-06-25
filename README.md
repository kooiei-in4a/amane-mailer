# Amane Mailer

[English README](README.en.md)

Amane Mailer は汎用メール送信マイクロサービスです。送信依頼を受け付けて永続化し、
バックグラウンド Worker が Azure Communication Services (ACS) または Mailpit 経由で
非同期に配送します。Consumer アプリは本文・宛先・件名を組み立てて送信依頼を POST するだけです。

## 構成

- `src/Amane.Mailer`: ASP.NET Core / Native AOT の Mailer サービス。
- `src/Amane.Mailer.Contracts`: DTO、error constants、payload hash helper の NuGet パッケージ。
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

`infra/deploy/drills/` 配下の MAIL-05a drill helper script は、SQLite Mailer CLI
（`healthcheck`、`db stats`、`db request-state`）と一時的な curl compose client を使います。
詳細は [docs/ops/drills/mail-05a-drill-guide.html](docs/ops/drills/mail-05a-drill-guide.html)
を参照してください。ACS 実送信なしの local deploy rehearsal は
[ローカル deploy rehearsal runbook](docs/ops/local-deploy-rehearsal-runbook.md) [(en)](docs/ops/local-deploy-rehearsal-runbook.en.md) を使います。

## Contracts パッケージ

`Amane.Mailer.Contracts` は
[`.github/workflows/publish-contracts.yml`](.github/workflows/publish-contracts.yml) から
GitHub Packages へ手動 publish します。`workflow_dispatch` で明示的な version
（例: `1.0.0-alpha.1`）を指定してください。

## Consumer クイックスタート

起動した Mailer にメール送信依頼を POST するための最低限の情報です。

- **エンドポイント**: `POST http://mailer:8080/internal/mail-requests`
- **認証**: `Authorization: Bearer <MAIL_SERVICE_TOKEN>`
  - ローカル既定トークン: `local-mail-service-token`
- **必須フィールド**: `tenant_id`, `source_service`, `mail_request_id`, `purpose`, `to`, `subject`, `payload_hash`
- **`payload_hash`**: 配送フィールドの canonical JSON SHA-256。
  .NET は `Amane.Mailer.Contracts` の `MailPayloadHasher` を使用。
  アルゴリズム仕様・エラーコード・冪等性: [docs/api/openapi.yaml](docs/api/openapi.yaml)

ローカル compose 起動後は、host から次の smoke request をそのまま実行できます。

```bash
curl -i -X POST http://127.0.0.1:5280/internal/mail-requests \
  -H "Authorization: Bearer local-mail-service-token" \
  -H "Content-Type: application/json" \
  -d '{
    "tenant_id": "00000000-0000-0000-0000-000000000101",
    "mail_request_id": "00000000-0000-0000-0000-000000000201",
    "source_service": "example-service",
    "purpose": "FormResponseNotification",
    "to": [
      { "email": "admin@example.com" }
    ],
    "subject": "New response",
    "text_body": "A new response arrived.",
    "payload_hash": "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9"
  }'
```

期待レスポンスは `202 Accepted` と次の JSON です。

```json
{
  "mail_request_id": "00000000-0000-0000-0000-000000000201",
  "status": "accepted"
}
```

Consumer アプリの compose ネットワーク接続例は [infra/deploy/compose.yml](infra/deploy/compose.yml) のコメントを参照してください。

## 主要ドキュメント

- [サービス仕様](docs/service-spec.md) [(en)](docs/service-spec.en.md)
- [OpenAPI contract](docs/api/openapi.yaml)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [GHCR image publish 手順](docs/ops/ghcr-image-publish.md) [(en)](docs/ops/ghcr-image-publish.en.md)
- [設定 README](config/mailer/README.md) [(en)](config/mailer/README.en.md)
