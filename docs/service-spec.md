[English](service-spec.en.md)

# Amane Mailer Service — サービス仕様（SQLite + Native AOT）

- **位置づけ:** 汎用メール送信マイクロサービス
- **HTTP 契約の正本:** `src/Amane.Mailer.Contracts/`（ADR 0012 D-01）
- **公開 HTTP reference:** [openapi.yaml](api/openapi.yaml)（Contracts / runtime に同期される公開 schema）
- **関連:** [ADR 0012](adr/0012-mail-via-mailer-microservice.md)（Mailer マイクロサービス化）
- **ランタイム:** Native AOT 単一バイナリ（`Amane.Mailer`）＋ chiseled コンテナ。PostgreSQL は使用しない。

---

## 1. このサービスは何をするか

> 利用側（App）が組み立てたメールを受け取り、**永続化 → 非同期に ACS/Mailpit で配送**する「配送専用」サービス。
> テンプレートは持たず、宛先・件名・本文は呼び出し側が payload に載せる。

```
App ──HTTP(Bearer)──▶ POST /internal/mail-requests
                          │  受付・冪等チェック・SQLite 永続化 → 202
                          ▼
                     /app/data/mailer.db（送信依頼の正本）
                          │
                     Worker（同一プロセス）が Channel + Sweep で起動
                          ▼
                  ┌── provider 判定 ──┐
        live_sending=false?           │
          ├ acs  → Azure Communication Services
          └ mailpit → Mailpit(SMTP)
```

- API・Worker・Retention・Sweep は **1 プロセス（1 コンテナ）に同居**。
- App とは **HTTP API のみ**で接続。App DB / Mailer DB の相互参照はしない。
- ACS を知るのは本サービスだけ。
- データベースは **SQLite（WAL モード）**。永続化はホスト側 `./data` → コンテナ `/app/data` のボリュームマウント。

---

## 2. インターフェース（HTTP）

HTTP 契約のコード上の正本は `src/Amane.Mailer.Contracts/`。Mailer runtime は同じ DTO / constants を参照し、[openapi.yaml](api/openapi.yaml) は Consumer 向けの HTTP reference / 公開 schema として Contracts / runtime に同期する。要約：

| メソッド | パス | 用途 | 認証 |
|---|---|---|---|
| `POST` | `/internal/mail-requests` | 送信依頼の受付（任意 `scheduled_at`） | テナント Bearer |
| `GET` | `/internal/mail-requests/{mail_request_id}` | 配送ステータス照会（`tenant_id` / `source_service` は query） | テナント Bearer |
| `POST` | `/internal/mail-requests/{mail_request_id}/cancel` | 送信前キャンセル（`queued`；既 `cancelled` は冪等） | テナント Bearer |
| `POST` | `/internal/mail-requests/{mail_request_id}/reschedule` | 予約時刻変更（`queued` かつ `attempt_count=0`） | テナント Bearer |
| `GET` | `/healthz` | 生存確認（liveness） | なし |
| `GET` | `/readyz` | 受付可否（現行 migration schema + Worker/Sweep 稼働・heartbeat 鮮度。provider / ACS 設定検証は含まない＝startup-only） | なし |
| `GET` | `/metrics` | Prometheus メトリクス（ops。詳細は [metrics-and-alerts.md](ops/metrics-and-alerts.md)） | Development: optional bearer（内部 NW 前提）。非 Development: Enabled 時 bearer 必須（startup 強制） |

### 契約同期と drift review

契約変更時は、同一変更内で `src/Amane.Mailer.Contracts/`、runtime 実装、[openapi.yaml](api/openapi.yaml)、関連テストの drift を確認する。対象は Request/Response DTO の property 名・required / nullable、`MailerErrorCodes`、`MailRequestAcceptanceStatus`、`MailRequestStatus`、payload hash 対象、JSON unknown / duplicate property 挙動を含む。

CI は `scripts/validate-openapi.mjs` で OpenAPI の構造を検証し、`scripts/check-contract-drift.mjs` で Contracts / runtime / OpenAPI の drift-specific assertion を実行する。drift check は Contracts DTO / constants を正本として、OpenAPI schema / enum、payload hash 対象、runtime の source-generated JSON 利用、JSON unknown / duplicate property の runtime/test coverage hook を検証する。

契約を意図的に変更する場合は、まず `src/Amane.Mailer.Contracts/` の DTO / constants / payload hash contract を更新し、同じ変更で runtime 実装、[openapi.yaml](api/openapi.yaml)、関連テストを同期する。現時点では再生成が必要な別 snapshot はなく、drift check は DTO / constants の期待値を source から導出する。OpenAPI example の `payload_hash` が変わる場合は再計算し、canonicalization fixture が変わる場合は `tests/Amane.Mailer.Contracts.Tests/TestVectors/payload-hash-vectors.json` も更新する。ローカル確認は `node scripts/validate-openapi.mjs docs/api/openapi.yaml` と `node scripts/check-contract-drift.mjs` を実行する。Contracts package / API versioning policy については「バージョニングポリシー」節を参照。

### 受付レスポンス

