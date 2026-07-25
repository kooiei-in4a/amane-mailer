# ADR 0019: SQLite／単一プロセス前提の維持と PostgreSQL／Worker 分離の着手境界

- **Status:** Accepted
- **Date:** 2026-07-25
- **Tracks:** [#363](https://github.com/kooiei-in4a/amane-mailer/issues/363)
- **Renumbered:** 旧ファイル名 `0018-sqlite-single-process-boundaries.md`。#385（Webhook 逐次維持 ADR）と同番号で develop に並んだため、後着の本 ADR を 0019 に振り直した。
- **Implementation follow-up:** なし（本 ADR は着手条件と非目標の明文化のみ。trigger 成立時に **別 ADR／tracking Issue** を起こす）
- **Preserves:** [ADR 0012 D-07](0012-mail-via-mailer-microservice.md)（Worker 1 レプリカ）、[ADR 0013 D-11](0013-admin-threat-model-and-pii-policy.md)（Admin 単一プロセス）、[ADR 0014](0014-admin-session-tenant-throttle-audit-design.md)（session／throttle の SQLite 正本）
- **Aligns with:** [service-spec](../service-spec.md)（SQLite + Native AOT 単一コンテナ）、[release-notes-checklist](../ops/release-notes-checklist.md)（single-node / single-replica）

## Context

調査基準の意図は `develop@da748f598f4ab0f19cd479d1210e887967a78263`。本 ADR の事実表は 2026-07-25 時点の `develop` 系実装に基づく。

現行の正しさは **SQLite ファイル正本 + 単一 Mailer プロセス** に強く依存する。PostgreSQL や Worker 別 process 化を「将来あるかも」だけで generic interface に隠すと、transaction／lease／signal の実際の意味が見えにくくなり、現在の堅牢性を損なう可能性がある。

#363 は architecture decision の準備であり、**現時点で SQLite abstraction や generic repository を実装しない**。本 ADR は着手 trigger・必要な契約・非目標を固定し、過剰抽象化を防ぐ。

### 現行前提一覧（実装との対応）

| # | 前提 | 根拠（現行実装／既存 ADR） | 備考 |
|---|------|---------------------------|------|
| P1 | Write transaction は `BEGIN IMMEDIATE` | `SqliteImmediateTransaction` | DEFERRED の lock upgrade での `SQLITE_BUSY` を避ける |
| P2 | WAL + `busy_timeout` + `foreign_keys` | `SqliteConnectionFactory.ApplyPragmasAsync`（WAL / `synchronous=NORMAL` / `busy_timeout=5000` / FK ON） | 単一 DB ファイル運用の前提 |
| P3 | claim／lease／lock token fencing の正本は DB 行 | `MailRequestClaimStore` / `DeliveryEventRepository`（`lock_token` + `lock_expires_at` 条件付き UPDATE） | process 内メモリは正本にしない |
| P4 | lease は wall-clock UTC 絶対時刻 | service-spec（lease 節）+ `TimeProvider` の `@Now` | clock skew／jump の影響は既知（#276） |
| P5 | claim fairness は `ORDER BY created_at ASC LIMIT 1` の単一行 claim | `TryClaimOneAsync` | 複数 Worker での公平性・steal は未設計 |
| P6 | process-local Channel は永続 queue ではなく work signal | `MailRequestQueue` / `WebhookDeliveryQueue`（bounded DropWrite、`SingleReader=true`） | 信号欠落は Sweep／polling で補完する設計 |
| P7 | API・Worker・Sweep・Retention・Webhook worker は同一プロセス | service-spec §1、hosted services | 独立 deploy は非対象 |
| P8 | Worker は 1 レプリカ固定 | ADR 0012 D-07、compose／ops checklist | 単一 SQLite ファイル共有の複数 Worker 水平化は運用対象外 |
| P9 | Admin session／login throttle／audit の正本は同一 SQLite | ADR 0013 D-11、ADR 0014 | in-memory はキャッシュのみ |
| P10 | Admin DB ops の二重実行防止は process-local `SemaphoreSlim` 等 | `AdminDbOpsService` | 複数プロセスでは不十分（ADR 0013 D-11 が再設計を要求） |
| P11 | shutdown／in-flight は process-local tracker | `InflightTracker`、Worker／Webhook shutdown drain | 別 process への hand-off はない |
| P12 | migration は SQLite 向け forward-only SQL バンドル | `SqlMigrationRunner`、`Data/Migrations/*.sql` | 他 dialect の ownership は未定義 |
| P13 | backup／checkpoint は SQLite Online Backup／WAL TRUNCATE | CLI `db backup`、`DbCheckpointCommand`、shutdown WAL checkpoint | PostgreSQL／共有 storage の DR は別設計 |

上表は #363 受入条件の「SQLite／single-process 前提の一覧が現行実装と一致している」を満たすための正本とする。実装が変わった場合は本表を更新する。

## Decision

### D-01. trigger 未成立のあいだは現行設計を維持する

**判断:** 下表の着手 trigger（D-02）が **いずれも未成立** のあいだは、SQLite + 単一 Mailer プロセス設計を維持する。PostgreSQL 導入、Worker process 分離、複数 instance、永続化抽象化の実装 Issue を起こさない。

**理由:**

1. 現行の claim／finalize／Admin／shutdown の正しさは SQLite semantics と単一プロセスに依存している。
2. 早期の generic repository／dialect abstraction は実際の transaction 境界を隠し、回帰コストが大きい。
3. Native AOT／自己完結デプロイの利点を、需要確定前に外部 DB／外部 queue 依存へ置き換えない。

### D-02. 本格設計へ進む trigger（測定可能な条件）

次の **いずれか** が、maintainer によって **文書化された要件または測定結果** として確定した場合のみ、本格設計（後継 ADR + tracking Issue）へ進む。

| # | Trigger | 測定／確定の例 |
|---|---------|----------------|
| T1 | 1 DB／1 process では処理量または可用性 SLO を満たせない | 文書化された SLO（例: p95 accept latency、ready backlog 上限、RPO/RTO）に対し、現行構成の負荷試験または本番観測で継続的に未達 |
| T2 | active-active または複数 Worker instance が必要 | HA／failover 要件書、または単一障害点の排除が受け入れ条件になった deployment 計画 |
| T3 | managed PostgreSQL を必須とする deployment 要件 | 顧客／基盤ポリシーで SQLite ファイル永続化が禁止、または managed PG が唯一の承認ストア |
| T4 | SQLite file 運用・backup・host affinity が受入不能 | ops／security レビューで host-local volume・file backup が却下された記録 |
| T5 | tenant ごとの DB 分離が必要 | 認可境界を超える物理分離（tenant 単位 DB／encryption／restore）が必須要件になった場合（論理 `tenant_id` では不足） |
| T6 | Web/API process と delivery Worker を独立 deploy する必要 | API と Worker のスケール／ライフサイクルを分離する運用要件が確定 |

**非 trigger（これだけでは着手しない）:**

- 「将来スケールするかも」という推測のみ
- 他サービスが PostgreSQL を使っているという類似性のみ
- repository パターンへの好みや一般的なクリーンアーキテクチャ論のみ

### D-03. trigger 成立時に定義する契約と検証項目

本格設計開始時は、実装の前に **後継 ADR** で次を定義する。本 ADR では採否を決めない。

| # | 契約領域 | 定義すべき内容 | 検証の観点（例） |
|---|----------|----------------|------------------|
| C1 | claim の atomicity と fairness | 単一行／バッチ claim、順序、steal、重複 claim 防止 | 並行 claim の一意性・飢餓の有無 |
| C2 | lease time source と clock skew | 時刻源、skew 許容、lease 延長の有無 | 時計ずれ下の二重配送／stale finalize |
| C3 | finalize fencing | lock token／世代の条件付き更新、失効後の証跡 | lease 失効後の finalize skip／再送抑止 |
| C4 | retry／dead-letter の transaction 境界 | 状態更新と attempt 証跡・audit の同一 TX 要否 | 部分コミット後の不整合 |
| C5 | queue signal の代替 | Channel 以外（polling、LISTEN/NOTIFY、external queue 等）と欠落補完 | 信号欠落下でも Sweep で進捗すること |
| C6 | migration ownership | 誰が schema を適用するか、rolling deploy 順序、互換窓 | forward-only／rollback 方針 |
| C7 | Admin session／throttle の共有範囲 | プロセス横断の正本、二重 checkpoint／backup 防止 | 複数 API プロセスでの session 失効一貫性 |
| C8 | shutdown／in-flight | drain、lease 返却、独立 Worker の停止順序 | デプロイ中の二重送信・ロスト更新 |
| C9 | backup／restore／DR | RPO/RTO、一貫性点、tenant 分離 restore | restore 後の lease／session 安全性 |
| C10 | SQLite deployment からの migration path | データ移行、二系統並行、切替／切戻し | 切替時の idempotency／重複配送 |

### D-04. 先行して導入してはいけない抽象化（現行の非対象）

trigger 未成立のあいだ、次を **導入しない**（Issue 化もしない）。

| 禁止（先行導入） | 理由 |
|------------------|------|
| `IMailRequestRepository` 等の巨大 generic interface | SQLite 固有の TX／SQL 意味がインターフェース裏に隠れ、実装が薄いラッパになる |
| SQL dialect abstraction（同一 SQL を PG/SQLite 両対応） | lease／claim SQL の意味が dialect で変わる。早期共通化は誤りを隠す |
| EF Core 導入 | Native AOT／trimming・migration モデル・学習コスト。需要未確定 |
| Npgsql（または他 PG ドライバ）の先行追加 | 未使用依存と AOT 表面積の増加 |
| Redis／external queue の先行追加 | Channel は signal であり、外部 queue は C5 の設計後に決める |
| Worker process 分離の部分実装 | P6–P11 の前提を壊す。C5/C7/C8 なしの分離は危険 |
| 複数 Mailer instance 対応の部分実装 | ADR 0012 D-07／0013 D-11 と矛盾。C1–C3/C7 が先 |

局所的な内部リファクタ（例: SQLite 固有ストア内のファイル分割）は、上記の横断抽象を導入しない限り本 ADR の禁止対象外とする。

### D-05. 将来設計開始時の手続き

1. D-02 のいずれかの trigger を **Issue／ADR 本文に証拠付きで記録**する。
2. **新しい tracking Issue** と **後継 ADR**（例: PostgreSQL 永続化、または Worker 分離）を作成する。本 ADR を直接「実装チケット」にしない。
3. 後継 ADR で D-03 の C1–C10 を埋め、採択する永続化／プロセスモデルを決める。
4. 実装は後継 ADR 採択後の分割 Issue で行う（想定 15–30 人日以上は別計画）。

> 本セッションでは Issue 作成が禁止のため、後継 Issue の文面は作成しない。trigger 成立時に maintainer が起こす。

## Rejected Alternatives（本判断時点）

| 案 | 却下理由 |
|----|----------|
| 今すぐ PostgreSQL 対応を実装する | trigger 未成立。コスト 15–30 人日超を先取りする |
| 今すぐ Worker を別 process にする | Channel／in-flight／Admin ロックが process-local。契約未定義 |
| 薄い `IRepository` を先に切る | YAGNI。現行 SQLite SQL の意味を隠すだけ |
| 「将来用」に Npgsql／EF／Redis を依存追加だけする | 未使用依存・AOT・レビュー負荷。D-04 で禁止 |
| ADR 0012 D-07／0013 D-11 を本 Issue で撤回する | 需要と代替設計なしの撤回は回帰 |

## Consequences

- _positive:_ 現行 SQLite／単一プロセス前提が一覧化され、実装との対応が追跡できる。
- _positive:_ 着手 trigger が測定可能な条件になり、「なんとなく将来対応」での抽象化を止められる。
- _positive:_ trigger 成立時に定義すべき契約（C1–C10）がチェックリストになる。
- _negative:_ PostgreSQL／水平スケール需要が来たとき、設計リードタイムが必要（意図的）。
- _operational:_ trigger 未成立のあいだは SQLite single-replica 運用文書（service-spec／release checklist）を正とし続ける。

## References

- [#363 design/architecture: PostgreSQL対応／Worker分離の着手条件と非目標](https://github.com/kooiei-in4a/amane-mailer/issues/363)
- [ADR 0012 D-07: Worker 1 レプリカ](0012-mail-via-mailer-microservice.md)
- [ADR 0013 D-11: Admin 単一プロセス](0013-admin-threat-model-and-pii-policy.md)
- [ADR 0014: Admin session／throttle／audit](0014-admin-session-tenant-throttle-audit-design.md)
- [docs/service-spec.md](../service-spec.md)
- [docs/ops/release-notes-checklist.md](../ops/release-notes-checklist.md)
- `src/Amane.Mailer/Data/Sqlite/SqliteImmediateTransaction.cs`
- `src/Amane.Mailer/Data/Sqlite/SqliteConnectionFactory.cs`
- `src/Amane.Mailer/Data/Sqlite/MailRequestClaimStore.cs`
- `src/Amane.Mailer/Queue/MailRequestQueue.cs`
- `src/Amane.Mailer/Worker/InflightTracker.cs`
- `src/Amane.Mailer/Admin/AdminDbOpsService.cs`
