[English](capacity-and-scaling.en.md)

# Capacity / scaling boundary

この文書は、Amane Mailer を採用・運用するときの現行 architecture envelope と、
負荷計測または別 architecture の検討を始める境界をまとめます。これは benchmark、
サイジング表、SLA、または性能保証ではありません。

設計判断の正本は [ADR 0019](../adr/0019-sqlite-single-process-boundaries.md)、runtime と
設定の正本は [service spec](../service-spec.md) と実装です。この文書はそれらを運用向けに
要約し、新しい上限を定義しません。

## Supported architecture envelope

現行の supported deployment は次の境界を前提にします。

| 面 | 現行の境界 | 運用上の意味 |
|---|---|---|
| Process / replica | API、mail Worker、Sweep、Retention、Webhook / bounce 関連 worker を同じ Mailer process の **1 replica** で実行 | API と Worker の独立 deploy、複数 Worker、active-active、水平 scale-out は対象外 |
| Persistence | 1つの host-local SQLite file（WAL）を正本にする | 複数 Mailer process からの共有 file 利用、managed PostgreSQL、分散 DB は対象外 |
| Durable backlog | mail request と処理状態を SQLite に永続化 | process-local Channel は容量 1 の work signal であり、永続 queue や backlog size 上限ではない。signal drop は Sweep / polling で補完する |
| Tenant model | 同一 service / SQLite 内の論理 `tenant_id` 分離 | tenant ごとの物理 DB、独立 restore、独立 performance isolation は提供しない |
| Deployment storage | `infra/deploy/compose.yml` は host path を `/app/data` に mount | host affinity、volume durability、disk capacity、復旧可能性は operator が設計・検証する |

この envelope の外側を「設定変更だけで対応可能」と扱わないでください。特に replica 数を
増やすことや API / Worker を分離することは、claim、lease、fencing、Admin state、shutdown、
backup / restore の再設計を必要とします。

## Worker と queue の境界