| 状況 | HTTP | code / status |
|---|---|---|
| 初回受付 | 202 | `status: accepted` |
| 同一依頼の再送 | 202 | `status: already_accepted` |
| ボディ不正 JSON / 空 / 未知 property / 重複 property | 400 | `INVALID_REQUEST` |
| トークン/テナント不一致 | 401 | `UNAUTHORIZED_TENANT` |
| source_service 許可外 | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| 同一ID・内容差異 | 409 | `IDEMPOTENCY_CONFLICT` |
| ボディ > 256,000 byte | 413 | `REQUEST_TOO_LARGE` |
| 宛先複数 / メタデータ / hash 不一致 | 422 | `TOO_MANY_RECIPIENTS` / `INVALID_METADATA` / `INVALID_PAYLOAD_HASH` / `INVALID_REQUEST` |
| 過去の `scheduled_at` / 最大予約期間超過 | 422 | `SCHEDULED_AT_IN_PAST` / `SCHEDULED_AT_TOO_FAR` |
| 一時的 DB 障害（busy/locked 等） | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk 枯渇（SQLITE_FULL） | 503 | `STORAGE_FULL` (`retryable: false`) |

時刻は API 上 **UTC**。`scheduled_at` は初回配送予定で `next_attempt_at`（再試行）とは独立。省略または null は即時。最大予約期間は受付 / 再スケジュール時点から **30 日**（`MailRequestScheduleLimits.MaxScheduledAhead`）。`scheduled_at` は payload_hash 対象外。

### 配送ステータス照会（GET）

`GET /internal/mail-requests/{mail_request_id}?tenant_id={uuid}&source_service={name}`

| 状況 | HTTP | code / status |
|---|---|---|
| 自 tenant・許可 source_service の既存依頼 | 200 | Worker 配送 `status`（`queued` / `processing` / `delivered` / `failed` / `dead_lettered` / `cancelled`） |
| mail_request_id / tenant_id / source_service 不正・欠落 | 400 | `INVALID_REQUEST` |
| トークン/テナント不一致 | 401 | `UNAUTHORIZED_TENANT` |
| source_service 許可外 | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| 存在しない、または他 tenant の依頼 | 404 | `NOT_FOUND`（存在有無を漏らさない） |
| 一時的 DB 障害（busy/locked 等） | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk 枯渇（SQLITE_FULL） | 503 | `STORAGE_FULL` (`retryable: false`) |

