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
| `POST` | `/internal/mail-requests` | 送信依頼の受付 | テナント Bearer |
| `GET` | `/healthz` | 生存確認（liveness） | なし |
| `GET` | `/readyz` | 受付可否（DB schema 確認込み readiness） | なし |

### 契約同期と drift review

契約変更時は、同一変更内で `src/Amane.Mailer.Contracts/`、runtime 実装、[openapi.yaml](api/openapi.yaml)、関連テストの drift を確認する。対象は Request/Response DTO の property 名・required / nullable、`MailerErrorCodes`、`MailRequestAcceptanceStatus`、`MailRequestStatus`、payload hash 対象、JSON unknown / duplicate property 挙動を含む。

現行 CI は `scripts/validate-openapi.mjs` で OpenAPI の構造を検証する。自動 drift check 追加までの運用として、HTTP 契約変更 PR は Contracts DTO / constants、runtime 実装、OpenAPI schema / examples、関連テスト・test vectors を比較した結果を validation notes に残す。OpenAPI を変更した場合は `node scripts/validate-openapi.mjs docs/api/openapi.yaml` の結果も残す。DTO / enum / error code と OpenAPI schema の自動 drift check は後続タスクとして追加する。JSON strictness（unknown / duplicate property）は #22 を参照。Contracts package / API versioning policy については「バージョニングポリシー」節を参照。

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
| 一時的 DB 障害 | 503 | `MAILER_TEMPORARILY_UNAVAILABLE` (`retryable: true`) |

### 冪等性

- 一意キーは **`(tenant_id, source_service, mail_request_id)`**。
- 同一キーの再送は 202 `already_accepted`、内容（`payload_hash`）が違えば 409。
- `mail_request_id` は利用側生成（UUIDv7 推奨）。

### バージョニングポリシー

service release（GitHub Release tag）、Docker image tag、`Amane.Mailer.Contracts` NuGet package、OpenAPI `info.version` はすべて同一の `X.Y.Z` を使用する。1 つのリリースで 4 つが揃う。

| アーティファクト | バージョン形式 | 例 |
|---|---|---|
| GitHub Release / Git tag | `vX.Y.Z` | `v0.1.0` |
| Docker image tag | `vX.Y.Z`（可変）+ `sha-<git-sha>`（不変） | `v0.1.0`, `sha-abc1234` |
| NuGet package (`Amane.Mailer.Contracts`) | `X.Y.Z` | `0.1.0` |
| OpenAPI `info.version` | `X.Y.Z` | `0.1.0` |

deploy では不変タグ `sha-<git-sha>` または digest を優先する。`vX.Y.Z` タグは人が参照する際の識別子として使う。

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
| `next_attempt_at` | TEXT NULL | 次回試行時刻（UTC ISO8601） |
| `lock_token` / `lock_expires_at` | TEXT NULL | Worker リース |
| `delivered_at` / `failed_at` / `completed_at` | TEXT NULL | 終端時刻 |
| `accepted_at` / `created_at` / `updated_at` | TEXT | 監査タイムスタンプ |

**一意制約:** `UNIQUE (tenant_id, source_service, mail_request_id)`

**部分インデックス:**

- `idx_mail_requests_queued_due` — `status = 0` かつ `next_attempt_at` 順
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

Worker と Sweep の BackgroundService がそれぞれ定期的に UPSERT する。CLI `healthcheck` が別プロセスから両行の存在と鮮度を検証し、Docker HEALTHCHECK の判定に使用する。

### 3.4 状態遷移（`mail_requests.status`）

| 値 | 名前 | 意味 |
|---|---|---|
| **0** | `Queued` | 受付済み・配送待ち |
| **1** | `Processing` | Worker がリース取得・送信中 |
| **2** | `Delivered` | 配送成功（終端） |
| **3** | `Failed` | 再試行可能な失敗（`next_attempt_at` 設定で 0 に戻る場合あり） |
| **4** | `DeadLettered` | 最大試行超過等で打ち切り（終端） |

```
0 Queued ──claim──▶ 1 Processing ──success──▶ 2 Delivered
                         │
                         ├──retryable fail──▶ 0 Queued (next_attempt_at)
                         ├──terminal fail───▶ 3 Failed
                         └──max attempts────▶ 4 DeadLettered
```

---

## 4. 運用 CLI（Native バイナリ）

Web ホスト起動前に `argv` で早期分岐。コンテナ `ENTRYPOINT` は `./Amane.Mailer`（`dotnet` 不要）。

