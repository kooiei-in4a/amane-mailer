[English](sqlite-disk-and-retention.en.md)

# SQLite disk / WAL / retention runbook

SQLite の disk 枯渇（`SQLITE_FULL`）と、WAL 肥大・retention 不足による容量圧迫への診断・対処手順です。
HTTP 上は `STORAGE_FULL`（503, `retryable: false`）として busy/locked 由来の
`MAILER_TEMPORARILY_UNAVAILABLE`（503, `retryable: true`）と区別されます。

関連メトリクスの一般的な scrape / アラート設定は
[metrics-and-alerts.md](metrics-and-alerts.md) を参照してください。

## 症状

| 観測 | 意味 |
|------|------|
| Consumer API が 503 / `STORAGE_FULL` / `retryable: false` | 受付・更新パスで SQLITE_FULL。短時間の再試行では解消しない |
| Worker / Sweep / Retention ログに `SQLITE_FULL` を含むメッセージ | バックグラウンド処理が書き込み不能 |
| `mail_queue_oldest_age_seconds` が長時間高い | ready backlog が進まない。disk 枯渇・Worker 停止・provider 障害などの複合要因があり得る |
| Admin `/admin/ops` の Database storage で DB / WAL サイズが大きい | 容量逼迫の予兆 |

## 診断手順

1. **HTTP エラー code を確認する**
   - `STORAGE_FULL` → disk / volume / inode / retention を疑う（本 runbook）
   - `MAILER_TEMPORARILY_UNAVAILABLE` → 一過性の busy/locked。短時間のバックオフ再試行が妥当
2. **ホストの空き容量を確認する**（DB ファイル配置 volume）
3. **Admin ops または CLI で DB / WAL サイズを確認する**
   - Admin: `/admin/ops` → Database storage（Database size / WAL size / Journal mode）
   - CLI: `db stats`（queue / dead letter / heartbeat）と合わせて確認
4. **メトリクスを確認する**
   - `mail_queue_oldest_age_seconds`
   - `mail_queue_ready_count`
   - `mail_worker_heartbeat_age_seconds{component="worker|sweep"}`
5. **ログを確認する**（recipient / subject / body / connection string は出さない）
   - Worker: `due to SQLite storage full (SQLITE_FULL)`
   - Sweep / Retention / Admin audit retention: 同様の区別ログ

## 対処

優先順:

1. **volume の空き容量を確保する**（不要ファイル削除、volume 拡張）
2. **retention を確認・必要なら短縮する**
   - mail request retention: `Mailer:Retention:*` / 関連 env
   - admin audit retention: `MAILER_ADMIN_AUDIT_RETENTION_DAYS`（既定 180 日）
   - 明示 purge: `db admin-audit purge --older-than-days <days>`
3. **WAL を縮退させる**（運用ウィンドウで）
   - プロセス停止時の checkpoint（`MailerWalCheckpointShutdownService`）後にサイズを再確認
   - 運用向け既定コマンド: `db checkpoint`（内部で `PRAGMA wal_checkpoint(TRUNCATE)`）
   - 手動で `PRAGMA wal_checkpoint(TRUNCATE);` を打つ場合はメンテウィンドウとバックアップ方針に従う
4. **容量復旧後に API / Worker の回復を確認する**
   - `/readyz` が 200
   - 新規受付が 202 に戻る
   - `mail_queue_oldest_age_seconds` が低下し始める

## 推奨アラート（disk 枯渇の早期検知）

disk 枯渇専用のゲージは現状ないため、**ready backlog の最古経過秒**を一次シグナルとして使う。

```yaml
groups:
  - name: amane-mailer-storage
    rules:
      - alert: MailQueueOldestAgeHigh
        expr: mail_queue_oldest_age_seconds > 300
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: Ready queue oldest item is older than 5 minutes
          description: >-
            May indicate Worker stall, provider outage, or SQLite disk pressure
            (STORAGE_FULL). Check volume free space, WAL size, and retention.

      - alert: MailQueueOldestAgeCritical
        expr: mail_queue_oldest_age_seconds > 900
        for: 10m
        labels:
          severity: critical
        annotations:
          summary: Ready queue oldest item is older than 15 minutes
          description: >-
            Prolonged backlog. Investigate disk/WAL/retention and Worker health
            before relying on Consumer retries.
```

注意:

- `mail_queue_oldest_age_seconds` だけでは原因を断定できない。必ず `STORAGE_FULL` ログ / HTTP code と volume 空き容量を突き合わせる。
- Consumer SDK は `retryable: true` を自動再試行する。`STORAGE_FULL` は `retryable: false` のため、disk 復旧までは受付失敗が継続する想定。

## セキュリティ

- ログ・Admin・メトリクスに recipient / subject / body / metadata 値 / connection string / token を出さない。
- `/metrics` と Admin は内部ネットワーク境界内で運用する。