返却 JSON は PII を含まない最小セット（`mail_request_id`, `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `scheduled_at`, `accepted_at`, `delivered_at`, `last_error_code`）。`last_error_code` は delivery attempt の stable taxonomy（`MailDeliveryErrorCodes` / `ProviderErrorClassifier`）のみ。library の exception 型名は使わない。詳細は [SECURITY.md](../SECURITY.md) の Provider Error Sanitization を参照。

### 送信前キャンセル（POST cancel）

`POST /internal/mail-requests/{mail_request_id}/cancel?tenant_id={uuid}&source_service={name}`

| 状況 | HTTP | code / status |
|---|---|---|
| `queued` の依頼をキャンセル | 200 | `status: cancelled`（ステータス JSON） |
| 既に `cancelled`（同一キー再 cancel） | 200 | `status: cancelled`（冪等） |
| クエリ不正 | 400 | `INVALID_REQUEST` |
| トークン/テナント不一致 | 401 | `UNAUTHORIZED_TENANT` |
| source_service 許可外 | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| 存在しない / 他 tenant | 404 | `NOT_FOUND` |
| `queued` / `cancelled` 以外 | 422 | `INVALID_STATE` |
| 一時的 DB 障害（busy/locked 等） | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk 枯渇（SQLITE_FULL） | 503 | `STORAGE_FULL` (`retryable: false`) |

Cancel の DB 更新（`Cancelled` commit）が成功したあと、webhook enqueue や status 再取得の一時失敗で
「未キャンセル」を示す HTTP 失敗を返さない。enqueue は best-effort で、欠落は reconcile が補完し得る。

### 再スケジュール（POST reschedule）

`POST /internal/mail-requests/{mail_request_id}/reschedule?tenant_id={uuid}&source_service={name}`

ボディ: `{ "scheduled_at": "<UTC date-time>|null" }`（null はスケジュール解除＝即時）。

| 状況 | HTTP | code / status |
|---|---|---|
| `queued` かつ `attempt_count=0` で更新成功 | 200 | 更新後ステータス JSON |
| クエリ不正 | 400 | `INVALID_REQUEST` |
| トークン/テナント不一致 | 401 | `UNAUTHORIZED_TENANT` |
| source_service 許可外 | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| 存在しない / 他 tenant | 404 | `NOT_FOUND` |
| ボディ > 256,000 byte | 413 | `REQUEST_TOO_LARGE` |
| 過去時刻 / 30 日超 | 422 | `SCHEDULED_AT_IN_PAST` / `SCHEDULED_AT_TOO_FAR` |
| 許可状態以外 | 422 | `INVALID_STATE` |
| 一時的 DB 障害（busy/locked 等） | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |
| SQLite disk 枯渇（SQLITE_FULL） | 503 | `STORAGE_FULL` (`retryable: false`) |

### metadata の秘密情報ポリシー（docs-first）

`metadata` は **キー名のみ**を検査し、**値の内容は検査しない**（docs-first ポリシー）。

| 検査対象 | 挙動 |
|---|---|
| キー名 | `token` / `password` / `secret` / `url` を含むキーは 422 `INVALID_METADATA` |
| 値 | 送信された文字列をそのまま `metadata_json` に永続化（secret スクラブなし） |
| サイズ | tenant の `metadata_max_bytes`（既定 4096）超過は 422 `INVALID_METADATA` |

Consumer は許可されたキー名でも、secret・Bearer token・パスワード・リセット URL のクエリ secret 等を metadata **値**に入れないこと。受理された metadata は SQLite・backup の対象となり、Admin 等で表示されうる。`subject` / 本文 / `reply_to` と同様、metadata は PII を含みうる。

OpenAPI・Contracts README・`SECURITY.md` と整合する。値の強制拒否（URL クエリ secret パターン等）は本ポリシーでは採用しない。

### 冪等性

- 一意キーは **`(tenant_id, source_service, mail_request_id)`**。
- 同一キーの再送は 202 `already_accepted`、内容（`payload_hash`）が違えば 409。
- `mail_request_id` は利用側生成（UUIDv7 推奨）。

### 配送一意性（実送信の保証）

HTTP 受付の冪等性（上記「冪等性」節）と、**ACS/Mailpit 経由の実メール送信の一意性は別契約**である。Consumer は「同一 `mail_request_id` で必ず 1 通だけ届く（exactly-once）」と期待してはならない。

Mailer の配送セマンティクスは **at-least-once**（同一依頼から複数通の実送信が起こりうる）である。以下は現在の実装に基づく保証の要約である。正本は [ADR 0012 D-07](adr/0012-mail-via-mailer-microservice.md) および本節。

| 対象 | 保証 | 備考 |
|---|---|---|
| HTTP 受付 | at-most-once 永続化 | 同一 `(tenant_id, source_service, mail_request_id)` は 1 行。再 POST は `already_accepted` |
| 実メール配送（全体） | **not exactly-once** / at-least-once | 自動リトライ・手動再送・provider 挙動により重複しうる |
| ACS (`provider=acs`) | 決定論的 operation id（UUIDv5）による**緩和のみ** | `tenant_id` + `source_service:mail_request_id` から生成（`AcsOperationIdFactory`）。ACS サーバ側の重複排除として機能する保証は本リポジトリでは検証・保証しない |
| Mailpit (`provider=mailpit`) | **冪等性なし**（best-effort） | 再送のたびに SMTP 送信が発生しうる（開発/検証向け）。ただし SMTP DATA 受理後の disconnect 失敗だけを理由に再送スケジュールしない（#275） |
| Worker 自動リトライ | at-least-once | retryable 失敗は `Queued` に戻り再配送 |
| lease 失効後の finalize 競合（#238） | **再送抑止** | provider 送信成功の証跡を `mail_attempts` に残し、reclaim 時は実送信をスキップして `Delivered` へ収束。finalize skip は `mail_finalize_skipped_total` で可観測（[metrics runbook](ops/metrics-and-alerts.md)） |
| Admin 手動再送 | **意図的な再配送** | `DeadLettered` / `Failed` から `Queued` へ戻す（`attempt_count` を 0 リセット）。旧サイクルの Delivered 証跡は prior-success 収束に使わない（#268）。provider 送信が成功済みでも row が `Delivered` へ収束していなければ再送されうる（[ADR 0015](adr/0015-manual-retry-cancel-state-transitions.md) at-least-once 維持） |
| 配送結果 Webhook | **first-wins**（同一 mail request 世代で高々 1 event）+ `event_id` による再 POST 冪等 | **実メール送信とは別契約**。最初に enqueue された終端状態のみ通知。Admin 手動再送後の別終端（例: `failed` → 再送 → `delivered`）は webhook を再送しない。Consumer は同一 `event_id` の再 POST を冪等に処理する（[webhook-verification.md](consumer/webhook-verification.md)） |

**Consumer 向け推奨:**

- 業務上の重複通知を避ける必要がある場合は、利用側で `mail_request_id` または独自の相関 ID で重複排除する。
- `GET /internal/mail-requests/{mail_request_id}` で `delivered` を確認しても、既に複数通送信済みの可能性は排除できない（特に Mailpit・手動再送）。
- Admin 手動再送後は status GET と webhook の終端状態が一致しないことがある（webhook は first-wins）。最新状態は status GET を正とする。

### バージョニングポリシー

service release（GitHub Release tag）、Docker image tag、`Amane.Mailer.Contracts` NuGet package、OpenAPI `info.version` はすべて同一の `X.Y.Z` を使用する。1 つのリリースで 4 つが揃う。

| アーティファクト | バージョン形式 | 例 |
|---|---|---|
| GitHub Release / Git tag | `vX.Y.Z` | `v0.1.0` |
| Docker image tag | `vX.Y.Z`（可変）+ `sha-<git-sha>`（不変） | `v0.1.0`, `sha-abc1234` |
| NuGet package (`Amane.Mailer.Contracts`) | `X.Y.Z` | `0.1.0` |
| OpenAPI `info.version` | `X.Y.Z` | `0.1.0` |

deploy では不変タグ `sha-<git-sha>` または digest を優先する。`vX.Y.Z` タグは人が参照する際の識別子として使う。

publish 手順: [docs/ops/ghcr-image-publish.md](ops/ghcr-image-publish.md)、[`.github/workflows/publish-contracts.yml`](../.github/workflows/publish-contracts.yml)

**Contracts package の target framework**

`Amane.Mailer.Contracts` は consumer 互換のため `net8.0` を target する。Mailer runtime は `net10.0` などより新しい framework を target できるが、リリース version (`X.Y.Z`) の同期と target framework は別問題である。runtime が新しい TFM に上がっても、Contracts package が同じ TFM に追随する必要はない。

Contracts package の TFM を引き上げる場合は、CHANGELOG のリリースノートと移行ガイダンスで明記する。consumer アプリの最小 .NET version 要件が上がる変更は、0.x では破壊的変更として扱い、1.0.0 以降は semver に従う。

**0.x ラインの互換性期待値**

0.x リリースは公開 API・contract をまだ安定化中である。後方互換性は保証しないが、破壊的変更は CHANGELOG のリリースノートと移行ガイダンスで明記する。1.0.0 以降は semver の後方互換保証を適用する。

---

## 3. データモデル（SQLite）

正本 DDL: `src/Amane.Mailer/Data/Migrations/001_initial.sql`

### 3.1 `mail_requests` — 送信依頼の正本

| カラム | 型 | 説明 |
|---|---|---|
| `id` | TEXT PK | 内部 UUIDv7 |
| `tenant_id` | TEXT | テナント UUID |
| `source_service` | TEXT | 呼び出し元サービス名 |
| `mail_request_id` | TEXT | 利用側生成の依頼 ID |
| `purpose` | TEXT | 用途ラベル |
| `payload_json` | TEXT | 受信 JSON 原文 |
| `payload_hash` | TEXT | SHA-256 hex（64 文字） |
| `subject` / `html_body` / `text_body` / `reply_to` | TEXT | 配送内容 |
| `recipient_email` / `recipient_display_name` | TEXT | 宛先（現在の API は 1 件） |
| `metadata_json` | TEXT NULL | 任意 metadata |
| `status` | INTEGER | 状態（下表） |
| `attempt_count` / `max_attempts` | INTEGER | 試行回数 |
| `next_attempt_at` | TEXT NULL | 次回試行時刻（UTC ISO8601）。再試行バックオフ専用 |
| `scheduled_at` | TEXT NULL | 初回配送予定（UTC ISO8601）。null = 即時。`next_attempt_at` とは独立 |
| `lock_token` / `lock_expires_at` | TEXT NULL | Worker リース |
| `delivered_at` / `failed_at` / `completed_at` | TEXT NULL | 終端時刻 |
| `accepted_at` / `created_at` / `updated_at` | TEXT | 監査タイムスタンプ |

**一意制約:** `UNIQUE (tenant_id, source_service, mail_request_id)`

**部分インデックス:**

- `idx_mail_requests_queued_due` — `status = 0` を `scheduled_at`, `next_attempt_at`, `created_at` 順
- `idx_mail_requests_processing_expired` — `status = 1` かつ `lock_expires_at` 順

### 3.2 `mail_attempts` — 送信試行履歴

| カラム | 型 | 説明 |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | |
| `request_id` | TEXT FK → `mail_requests.id` | ON DELETE CASCADE |
| `attempt_number` | INTEGER | 1 始まり |
| `provider` | TEXT | `acs` / `mailpit` 等 |
| `status` | INTEGER | 終端状態（2/3/4 のみ） |
| `provider_message_id` | TEXT NULL | ACS operation id（UUIDv5 決定論的生成） |
| `error_code` / `error_message` | TEXT NULL | 失敗詳細 |
| `retryable` | INTEGER | 0/1 |
| `lock_token` | TEXT | 試行時のリース |
| `started_at` / `completed_at` | TEXT | UTC ISO8601 |

### 3.3 `worker_heartbeats` — Worker/Sweep liveness 信号

DDL: `src/Amane.Mailer/Data/Migrations/002_worker_heartbeats.sql`

| カラム | 型 | 説明 |
|---|---|---|
| `name` | TEXT PK | サービス名（`worker` / `sweep`） |
| `last_heartbeat_at` | TEXT | 最終 heartbeat 時刻（UTC ISO8601） |

Worker と Sweep の BackgroundService がそれぞれ定期的に UPSERT する。CLI `healthcheck` と `GET /readyz` は、現行バイナリが必要とする applied migration（version + checksum）を含む schema 準備状況を検証し、Worker 有効時は両 heartbeat 行の存在と鮮度も検証する。鮮度閾値は `Mailer__Healthcheck__MaxHeartbeatStalenessSeconds`（既定 300 秒）。Docker HEALTHCHECK は CLI `healthcheck` を使用する。

### 3.4 状態遷移（`mail_requests.status`）

状態値と Worker 自動遷移の正本は [ADR 0015: 手動再送・手動キャンセル状態遷移](adr/0015-manual-retry-cancel-state-transitions.md) を参照する。以下は service-spec 上の要約である。

| 値 | 名前 | 意味 |
|---|---|---|
| **0** | `Queued` | 受付済み・配送待ち（`scheduled_at` と `next_attempt_at` の両方が到来で claim 対象） |
| **1** | `Processing` | Worker がリース取得・送信中 |
| **2** | `Delivered` | 配送成功（終端） |
| **3** | `Failed` | 非 retryable な provider 失敗（終端） |
| **4** | `DeadLettered` | 最大試行超過等で打ち切り（終端） |
| **5** | `Cancelled` | 運用者による手動キャンセル（終端） |

**Worker 自動遷移:**

```
0 Queued ──claim──▶ 1 Processing ──success──▶ 2 Delivered
                         │
                         ├──retryable fail──▶ 0 Queued (next_attempt_at)
                         ├──terminal fail───▶ 3 Failed
                         └──max attempts────▶ 4 DeadLettered