Mail Worker の主要な設定値は次のとおりです。正確な既定値、許容範囲、cross-field validation は
[service spec §5.2](../service-spec.md#52-worker--sweep--retention環境変数) を確認してください。

| 設定 | 既定 | 起動時に受理する範囲 | 意味しないもの |
|---|---:|---:|---|
| `Mailer__Worker__BatchClaimSize` | 4 | 1–100 | 1 drain の claim 上限。秒間配送数や durable queue 容量ではない |
| `Mailer__Worker__MaxSendConcurrency` | 4 | 1–64 | 単一 process 内の provider send 同時実行上限。保証 throughput、provider quota、tenant ごとの割当ではない |
| `Mailer__Worker__SendTimeoutSeconds` | 90 | 1–600 | 1 provider invocation の timeout。配送 latency の保証ではない |
| `Mailer__Worker__LeaseDurationSeconds` | 120 | 1–86400 | claim lease。batch、concurrency、send / finalize timeout との整合条件があり、自由な tuning knob ではない |

これらは **configured technical limits** です。許容範囲内の値が、あらゆる workload、provider、
CPU、memory、disk で qualification 済みという意味ではありません。値を増やす場合は lease、
healthcheck heartbeat、shutdown drain、`MAILER_STOP_GRACE_PERIOD` の制約も維持し、実際の workload
で DB contention、backlog、provider result、shutdown を再計測してください。

関連する逐次処理にも注意が必要です。

- outbound delivery-result Webhook は 1 claim → 1 delivery → finalize の逐次処理です。
  `Mailer__Webhook__ReconcileBatchSize` は欠落 event の検索件数であり、delivery concurrency では
  ありません。遅い endpoint は後続 tenant に head-of-line blocking を起こし得ます
  （[ADR 0018](../adr/0018-webhook-delivery-sequential-concurrency.md)）。
- optional bounce ingestion worker も process 内 worker です。Storage Queue は provider event の
  pull transport であり、mail delivery Worker を外部 queue 化・水平 scale-out するものでは
  ありません。

## SQLite と availability の境界

SQLite は WAL、`BEGIN IMMEDIATE`、`busy_timeout`、DB row の lock token / lease fencing を使います。
これらは単一 file / 単一 process での整合性設計であり、write contention や disk latency を
消すものではありません。API、Worker、metrics aggregate、Retention、Admin / CLI DB 操作は同じ
SQLite file を使います。行数や同時負荷が増えると、full-scan query と write lock 競合を含む
実 workload で評価が必要です。

1 replica のため、Mailer process、host、または volume が利用不能な間に別 replica が自動で
引き継ぐ active-active / failover はありません。`/healthz` と `/readyz` はその instance の状態を
示す health signal であり、availability SLA ではありません。

[backup runbook](backup-operations.md) は稼働中 instance から SQLite Online Backup API で一貫した
backup を作成しますが、[restore procedure](restore-procedure.md) は Mailer を停止して DB file を
置換します。backup の存在だけでは RPO / RTO を保証しません。operator は対象 storage、backup
周期、offsite copy、restore 所要時間、呼び出し元の停止を含む recovery を検証してください。

## Provider throttling と backpressure

`MaxSendConcurrency` は Mailer process 内の同時 send 数だけを制限します。ACS / SMTP の契約、
送信者 reputation、tenant 別 quota、日次上限、provider latency は定義しません。provider ごとの
制限を確認し、production 相当の観測に基づいて concurrency と受入負荷を決めてください。

ACS の HTTP 429 は内部では retryable な provider failure に分類されますが、現行の provider
submission 境界では invocation 前に durable evidence を記録し、`Started` 以降の同一 request を
自動再送しません。曖昧な結果は `DeliveryUnknown` に収束し得ます。この内部分類を、同一
`mail_request_id` の安全な再送または Mailer による adaptive throttling の保証と解釈しないで
ください。正確な配送一意性と再送境界は [service spec の「配送一意性」](../service-spec.md#配送一意性実送信の保証)
を参照してください。

HTTP `202 Accepted` は SQLite に依頼が永続化されたことを示し、provider が処理できる速度を
保証しません。流入が配送速度を上回れば durable backlog が増えます。呼び出し元は status と
運用 signal を監視し、自らの admission control / backpressure 方針を持つ必要があります。

## 既存の測定記録をどう扱うか

repository には限定された測定・qualification がありますが、いずれも production の
end-to-end mail throughput や最大容量を定義しません。

| 記録 | 証明する範囲 | 外挿してはいけないもの |
|---|---|---|
| [Large DB query measurement](large-db-query-measurement.md) | 1環境の synthetic SQLite seed に対する特定 query / retention batch と EXPLAIN 形状 | 最大 DB size、request rate、同時 API / Worker 負荷、SLA |
| [ADR 0018 synthetic Webhook HOL measurement](../adr/0018-webhook-delivery-sequential-concurrency.md) | 人工的 endpoint delay で逐次 Webhook の HOL を再現 | production endpoint latency、mail delivery throughput、availability |
| [Issue #532 Docker memory qualification](../cd/reports/2026-08-04-issue-532-docker-memory-qualification.md) | 記録された Docker/cgroup 条件での attachment envelope memory boundary | 一般的な mail rate、tenant 数、DB 容量、全 workload の memory / performance |

したがって、この repository は TPS、mail/秒、mail/日、最大 tenant 数、最大 SQLite size、
latency percentile、availability percentage、RPO / RTO、SLA を公開保証していません。

## 導入前の capacity qualification

production 採用前に、operator 自身の目標と production 相当環境で少なくとも次を確認してください。

1. accept latency、ready backlog / oldest queued age、配送完了時間、disk 使用量、backup / restore、
   recovery の目標を workload ごとに定義する。
2. 1 replica、実際の CPU / memory / volume、実際の provider quota、代表的な tenant / recipient / body /
   attachment mix で、平常時と burst を計測する。
3. `/metrics` と `db stats` の `ready_backlog_count`、`oldest_queued_age_seconds`、heartbeat、failure、
   disk / WAL、および provider 側 throttle を観測する。
4. backup を取得し、使い捨て環境で restore verification を行い、backup の鮮度と実測した
   restore 所要時間が operator の RPO / RTO 目標に適合するか確認する。
5. 設定変更ごとに lease / health / shutdown の validation を通し、変更前後を同じ条件で比較する。

## Scale-out を検討する trigger

次のいずれかが文書化された要件または測定結果として成立した場合は、設定範囲の上端へ単純に
tuning するのではなく、[ADR 0019 D-02 / D-03](../adr/0019-sqlite-single-process-boundaries.md#d-02-本格設計へ進む-trigger測定可能な条件)
に従って後継 ADR と tracking Issue を作成してください。

- 1 DB / 1 process が、定義済みの throughput、latency、backlog、availability、RPO / RTO 目標を
  production 相当の測定で継続的に満たせない。
- active-active、複数 Worker、API / Worker の独立 lifecycle または独立 scaling が必須になる。
- host-local SQLite / file backup / host affinity が platform または security 要件に適合しない。
- managed PostgreSQL、tenant ごとの物理 DB / restore boundary が必須になる。

後継設計では少なくとも claim atomicity / fairness、lease time source、finalize fencing、queue signal、
migration ownership、Admin state、shutdown / in-flight、backup / restore / DR、SQLite からの移行を
まとめて定義する必要があります。これらを決めずに replica 追加や Worker 分離を行う構成は
supported ではありません。
