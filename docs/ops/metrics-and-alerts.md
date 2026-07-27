[English](metrics-and-alerts.en.md)

# Prometheus メトリクスとアラート runbook

Amane Mailer の `/metrics` エンドポイントは、キュー滞留・配送結果・Webhook outbox・Worker heartbeat を Prometheus text format で公開します。Admin `/admin/ops` および CLI `db stats` と同じ `MailerDbStatsReader` 由来の gauge（queue / dead letter / heartbeat）と、同じ `DeliveryEventRepository.CountOperationalAsync` 由来の Webhook backlog gauge に加え、プロセス起動以降の counter / histogram を in-memory で保持します。

## エンドポイント

| 項目 | 値 |
|------|-----|
| Path | `GET /metrics` |
| Content-Type | `text/plain; version=0.0.4; charset=utf-8` |
| 既定 | 有効（`Mailer:Metrics:Enabled=true`） |
| 認証 | **Production / Staging 等（非 Development）:** `Mailer:Metrics:Enabled=true` のとき `Mailer:Metrics:BearerToken`（または `MAILER_METRICS_BEARER_TOKEN`）が **起動時必須**。未設定は startup fail-closed。リクエスト時は `Authorization: Bearer <token>` 必須。<br>**Development / local:** bearer は任意。未設定なら anonymous 可（**内部ネットワーク分離前提**）。設定時は同様に Bearer 必須 |
| 無効化 | `Mailer:Metrics:Enabled=false` → **404**（非 Development でも bearer 不要） |
| DB 未 migrate | **503** |

### 設定例

```bash
# Development / local: 任意（内部 NW 前提で anonymous scrape 可）
# export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# Production / Staging: Enabled=true なら必須（未設定は起動失敗）
export MAILER_METRICS_BEARER_TOKEN="replace-with-scrape-token"

# scrape しない場合は無効化（非 Development でも bearer 不要）
export Mailer__Metrics__Enabled=false
```

Compose / systemd では Mailer HTTP ポートを **内部ネットワークのみ** に publish し、Prometheus は同ネットワークまたは VPN から scrape してください。`/healthz`・`/readyz` と同様、インターネット直接公開は想定していません。Staging/Production の `infra/deploy/compose.yml` は `MAILER_METRICS_BEARER_TOKEN` を渡します。トークン未設定のまま metrics を有効にするとプロセスは起動しません。

`ASPNETCORE_ENVIRONMENT=Testing` は WebApplicationFactory など自動テストホスト専用です。実運用の環境名には使わないでください（optional bearer パスに入るため）。

## 公開メトリクス

| メトリクス | 型 | ラベル | 意味 |
|---|---|---|---|
| `mail_requests_accepted_total` | counter | なし | プロセス起動以降に受け付けた mail request 数（再起動で reset） |
| `mail_deliveries_total` | counter | `result`, `provider` | プロセス起動以降の完了 attempt 数。`result` は `delivered` / `failed` / `dead_lettered` |
| `mail_delivery_duration_seconds` | histogram | `provider` | プロセス起動以降の attempt 所要時間（秒）。再起動で reset |
| `mail_queue_ready_count` | gauge | なし | 即時配送可能な queued 件数（全 tenant 合算） |
| `mail_queue_oldest_age_seconds` | gauge | なし | ready backlog 内の最古 updated_at からの経過秒 |
| `mail_retries_total` | counter | なし | プロセス起動以降の再試行 attempt 数（`attempt_number > 1` の完了 attempt） |
| `mail_finalize_skipped_total` | counter | なし | delivered finalize で strict lease fencing（`lock_expires_at` 条件）に失敗した回数。同一 lock での遅延完了や supersede / terminal 競合を含む（**mail request 専用**。Webhook は別 counter） |
| `mail_webhook_finalize_skipped_total` | counter | なし | Webhook `delivery_events` の finalize で strict lease fencing（`lock_expires_at` / lock token）に失敗した回数。通常配送結果のほか、Webhook 未設定・secret 不足・payload 不正などの終端失敗経路も含む |
| `mail_dead_letters_total` | gauge | なし | 現在 dead_lettered 状態の request 数 |
| `mail_webhook_events_pending` | gauge | なし | Webhook outbox の pending / delivering 件数（CLI `webhook_events_pending` と同集計） |
| `mail_webhook_events_dead_lettered` | gauge | なし | Webhook outbox の dead_lettered 件数（CLI `webhook_events_dead_lettered` と同集計） |
| `mail_worker_heartbeat_age_seconds` | gauge | `component` | `worker` / `sweep` の heartbeat 経過秒。行未存在時は series なし |
| `mail_ready` | gauge | なし | 直近の `/readyz` 評価結果（1=ready、0=not ready）。未評価時は series なし |
| `mail_readiness_failure` | gauge | `reason` | 直近 `/readyz` の primary failure reason。固定値のみ（`schema_not_ready` / `worker_not_running` / `sweep_not_running` / `heartbeat_missing` / `heartbeat_stale` / `database_error` / `unexpected_error`）。active な reason のみ 1、他は 0。ready 時はすべて 0。未評価時は series なし |
| `mail_bounce_events_total` | counter | なし | プロセス起動以降に相関済みバウンスを `bounce_events` へ取り込んだ件数（再起動で reset） |
| `mail_bounce_unmatched_total` | counter | なし | プロセス起動以降に `provider_message_id` 相関できなかった件数。相関設計破綻の早期シグナル |
| `mail_bounce_recipient_mismatch_total` | counter | なし | プロセス起動以降にイベント申告宛先と DB 宛先が不一致で破棄した件数 |
| `mail_suppressed_sends_total` | counter | なし | プロセス起動以降に送信前抑制リストでブロックした件数 |
| `mail_provider_queue_poll_failed_total` | counter | なし | プロセス起動以降の ACS Storage Queue ポーリング失敗件数 |
| `mail_provider_events_pending` | gauge | なし | `provider_event_inbox` の pending / processing 件数（CLI `provider_events_pending` と同集計） |
| `mail_provider_events_dead_lettered` | gauge | なし | `provider_event_inbox` の dead_lettered 件数（CLI `provider_events_dead_lettered` と同集計） |