```

retryable 失敗時は `status` を `Failed` (3) にせず **`Queued` (0) に戻し** `next_attempt_at` を設定する（runtime 実装どおり）。

**Admin 手動操作（ADR 0015 要約）:**

| 操作 | 許可される遷移元 | 遷移先 |
|---|---|---|
| 手動再送 | `DeadLettered`, `Failed` | `Queued`（`attempt_count=0`, `next_attempt_at=NULL`） |
| 手動キャンセル | `Queued`, `Failed`, `DeadLettered`, 期限切れ `Processing` | `Cancelled` |

`Delivered` / 有効 lock 保持中の `Processing` / `Cancelled` からの手動操作は拒否する。詳細な競合ルール・監査・tenant 認可は ADR 0015 を正とする。

### 3.5 配送結果 Webhook（outbound）

テナント JSON の optional `webhook` 設定により、Mailer は terminal 状態
（`delivered` / `failed` / `dead_lettered` / `cancelled`）到達時に
`delivery_events` outbox へ `MailDeliveryEventPayload` を enqueue し、
`WebhookDeliveryWorker` が HMAC 署名付き HTTPS POST で Consumer へ配信する。

- **first-wins:** 同一 `(tenant_id, source_service, mail_request_id)` 世代では高々 1 event
  （`ON CONFLICT DO NOTHING`）。最初に enqueue された終端状態のみが残る。Admin 手動再送で
  別の終端（例: `failed` → `Queued` → `delivered`）へ進んでも、2 つ目の event は作らず、
  既存 event も更新しない。最新終端の再通知が必要なら別 issue / ADR で設計する（本契約の非目標）。
- reconcile は **欠落**（outbox 行が無い terminal request）のみ補完する。既存 event の上書きはしない。
- secret は `webhook.secret_env` が指す環境変数から読み込む（tenant JSON 平文禁止）。
- payload に PII（recipient, subject, body 等）は含めない。
- Consumer 重複排除契約は `event_id`（同一 mail request 世代の webhook 再送で不変）。request retention 後に同一 `mail_request_id` を再利用した場合は新しい `event_id` が発行される。
- 配信失敗時は指数バックオフで再送し、上限超過で webhook Dead Letter として記録する。
- shutdown 中は `stoppingToken` により新規 claim を行わない。インフライト配信は最大 `DeliveryTimeoutSeconds + FinalizeTimeoutSeconds` 待機する（`MailRequestWorker` と同型の drain）。
- SSRF 対策: HTTPS 必須。IPv4 private / loopback / link-local / CGNAT / multicast / reserved、
  IPv4-mapped、IPv6 loopback / link-local / site-local / ULA / multicast / unspecified、
  廃止済み IPv4-compatible IPv6（`::/96`、例: `::10.0.0.1`）、
  および NAT64 well-known prefix（`64:ff9b::/96`）・6to4（`2002::/16`）上の
  private 等ブロック対象 IPv4 埋め込みを拒否。optional `allowed_host_suffixes`。
- 検証手順: [docs/consumer/webhook-verification.md](consumer/webhook-verification.md)
- OpenAPI schema: `MailDeliveryEventPayload`
- Admin / ops 確認: `/admin/webhook-dead-letters`、`db stats` の webhook 件数

---

## 4. 運用 CLI（Native バイナリ）

Web ホスト起動前に `argv` で早期分岐。コンテナ `ENTRYPOINT` は `./Amane.Mailer`（`dotnet` 不要）。

| サブコマンド | 用途 | 終了コード |
|---|---|---|
| `healthcheck` | 現行 SQLite schema（applied migration version + checksum）+ Worker/Sweep heartbeat 鮮度確認（Docker `HEALTHCHECK`） | 0=healthy / 1=unhealthy |
| `db migrate` | 未適用 SQL マイグレーションを適用 | 0=成功 |
| `db checkpoint` | `PRAGMA wal_checkpoint(TRUNCATE)` で `-wal` をクリーンアップ | 0=成功 |
| `db backup <absolute-path>` | オンライン SQLite バックアップ（Backup API）。同ディレクトリの temp へ書いて検証後に destination を atomic replace する。途中失敗時も既存の正常 backup は残る。世代管理のため timestamp 付き path を推奨 | 0=成功 / 2=usage error |
| `db stats [--tenant-id <uuid>]` | SQLite `mail_requests` の status 別件数、ready backlog、oldest queued age、stale processing、dead-letter 件数を `key=value` で出力 | 0=成功 / 1=schema unavailable / 2=usage error |
| `db request-state --tenant-id <uuid> --source-service <name> --mail-request-id <uuid>` | 1 request の状態、attempt 件数、provider message id の有無を `key=value` で出力（secret / recipient は出さない） | 0=成功 / 1=schema unavailable / 2=usage error |

### マイグレーション checksum policy

`db migrate` は各 SQL migration file の byte-level SHA-256 hex checksum を
`schema_migrations.checksum` に保存する。`schema_migrations` は runner-owned
metadata なので、checksum column は番号付き SQL migration ではなく runner が通常
migration 適用前に追加・backfill する。

- 新規 DB では、適用した各 migration の `version`, `applied_at`, `checksum` を同一 transaction で記録する。
- `db migrate` は同一 DB に対して排他的に実行する。複数の migration runner を同時に起動しない。
- `v0.1.0` など checksum column がない既存 DB では、初回の checksum 対応 `db migrate` が `checksum` column を追加し、同梱されている現在の migration files のうち適用済み `version` と一致する行へ checksum を backfill する。その時点より前の historical checksum は存在しないため、この初回 backfill が現在同梱 SQL を信頼の起点にする。
- 以後の `db migrate` は、適用済み `version` の SQL file が同梱されていることと、保存済み checksum が現在の file checksum と一致することを確認する。file 不在または checksum 不一致なら pending migration を適用する前に fail-fast する。
- release 後の SQL migration file は forward-only とし、後編集しない。byte-level checksum の対象なので、内容が同じに見える reformat、改行コード変更、encoding / BOM 変更も checksum mismatch になりうる。schema 変更は新しい番号付き migration を追加する。checksum mismatch が出た場合は、正しい image / SQL file に戻すか、backup からの restore / rebuild 手順を選ぶ。

**例（compose ops）:**

```bash
docker compose --profile ops run --rm mailer-migrate          # db migrate
docker compose exec mailer ./Amane.Mailer db checkpoint
docker compose exec mailer ./Amane.Mailer db backup "/app/data/backups/mailer-$(date -u +%Y%m%dT%H%M%SZ).db"  # 平文。本番運用は backup-mailer.sh を使うこと。固定 path 上書きも途中失敗時は旧 backup を残すが、世代管理には timestamp 付き path を推奨
docker compose exec mailer ./Amane.Mailer db stats --tenant-id <tenant-uuid>
docker compose exec mailer ./Amane.Mailer db request-state --tenant-id <tenant-uuid> --source-service <source-service> --mail-request-id <request-uuid>
```

`db stats` は optional な `--tenant-id <uuid>`（省略時は全 tenant）と、
`--queued-stale-minutes`（default 30）、`--failure-window-minutes`（default 60）、
`--stale-processing-minutes`（default 30）を受け取る。出力は 1 行 1 key の
`key=value` 形式で、host-monitor は次のキーに依存する。

| key | 意味 |
|---|---|
| `as_of_utc` | 集計基準時刻（UTC） |
| `tenant_id` | 対象 tenant UUID、または `all` |
| `status_queued` / `status_processing` / `status_delivered` / `status_failed` / `status_dead_lettered` / `status_cancelled` | `mail_requests.status` 別件数 |
| `ready_backlog_count` | `queued` かつ `next_attempt_at` / `scheduled_at` がいずれも due（null または `<= now`）の件数 |
| `oldest_queued_age_seconds` | ready backlog 内の最古 `updated_at` からの秒数（対象なしは 0） |
| `queued_stale_count` | ready backlog のうち `updated_at` が `--queued-stale-minutes` より古い件数 |
| `stale_processing_count` | `processing` かつ `updated_at` が `--stale-processing-minutes` より古い件数 |
| `expired_processing_count` | `processing` かつ `lock_expires_at <= now` の件数（worker liveness 監視の材料） |
| `recent_failed_count` / `recent_dead_lettered_count` | `--failure-window-minutes` 内の terminal failure 件数 |
| `failed_total` / `dead_lettered_total` / `terminal_total` | terminal failure の累計件数 |
| `worker_heartbeat_age_seconds` | Worker の最終 heartbeat からの経過秒数（行未存在は `-1`） |
| `sweep_heartbeat_age_seconds` | Sweep の最終 heartbeat からの経過秒数（行未存在は `-1`） |
| `webhook_events_pending` / `webhook_events_dead_lettered` | 配送結果 Webhook outbox 件数 |

`db request-state` は no-send / ACS deploy drill などの read-only 検証コマンド。出力は
`tenant_id`, `source_service`, `mail_request_id`, `found`, `status`,
`status_code`, `attempt_count`, `attempt_rows`, `last_provider`,
`last_attempt_status`, `last_attempt_status_code`,
`provider_message_id_present`, `last_error_code`。実宛先、provider message id
実値、本文、metadata は出力しない。

---

## 5. 設定

**原則:** 秘密情報は環境変数、構造・ポリシーは JSON。優先順位は `env > JSON > 既定値`。

### 5.1 秘密情報（環境変数 / `.env`）

| 変数 | 用途 | 例・備考 |
|---|---|---|
| `ConnectionStrings__Mailer` | SQLite 接続文字列 | 既定 `Data Source=/app/data/mailer.db`（未設定時も同値） |
| **`ACS_CONNECTION_STRING_FILE`** | **ACS 接続文字列ファイル** | **Staging/Production deploy（`infra/deploy/compose.yml`）の正本。`admin provider register-acs` が書く `acs_connection_string` を指す。`MAILER_REQUIRE_ACS_SECRET_FILE=true` のとき bare env へのフォールバックはしない** |
| `ACS_CONNECTION_STRING` | ACS 接続文字列（環境変数） | local Mailpit compose、および local ACS drill（`mail-05a-acs-drill.sh` の compose override）向け。Staging/Production の `compose.yml` では参照しない |
| `MAIL_SERVICE_TOKEN_*` | テナント Bearer トークン | `tenants.json` の `token_env` が指定 |
| `MAILER_PROVIDER` | provider グローバル上書き（任意） | `acs` / `mailpit`。未知値は **startup fail-closed**（`/readyz` では再検証しない） |
| `MAILER_TENANTS_PATH` | tenants.json の場所 | 例 `/app/config/mailer/tenants.json` |

### 5.2 Worker / Sweep / Retention（環境変数）

| 変数 | 既定 | 説明 |
|---|---|---|
| `Mailer__Worker__Enabled` | `true` | Worker 系 HostedService の有効化 |
| `Mailer__Worker__BatchClaimSize` | `4` | 1 ドレインあたりの claim 上限 |
| `Mailer__Worker__MaxSendConcurrency` | `4` | 並列送信数 |
| `Mailer__Worker__SendTimeoutSeconds` | `90` | 1 通あたり送信タイムアウト |
| `Mailer__Worker__LeaseDurationSeconds` | `120` | Processing リース TTL |
| `Mailer__Sweep__IntervalSeconds` | `30` | 滞留スイープ間隔 |
| `Mailer__Retention__Days` | `90` | 終端レコード保持日数（`mail_requests` と同一冪等キーの `delivery_events` を同時パージ） |
| `Mailer__Retention__SweepIntervalHours` | `24` | Retention パージ周期 |
| `Mailer__Healthcheck__MaxHeartbeatStalenessSeconds` | `300` | heartbeat stale 判定閾値（秒）。`>= ceil(BatchClaimSize/MaxSendConcurrency) * SendTimeoutSeconds + FinalizeTimeoutSeconds + 30` かつ `> WorkerHeartbeatIntervalSeconds` かつ `> Sweep:IntervalSeconds` |
| `Mailer__Healthcheck__WorkerHeartbeatIntervalSeconds` | `60` | Worker idle 時の heartbeat 更新間隔（秒）。Sweep の更新間隔は `Mailer__Sweep__IntervalSeconds` に従う |

### 5.3 構造・ポリシー（JSON / `tenants.json`）

スキーマは [config/mailer/tenants.schema.json](../config/mailer/tenants.schema.json)。テナント1件あたり：

| フィールド | 意味 |
|---|---|
| `tenant_id` | 環境×プロダクトの UUID |
| `name` | 表示名 |
| `source_services` | 許可する呼び出し元 allowlist |
| `default_from` | 送信元（App からの上書き不可） |
| `token_env` | Bearer トークンの環境変数名 |
| `provider` | `acs` / `mailpit` |
| `live_sending` | 実送信ゲート（fail-closed） |
| `metadata_max_bytes` | metadata 上限（既定 4096） |
| `retry` | `max_attempts` / `initial_delay_seconds` / `max_delay_seconds` |

### 5.4 実送信ゲート（`live_sending`）

- `provider=acs` でも `live_sending=false` のテナントは `LIVE_SENDING_DISABLED` で**送らない**。
- develop / staging は原則 `false`、production のみ `true`。
- effective provider（`MAILER_PROVIDER` override 含む）が `acs` かつ `live_sending=true` のテナントがある場合、ACS 接続文字列（`ACS_CONNECTION_STRING_FILE` / `ACS_CONNECTION_STRING`）が無いと **startup fail-closed**。`live_sending=false` のみの構成では ACS secret は起動時必須ではない（offline `scripts/validate-tenant-config.mjs` と同じ）。provider / ACS のこの検証は startup-only で、`/readyz` には含めない。

---

## 6. デプロイ構成

`infra/deploy/compose.yml` が独立デプロイ単位。**常駐は `mailer` 1 コンテナのみ**（PostgreSQL なし）。

| 要素 | 内容 |
|---|---|
| イメージ | `infra/docker/Dockerfile` — digest-pinned `sdk:10.0-noble-aot` ビルド → digest-pinned `runtime-deps:10.0-noble-chiseled` 実行（[pinning policy](ops/container-image-pinning.md)） |
| データ | `./data:/app/data`（SQLite `mailer.db` + WAL） |
| テナント設定 | host-owned tenant JSON を `MAILER_TENANTS_HOST_PATH` から `MAILER_TENANTS_CONTAINER_PATH`（既定 `/app/config/mailer/tenants.json`）へ read-only mount |
| マイグレーション | `profiles: ops` の `mailer-migrate`（`db migrate`） |
| ヘルスチェック | `HEALTHCHECK CMD ["/app/Amane.Mailer", "healthcheck"]` |
| HTTP | `ASPNETCORE_URLS=http://+:8080` |

