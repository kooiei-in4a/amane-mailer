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

## 適合する用途と境界

公開中の release version / tag の機械可読な正本は [`release/current-public.json`](release/current-public.json) です。README とセットアップ入口は利用者がコマンドを組み立てられるよう current tag を表示しますが、更新時はこの authority を起点にします。`docs/releases/` にある過去 version は historical record であり、current release を示しません。

このサービスは、[サービス仕様](docs/service-spec.md) と [ADR 0019](docs/adr/0019-sqlite-single-process-boundaries.md) に記載された SQLite + 単一 Mailer process / 1 replica の境界を前提にしています。運用上の解釈、計測、scale-out の判断は [Capacity / scaling boundary](docs/ops/capacity-and-scaling.md) を参照してください。

適合する用途:

- Mailpit を使う local / staging のメール配送確認
- 複数の業務アプリからメール配送の責任を分離する self-hosted 構成
- host-local の SQLite volume と、文書化された backup / restore を運用できる single-node 構成
- tenant を同一サービス内の論理境界として管理できる構成

適さない用途:

- active-active、複数 Worker、または水平スケールが必須の構成
- API と配送 Worker を独立した process / deployment としてスケールさせる必要がある構成
- host-local SQLite file、file backup、または single-replica 運用を受け入れられない構成
- 物理的な tenant 分離や、vendor-managed database / SLA をこのサービス自体に要求する構成

これは採用境界であり、capacity・performance・availability SLA の保証ではありません。

## 前提ツール

- [.NET SDK](https://dotnet.microsoft.com/download) — `global.json` で固定したバージョン（ビルド前にファイルの値を確認）
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)

## セットアップ入口