**禁止ラベル（含めない）:** `recipient_email`, `subject`, `mail_request_id`, `tenant_id`, `source_service`

### Admin / CLI との関係

- **Gauge（queue / dead letter / heartbeat）:** CLI `db stats`（tenant 指定なし）および break-glass Admin ops と同じ service-wide 集計。
- **Gauge（webhook pending / dead-letter）:** CLI `db stats`（tenant 指定なし）および Admin ops の **service-wide** webhook 件数と同じ `CountOperationalAsync` 集計。Admin の tenant-scoped dead-letter 件数とは別物。
- **Counter / histogram:** プロセス lifetime 内のイベントのみ。DB に直接 INSERT された履歴は含まれません。再起動後は counter / histogram が 0 から再開します。

## Prometheus scrape 設定例

```yaml
scrape_configs:
  - job_name: amane-mailer
    scrape_interval: 30s
    metrics_path: /metrics
    static_configs:
      - targets:
          - mailer.internal:5280
    # bearer 設定時:
    # authorization:
    #   type: Bearer
    #   credentials: replace-with-scrape-token
```

## 推奨アラート閾値例

```yaml
groups:
  - name: amane-mailer
    rules:
      - alert: MailNotReady
        expr: mail_ready == 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Mailer /readyz is not ready; see mail_readiness_failure for primary reason

      - alert: MailQueueOldestAgeHigh
        expr: mail_queue_oldest_age_seconds > 300
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready queue oldest item is older than 5 minutes

      - alert: MailWorkerHeartbeatStale
        expr: mail_worker_heartbeat_age_seconds{component="worker"} > 120
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: Worker heartbeat is stale

      - alert: MailQueueReadyBacklogSpike
        expr: deriv(mail_queue_ready_count[10m]) > 10
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready backlog is growing quickly

      - alert: MailDeliveryFailureRateHigh
        expr: rate(mail_deliveries_total{result="failed"}[5m]) > 0.1
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Failed delivery attempt rate is elevated

      - alert: MailFinalizeSkipped
        expr: increase(mail_finalize_skipped_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Delivered finalize hit strict lease fencing failure (delayed complete or superseded/terminal race)

      - alert: MailWebhookFinalizeSkipped
        expr: increase(mail_webhook_finalize_skipped_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Webhook delivery-event finalize hit strict lease fencing failure (may re-POST; consumers must dedupe by event_id)

      - alert: MailWebhookBacklogHigh
        expr: mail_webhook_events_pending > 100
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: Delivery-result webhook outbox backlog is elevated

      - alert: MailWebhookDeadLettersPresent
        expr: mail_webhook_events_dead_lettered > 0
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Delivery-result webhook outbox has dead-lettered events

      - alert: MailBounceUnmatchedRising
        expr: increase(mail_bounce_unmatched_total[30m]) > 5
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: Bounce events failed provider_message_id correlation; check ACS Event Grid mapping and mail_attempts.provider_message_id

      - alert: MailProviderEventsPendingHigh
        expr: mail_provider_events_pending > 50
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Bounce provider-event inbox backlog is elevated

      - alert: MailProviderEventsDeadLettersPresent
        expr: mail_provider_events_dead_lettered > 0
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: Bounce provider-event inbox has dead-lettered rows

      - alert: MailProviderQueuePollFailed
        expr: increase(mail_provider_queue_poll_failed_total[15m]) > 0
        for: 0m
        labels:
          severity: warning
        annotations:
          summary: ACS Storage Queue bounce poll failed; check queue credentials and network
```