**Bootstrap:**

```bash
mkdir -p data
docker compose --env-file .env -f compose.yml config --quiet
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml up -d mailer
```

**バックアップ（PostgreSQL / pg_dump 廃止後）:**

`infra/deploy/backup-mailer.sh` で SQLite バックアップ → age 暗号化 → rclone アップロードを一括実施する。
手順は [バックアップ運用 runbook](ops/backup-operations.md) を参照。

---

## 7. シャットダウン（Graceful Shutdown）

SIGTERM 受信時の運用順序：

1. Generic Host が `ApplicationStopping` を発火し、Kestrel が新規 HTTP 受付を停止する
2. `MailRequestWorker` は新規 claim を止め、既に Semaphore 待ちの後続 wave は `stoppingToken` で送信開始しない（`BatchClaimSize > MaxSendConcurrency` でも同様。未開始の Processing は lease reclaim）。開始済みのインフライト送信のみ最大 `SendTimeoutSeconds + FinalizeTimeoutSeconds` 待機する。`WebhookDeliveryWorker` は新規 claim を止め、インフライト webhook 配信を最大 `DeliveryTimeoutSeconds + FinalizeTimeoutSeconds` 待機する。待機を使い切っても残る場合は warning ログを出す
3. Worker / Sweep / Retention など全 HostedService の `StopAsync` 完了後、`MailerWalCheckpointShutdownService.StoppedAsync` が `PRAGMA wal_checkpoint(TRUNCATE)` を実行する
4. Generic Host が `ApplicationStopped` を発火する