| サブコマンド | 用途 | 終了コード |
|---|---|---|
| `healthcheck` | SQLite schema + Worker/Sweep heartbeat 鮮度確認（Docker `HEALTHCHECK`） | 0=healthy / 1=unhealthy |
| `db migrate` | 未適用 SQL マイグレーションを適用 | 0=成功 |
| `db checkpoint` | `PRAGMA wal_checkpoint(TRUNCATE)` で `-wal` をクリーンアップ | 0=成功 |
| `db backup <absolute-path>` | オンライン SQLite バックアップ（Backup API） | 0=成功 / 2=usage error |
| `db stats [--tenant-id <uuid>]` | SQLite `mail_requests` の status 別件数、ready backlog、oldest queued age、stale processing、dead-letter 件数を `key=value` で出力 | 0=成功 / 1=schema unavailable / 2=usage error |
| `db request-state --tenant-id <uuid> --source-service <name> --mail-request-id <uuid>` | 1 request の状態、attempt 件数、provider message id の有無を `key=value` で出力（secret / recipient は出さない） | 0=成功 / 1=schema unavailable / 2=usage error |

**例（compose ops）:**

```bash
docker compose --profile ops run --rm mailer-migrate          # db migrate
docker compose exec mailer ./Amane.Mailer db checkpoint
docker compose exec mailer ./Amane.Mailer db backup /app/data/backups/mailer.db  # 平文。本番運用は backup-mailer.sh を使うこと
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
| `status_queued` / `status_processing` / `status_delivered` / `status_failed` / `status_dead_lettered` | `mail_requests.status` 別件数 |
| `ready_backlog_count` | `queued` かつ `next_attempt_at IS NULL OR next_attempt_at <= now` の件数 |
| `oldest_queued_age_seconds` | ready backlog 内の最古 `updated_at` からの秒数（対象なしは 0） |
| `queued_stale_count` | ready backlog のうち `updated_at` が `--queued-stale-minutes` より古い件数 |
| `stale_processing_count` | `processing` かつ `updated_at` が `--stale-processing-minutes` より古い件数 |
| `expired_processing_count` | `processing` かつ `lock_expires_at <= now` の件数（worker liveness 監視の材料） |
| `recent_failed_count` / `recent_dead_lettered_count` | `--failure-window-minutes` 内の terminal failure 件数 |
| `failed_total` / `dead_lettered_total` / `terminal_total` | terminal failure の累計件数 |
| `worker_heartbeat_age_seconds` | Worker の最終 heartbeat からの経過秒数（行未存在は `-1`） |
| `sweep_heartbeat_age_seconds` | Sweep の最終 heartbeat からの経過秒数（行未存在は `-1`） |

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
| **`ACS_CONNECTION_STRING`** | **ACS の接続文字列** | **provider=acs のとき必須** |
| `MAIL_SERVICE_TOKEN_*` | テナント Bearer トークン | `tenants.json` の `token_env` が指定 |
| `MAILER_PROVIDER` | provider グローバル上書き（任意） | `acs` / `mailpit` |
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
| `Mailer__Retention__Days` | `90` | 終端レコード保持日数 |
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

---

## 6. デプロイ構成

`infra/deploy/compose.yml` が独立デプロイ単位。**常駐は `mailer` 1 コンテナのみ**（PostgreSQL なし）。

| 要素 | 内容 |
|---|---|
| イメージ | `infra/docker/Dockerfile` — `sdk:10.0-noble-aot` ビルド → `runtime-deps:10.0-noble-chiseled` 実行 |
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
2. `MailRequestWorker` がインフライト送信を最大 `SendTimeoutSeconds + 10秒` 待機する
3. Worker / Sweep / Retention など全 HostedService の `StopAsync` 完了後、`MailerWalCheckpointShutdownService.StoppedAsync` が `PRAGMA wal_checkpoint(TRUNCATE)` を実行する
4. Generic Host が `ApplicationStopped` を発火する

WAL TRUNCATE は shutdown cleanup の best-effort であり、配送 durability は SQLite WAL
自体で担保する。checkpoint が失敗した場合は error log、shutdown timeout で中断された場合は
warning log を出す。

compose は既定で `stop_grace_period=120s` とし、アプリ側 `HostOptions.ShutdownTimeout` は worker 設定から `SendTimeoutSeconds + 25秒` 以上に設定する。`SendTimeoutSeconds` を増やす場合は `MAILER_STOP_GRACE_PERIOD` も併せて増やす。

---

## 8. データ所有

`/app/data/mailer.db` が **送信依頼の正本**（宛先・件名・本文＝PII、送信試行履歴、ACS operation id）。
バックアップは **`db backup` CLI** で同一コンテナから取得。Retention が終端レコードを自動パージ。

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