`mail_deliveries_total` はプロセス内 counter のため、Mailer 再起動直後は `rate()` が短時間不安定になることがあります。queue / heartbeat / webhook backlog アラートを primary、delivery rate は補助として運用してください。バウンス系は [bounce-ingestion-runbook.md](bounce-ingestion-runbook.md) も参照してください。`mail_finalize_skipped_total` は **mail request** の strict lease fencing 失敗の検知用で、増加時は証跡の有無・Delivered 収束・DeadLetter との競合を確認してください。`mail_webhook_finalize_skipped_total` は **Webhook outbox** の finalize fencing 失敗用です。Webhook は at-least-once 契約のため、skip 後に同一 `event_id` の再 POST が起き得ます。増加時は Warning ログの `EventId` / `TenantId` / `MailRequestId` / `FinalizeOutcome` / `FinalizeSkipReason` と webhook backlog を確認し、Consumer 側の `event_id` 重複排除が機能しているかを確認してください。metric / ログには lock token 実値・Webhook URL / secret・payload 本文・recipient 等の PII は含めません。Webhook backlog はメール配送が正常でも通知だけ止まる障害の早期検知用です。Admin 手動リトライは監査付きの明示操作として `attempt_count` をリセットし、旧サイクルの Delivered 証跡を prior-success 収束の対象外にします（#268）。そのため delivered 証跡付きの DeadLetter を再投入すると新サイクルでは実再送されます。同一サイクル内の #238 prior-success 収束は維持されます。再送前に Admin attempt 履歴の `provider_message_id` を確認してください。

## Worker lease と wall-clock jump（#276）

mail / webhook の lease は `TimeProvider.GetUtcNow()` 由来の **wall-clock 絶対時刻**（`lock_expires_at`）で判定します。monotonic clock は使いません。詳細は [service-spec](../service-spec.md) の「Worker / Webhook lease と wall clock（#276）」を参照してください。

| 補正 | 影響 | 観測・緩和 |
|---|---|---|
| 大きな前進（step） | early reclaim / reaper。strict finalize fencing 失敗 | `mail_finalize_skipped_total` / `mail_webhook_finalize_skipped_total` 増加の候補。mail は #238 で再送抑止・Delivered 収束し得る。webhook は同等の prior-success 収束がなく再 POST し得る |
| 大きな後退 | Processing / Delivering の reclaim 遅延 | `expired_processing_count` / webhook backlog / heartbeat age の解釈時に時刻異常も疑う |

通常の NTP slew では稀です。ホスト時刻は slew を優先し、大きな step を避けてください。monotonic lease への再設計は別 ADR 候補です。

## disk 枯渇・WAL・retention

`mail_queue_oldest_age_seconds` の上昇は Worker 停滞や provider 障害に加え、
SQLite disk 枯渇（HTTP `STORAGE_FULL`）の早期シグナルにもなります。
診断・対処・追加の critical 閾値例は
[sqlite-disk-and-retention.md](sqlite-disk-and-retention.md) を参照してください。

## Large DB 上の metrics / retention コスト（#288）

`MailerDbStatsReader` の gauge は `mail_requests` 全件 aggregate です。large DB での
経過時間・EXPLAIN・スクレイプ間隔の目安は
[large-db-query-measurement.md](large-db-query-measurement.md) を参照してください。

## セキュリティ注意

- `/metrics` を公開インターネットに直接露出しない。内部ネットワーク境界は Development でも必須前提。
- 非 Development ではアプリが scrape bearer を起動時に強制する。ネットワーク分離の代替にはならない。
- レスポンスに recipient / subject / mail_request_id / tenant_id は含めない。
- bearer token は scrape 設定と同じ secret 管理境界でローテートする。
- Admin UI（`/admin/ops`）とは別経路。Admin は session 認証 + tenant scope、metrics は ops 向け service-wide。

## ローカル確認

`ASPNETCORE_ENVIRONMENT=Development`（例: `dotnet run`）では bearer 未設定でも可:

```bash
curl -fsS http://127.0.0.1:5280/metrics | head
```

local Docker compose（`ASPNETCORE_ENVIRONMENT=Production`）は既定で
`MAILER_METRICS_BEARER_TOKEN=local-metrics-scrape-token` を渡します:

```bash
curl -fsS -H "Authorization: Bearer local-metrics-scrape-token" http://127.0.0.1:5280/metrics | head
```

任意の bearer 設定時:

```bash
curl -fsS -H "Authorization: Bearer replace-with-scrape-token" http://127.0.0.1:5280/metrics | head
```