WAL TRUNCATE は shutdown cleanup の best-effort であり、配送 durability は SQLite WAL
自体で担保する。checkpoint が失敗した場合は error log、shutdown timeout で中断された場合は
warning log を出す。

compose は既定で `stop_grace_period=120s` とし、アプリ側 `HostOptions.ShutdownTimeout` は mail / webhook 双方の drain 所要時間の大きい方に slack（15 秒）を加えた値とする（既定設定では mail 側が支配的で `SendTimeoutSeconds + 25秒` 以上）。HostedService の `StopAsync` は既定で逐次（`ServicesStopConcurrently=false`）のため、両 worker が同時に最大長のインフライトを抱える最悪ケースでは `max()` を超える加算待ちになり得る。その場合の打ち切り分は lease 失効後の reclaim / 冪等収束（#238）で吸収する前提であり、`max()` は concurrent drain 仮定ではなく「片側の最大 drain + slack」のホスト上限である。`SendTimeoutSeconds` や webhook `DeliveryTimeoutSeconds` を増やす場合は `MAILER_STOP_GRACE_PERIOD` も併せて増やす。

---

## 8. データ所有

`/app/data/mailer.db` が **送信依頼の正本**（宛先・件名・本文＝PII、送信試行履歴、ACS operation id）。
バックアップは **`db backup` CLI** で同一コンテナから取得。Retention が終端 `mail_requests` と対応する `delivery_events` を自動パージ。