初めて構築するときは **Easy Setup（推奨）** を含む単一入口の
[セットアップ入口](docs/ops/setup-guide.md) [(en)](docs/ops/setup-guide.en.md)
から始めてください（[Easy Setup](docs/ops/setup-guide.md#easy-setup推奨) /
[Manual](docs/ops/setup-guide.md#manual-deployment) /
[Hardened](docs/ops/setup-guide.md#hardened-deployment)）。
詳細手順は既存 runbook へリンクします。判断・順序・安全境界の正本は setup-guide です。

## ローカル検証

リポジトリ root で実行します。

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

formatter / 段階 analyzer の詳細は
[Code quality gates](docs/ops/code-quality-gates.md)
[(en)](docs/ops/code-quality-gates.en.md) を参照してください。

## Mailpit で起動する

v2 の送信には、事前に作成された Sender と managed API Key が必要です。
Sender/API Key の Setup UI は #732 の対象で、この変更には含まれません。

local compose は Mailer イメージを build し、Mailpit を起動します。

```powershell
docker compose -f infra/docker/docker-compose.local.yml up -d --build --wait mailer
```

ローカル URL:

- Mailer health: <http://127.0.0.1:5280/healthz>
- Mailer readiness: <http://127.0.0.1:5280/readyz>
- Mailpit UI: <http://127.0.0.1:8025/>

Consumer は `MAILER_API_KEY` に managed API Key を設定します。API Key が
Sender を選択するため、Consumer から tenant / From / provider は指定しません。
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

- [Upgrade / rollback ガイド](docs/ops/upgrade-guide.md) [(en)](docs/ops/upgrade-guide.en.md)
- [ローカル deploy rehearsal](docs/ops/local-deploy-rehearsal-runbook.md) [(en)](docs/ops/local-deploy-rehearsal-runbook.en.md)
- [ACS secret / platform-owned sender 登録 CLI](docs/ops/register-acs-cli-runbook.md) [(en)](docs/ops/register-acs-cli-runbook.en.md)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [リストア手順](docs/ops/restore-procedure.md) [(en)](docs/ops/restore-procedure.en.md)
- [リストア検証](docs/ops/restore-verification.md) [(en)](docs/ops/restore-verification.en.md)

v1.3.8 publish 後の GHCR イメージ（既定 `ghcr.io/kooiei-in4a/amane-mailer:v1.3.8`）を clean state から
pull して Mailer + Mailpit を起動し、`/healthz`・`/readyz`・正常 POST・Mailpit 到着・冪等再送・
conflict・401・403 を自動 smoke するには **Linux local Docker 上** で
`scripts/release-smoke.sh`（サポート対象の canonical entrypoint）を使います。
Windows Docker Desktop 上での release smoke live 実行は **サポート対象外** です。
`scripts/release-smoke.ps1` は shell 版と同一 contract を保つ PowerShell 実装として維持し、
contract 検証は Linux 上の self-test（`release-smoke-preflight-self-test.ps1` 等）で行います。
手順と設定は [公開 release イメージ smoke](docs/ops/release-image-smoke.md) [(en)](docs/ops/release-image-smoke.en.md) を参照してください。
公開 identities は [v1.3.8 release record](docs/releases/v1.3.8.md) /
[GitHub Release](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.8) です。

v1.3.8 release の GHCR runtime image は **`linux/amd64` only** です。
現在公開中の release tag は `v1.3.8` ですが、smoke 実行時は `MAILER_IMAGE_TAG` または
`MAILER_IMAGE_DIGEST` を明示指定してください（暗黙 default はありません）。
release notes または Docker manifest で platform を確認し、必要に応じて
`MAILER_IMAGE_PLATFORM=linux/amd64` を明示してください。

```bash
MAILER_IMAGE_TAG=v1.3.8 bash scripts/release-smoke.sh
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

**公式 Consumer SDK（TypeScript / Python）**: v2 リクエストビルダー、型付きエラー、503 リトライを含む SDK は [sdk/](sdk/README.md) を参照してください。

- **エンドポイント**: `POST http://mailer:8080/api/mail-requests`
- **認証**: `Authorization: Bearer <managed API key>`
- **JSON上の必須フィールド**: `mail_request_id`, `purpose`, `subject`
- **宛先要件**: `to` / `cc` / `bcc` の全 role 合計で1件以上。各 role は未指定・`null`・空配列を0件として扱います。
- **本文要件**: `html_body` / `text_body` の少なくとも一方が必要です。
- **冪等性**: `(API Key が選択する Sender, mail_request_id)`。payload identity は server-side で計算します。

ローカル compose 起動後は、host から次の smoke request を実行できます。
`mail_request_id` は冪等キーなので、同じ依頼として再送したい場合以外は毎回新しい UUID を使います。
`uuidgen` がない環境では、`request_id` に任意の UUID 文字列を設定してください。

```bash
request_id="$(uuidgen)"

curl -i -X POST http://127.0.0.1:5280/api/mail-requests \
  -H "Authorization: Bearer ${MAILER_API_KEY}" \
  -H "Content-Type: application/json" \
  -d @- <<JSON
{
    "mail_request_id": "${request_id}",
    "purpose": "FormResponseNotification",
    "to": [
      { "email": "admin@example.com" }
    ],
    "subject": "New response",
    "text_body": "A new response arrived."
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
payload identity 対象フィールドを変更して POST してください（hash は Mailer が計算します）。
期待結果は `409 Conflict` / `IDEMPOTENCY_CONFLICT` です。

### 配送ステータスの照会（GET）

`202 Accepted` は「依頼を受け付けた」ことだけを示します。Worker による実際の配送結果（`delivered` / `failed` など）は GET で確認します。

- **エンドポイント**: `GET http://mailer:8080/api/mail-requests/{mail_request_id}`
- **認証**: POST と同じ managed API Key（その Sender 所有 request のみ）
- **返却フィールド**: `mail_request_id`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `scheduled_at`, `accepted_at`, `delivered_at`, `last_error_code`
- **任意**: POST の `scheduled_at`（UTC）で予約送信。送信前キャンセル / 再スケジュールは OpenAPI の `/cancel`・`/reschedule` を参照
- **PII なし**: 宛先・件名・本文は返しません

`status` の値は Worker 配送状態です（`queued`, `processing`, `delivered`, `failed`, `dead_lettered`, `cancelled`, `delivery_unknown`）。POST レスポンスの `accepted` / `already_accepted` とは別物です。

`delivery_unknown` は終端 status です。provider invocation 開始後に provider acceptance を証明できなかった状態で、未送信または安全に retry 可能であることを意味しません。同じ `mail_request_id` の配送を Mailer が自動・手動で再送することはありません。これは Consumer SDK の一時的な HTTP 503 retry、同じ JSON の idempotent POST retry、新しい `mail_request_id` を使った重複可能性評価済みの業務上の再送とは別概念です。

存在しない ID、または他 Sender の ID に対しては **404 `NOT_FOUND`** を返します（存在有無を漏らしません）。

POST 直後の例（同じ `request_id` と managed API Key を使う）:

```bash
curl -fsS "http://127.0.0.1:5280/api/mail-requests/${request_id}" \
  -H "Authorization: Bearer ${MAILER_API_KEY}"
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

.NET Consumer の full runnable sample（`Amane.Mailer.Contracts` 使用、
`accepted` / `already_accepted` / `IDEMPOTENCY_CONFLICT` の分岐を含む）は
[examples/consumer-dotnet/](examples/consumer-dotnet/README.md) を参照してください。
Python Consumer の full runnable sample（local Mailer への
POST、`accepted` / `already_accepted` / `IDEMPOTENCY_CONFLICT` の分岐を含む）は
[examples/consumer-python/](examples/consumer-python/README.md) を参照してください。
Node.js Consumer の full runnable sample（local Mailer への
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

- [セットアップ入口](docs/ops/setup-guide.md) [(en)](docs/ops/setup-guide.en.md)
- [ブランチ戦略と CI 重み付け](docs/ops/branch-and-ci-workflow.md) [(en)](docs/ops/branch-and-ci-workflow.en.md)
- [サービス仕様](docs/service-spec.md) [(en)](docs/service-spec.en.md)
- [OpenAPI HTTP reference](docs/api/openapi.yaml)
- [Consumer SDKs](sdk/README.md)
- [Prometheus メトリクスとアラート](docs/ops/metrics-and-alerts.md) [(en)](docs/ops/metrics-and-alerts.en.md)
- [バックアップ運用](docs/ops/backup-operations.md) [(en)](docs/ops/backup-operations.en.md)
- [GHCR image publish 手順](docs/ops/ghcr-image-publish.md) [(en)](docs/ops/ghcr-image-publish.en.md)
- [Release artifact verification](docs/ops/release-artifact-verification.md) [(en)](docs/ops/release-artifact-verification.en.md)
- [設定 README](config/mailer/README.md) [(en)](config/mailer/README.en.md)
- [セキュリティポリシー](SECURITY.md)
