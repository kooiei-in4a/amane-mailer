[English](large-db-query-measurement.en.md)

# Large DB query measurement（#288）

`/metrics`・Admin ops・CLI `db stats` の全件 aggregate、retention の batched DELETE、
主要 Admin list / dead-letter COUNT を、seeded SQLite 上で再現可能な形で測るための
メモです。本番変更ではなく **計測ハーネス + EXPLAIN + 結果記録** が目的です。

関連:

- [metrics-and-alerts.md](metrics-and-alerts.md)
- [sqlite-disk-and-retention.md](sqlite-disk-and-retention.md)

## ハーネスの実行（CI では既定スキップ）

常時 CI に載せるのは EXPLAIN 形状テストのみです。

```powershell
# Always-on（Release テストに含まれる）
dotnet test Amane.Mailer.slnx -c Release --no-build `
  --filter "FullyQualifiedName~MailerLargeDbQueryPlanTests"
```

large seed の経過時間計測は環境変数で opt-in します（未設定時は `Assert.Skip`）。

```powershell
$env:AMANE_LARGE_DB_BENCH = '1'
# optional: default 50000
$env:AMANE_LARGE_DB_BENCH_ROWS = '50000'
# optional: summary file path (default: %TEMP%\amane-large-db-bench-last.txt)
$env:AMANE_LARGE_DB_BENCH_OUT = (Join-Path (Get-Location) 'artifacts/large-db-measurement-summary.txt')

dotnet test Amane.Mailer.slnx -c Release --no-build `
  --filter "FullyQualifiedName~MailerLargeDbMeasurementTests"
```

計測対象:

| 操作 | 実装 |
|------|------|
| Metrics / ops / `db stats` aggregate | `MailerDbStatsReader.LoadStatsAsync` |
| Admin queued list（LIMIT 50） | `MailRequestRepository.ListForAdminAsync` |
| Admin dead-letter COUNT | `MailRequestRepository.CountDeadLettersForAdminAsync` |
| Retention 1 batch（LIMIT 100） | `MailRequestRepository.DeleteExpiredCompletedAsync` |

シードは合成データのみ（`bench-n@example.com` 等）。サマリに recipient / subject / body /
provider raw error は出ません。

## 代表結果（2026-07-24、Windows / .NET 10.0.10）

条件: `row_count=50000`、単一テナント合成シード、`ANALYZE` 後、temp ファイル DB。

| 指標 | 値 |
|------|-----|
| DB size | ~39 MB |
| Seed | ~4323 ms |
| `LoadStatsAsync` | ~102 ms |
| `ListForAdminAsync`（status=Queued, page 50） | ~6 ms |
| `CountDeadLettersForAdminAsync` | ~2 ms |
| `DeleteExpiredCompletedAsync`（batch 100） | ~92 ms（deleted=100） |

絶対値はディスク・CPU・同時負荷で変わります。相対関係の目安:

- Admin status list（index あり）は数十 ms 未満が期待値。
- Metrics aggregate は行数にほぼ比例する full scan 系（下記 EXPLAIN）。
- Retention 1 batch は select + dual DELETE のため list より重いが、既定 batch 100 なら
  同条件で ~100 ms 前後。

### EXPLAIN QUERY PLAN（同条件）

Metrics aggregate（`mail_requests` 全件 CTE + COUNT）:

```text
SCAN mail_requests USING COVERING INDEX sqlite_autoindex_mail_requests_1
```

→ ユニーク制約の covering index を辿る **全件スキャン**。status 部分 index では
各 `COUNT(CASE ...)` を一括では賄えない。

Retention SELECT（status IN (2,3,4,5) + `completed_at` 範囲 + ORDER BY `completed_at`, id）:

```text
SEARCH mail_requests USING INDEX idx_mail_requests_status_updated (status=?)
USE TEMP B-TREE FOR ORDER BY
```

→ `idx_mail_requests_status_updated` で status 探索するが、`completed_at` 順は
TEMP B-TREE。`idx_mail_requests_deadletter_completed`（status=4 専用）は multi-status
retention には使われない。

Admin queued list / dead-letter COUNT は既存の Admin index（`MailerAdminIndexMigrationTests`）
を利用する想定。常時 EXPLAIN 回帰は `MailerLargeDbQueryPlanTests` を参照。

## 閾値・運用上の注意

| 面 | 注意 |
|----|------|
| `/metrics` scrape | aggregate は行数比例。スクレイプ間隔を極端に短くしない（例: 15–60s）。Admin ops / `db stats` も同コスト。 |
| Retention | batch ループ全体の wall time と Worker/API との lock 競合は未計測。メンテウィンドウでは elapsed を別途見る。 |
| Cancelled | `LoadStatsAsync` は Cancelled をカウントしない。retention は Cancelled を含む。 |
| PII | ハーネス結果・本ドキュメントに実メールや本文を載せない。 |

## Follow-up（index）

明らかなギャップ: retention の `ORDER BY completed_at ASC, id ASC` 向けの
multi-status / `completed_at` index が無い（TEMP B-TREE）。候補例:

```sql
CREATE INDEX IF NOT EXISTS idx_mail_requests_retention_completed
    ON mail_requests (completed_at ASC, id ASC)
    WHERE status IN (2, 3, 4, 5) AND completed_at IS NOT NULL;
```

本 issue（#288）では計測と文書化まで。index 追加は別変更（migration + EXPLAIN 更新 +
retention 回帰）として扱う。ギャップを追跡可能にするには、maintainer が tracking
GitHub issue を立てるか、上記 migration を直接 land すること（#288 AC の
「follow-up issue または修正」経路）。Metrics の full scan は仕様上の全件 gauge なので、
行数増大時は scrape 間隔・DB サイズ監視で緩和し、必要なら別設計（増分カウンタ等）を検討する。

## 再現手順チェックリスト

1. `AMANE_LARGE_DB_BENCH=1` で計測テストを実行する。
2. `AMANE_LARGE_DB_BENCH_OUT`（または temp の last ファイル）のサマリを確認する。
3. EXPLAIN 行が「全件 SCAN（metrics）」「status index + TEMP B-TREE（retention）」であることを確認する。
4. 結果をこのドキュメントの表に必要なら更新する（ホスト差は注記する）。