---

## 9. 別リポジトリ化に向けた論点

| ID | 論点 | 現状 / 方針 |
|---|---|---|
| O-04 | HTTP 契約の正本 | **`src/Amane.Mailer.Contracts/`**（ADR 0012 D-01） |
| O-02 | Contracts 配布 | `Amane.Mailer.Contracts` NuGet。OpenAPI は Consumer 向け HTTP reference |
| O-03 | source_service 登録制 | tenants.json allowlist |
| O-06 | 複数プロダクト × ACS | 現状サービス単位 1 本 |
| O-13 | `from` 上書き | 不可 |
| — | 契約バージョニング | service release / Docker image / NuGet package / OpenAPI `info.version` はすべて同一の `X.Y.Z` を使用。詳細は「バージョニングポリシー」節を参照 |

---

## 10. 変更履歴

| Date | 内容 |
|---|---|
| 2026-06-22 | 初版。実装から HTTP 契約と設定仕様を起こす |
| 2026-06-23 | 初回 SQLite / Native AOT リリース仕様に追随: chiseled 単一コンテナ / CLI / Retention / 状態遷移 DDL |
| 2026-06-24 | Worker/Sweep heartbeat liveness 追加: `worker_heartbeats` テーブル、CLI heartbeat 鮮度チェック、`/readyz` Worker 稼働確認、`db stats` heartbeat age keys |
| 2026-06-27 | バージョニングポリシー節追加（#5）。OpenAPI `info.version` を release/package と同一の `0.1.0` に修正 |
| 2026-06-27 | `v0.1.1` patch release 準備として Contracts package と OpenAPI `info.version` を `0.1.1` に更新 |
| 2026-07-03 | ADR 0015 に追随: `Cancelled` 状態、手動再送・手動キャンセル遷移、`Failed` 定義修正 |
| 2026-07-22 | `/readyz` に Worker/Sweep heartbeat 鮮度チェックを追加（#241）。CLI healthcheck と同じ閾値を共有 |
| 2026-07-22 | 配送一意性（実送信の保証）節を追加（#239）。#238 の finalize 証跡・reclaim 収束と整合 |
| 2026-07-22 | `WebhookDeliveryWorker` の shutdown drain（新規 claim 停止 + inflight 待機）を明記（#245） |
| 2026-07-23 | `/readyz` / CLI `healthcheck` が現行 migration version + checksum を要求（#267） |
| 2026-07-23 | Consumer cancel の冪等成功と commit 後 HTTP 失敗の回避（#269） |
| 2026-07-23 | effective provider / ACS live-sending の startup 検証を明記。`/readyz` は含めない（#272）。Mailpit は SMTP 受理後の disconnect 失敗を再送対象にしない（#275） |
| 2026-07-23 | `MailRequestWorker` shutdown: Semaphore 待ち後続 wave は送信開始しない（#271） |
| 2026-07-24 | 配送結果 Webhook の first-wins（最初の終端のみ通知。Admin 手動再送後の再通知なし）を明記（#273） |
