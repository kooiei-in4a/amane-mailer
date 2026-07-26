# ADR 0018: Webhook 配送の逐次維持（制限付き並列化の見送り）

- **Status:** Accepted
- **Date:** 2026-07-25
- **Tracks:** [#361](https://github.com/kooiei-in4a/amane-mailer/issues/361)
- **Implementation follow-up:** なし（並列化を採用しない。再評価 trigger 成立時に新 Issue / ADR を起こす）
- **Aligns with:** [service-spec §3.5](../service-spec.md#35-配送結果-webhookoutbound)、[ADR 0017](0017-webhook-first-wins-delivery-cycle.md)（契約は変更しない）
- **Preserves:** `WebhookDeliveryWorker` の 1 claim → 1 delivery → finalize 逐次ループ、lock-token fencing、at-least-once

## Context

調査基準の意図は `develop@da748f598f4ab0f19cd479d1210e887967a78263`。本 ADR の事実は 2026-07-25 時点の `develop` 系実装と、同日に追加した synthetic HOL harness に基づく。

### 現行実装

| 項目 | 現行 |
|------|------|
| Claim | `DeliveryEventRepository.TryClaimOneAsync` が `ORDER BY created_at ASC LIMIT 1` |
| Worker | `WebhookDeliveryWorker` は 1 件 claim → HTTP 配送 → finalize を **await 完了してから**次へ |
| 並列度 | 配送経路に concurrency 設定なし（`Mailer:Webhook:ReconcileBatchSize` は reconcile 検索件数。配送 claim batch ではない。旧 `BatchClaimSize` は deprecated alias） |
| Lease | `LeaseDurationSeconds > DeliveryTimeoutSeconds + FinalizeTimeoutSeconds(10)`（**単一** in-flight 前提） |
| Shutdown | 新規 claim 停止 + 最大 1 件分の `DeliveryTimeout + FinalizeTimeout` drain |
| 比較対象 | `MailRequestWorker` は `BatchClaimSize` + `MaxSendConcurrency` の制限付き並列 |

グローバル FIFO claim のため、遅い tenant の event が先に並ぶと、後続の速い tenant は **head-of-line (HOL) blocking** を受ける。

### #361 の採用判断基準（Issue 原文）

並列化は次のいずれかが確認された場合のみ採用候補:

1. 想定最大 backlog で SLO を満たさない
2. 1 つの遅い tenant が他 tenant を継続的に阻害する
3. current deployment の CPU / memory / SQLite write 競合に十分な余裕がある

Issue は根拠なしの `Task.WhenAll` 追加を禁止し、実装は別 Issue へ分割する。

## Measurement

### 条件（synthetic）

| 項目 | 値 |
|------|-----|
| Harness | `tests/Amane.Mailer.Tests/Webhooks/WebhookDeliverySequentialBenchmarkTests.cs` |
| DB | 一時 SQLite + 本番同等 migration / `TryClaimOneAsync` / `FinalizeAsync` |
| Seed | slow tenant 8 件 → fast tenant 8 件（`created_at` 昇順） |
| Latency | slow=25ms / fast=2ms（HTTP なし。endpoint 遅延のみを模擬） |
| Sequential | concurrency=1（現行 worker と同型） |
| Parallel（比較のみ） | concurrency=4（製品コードは変更しない） |
| PII | synthetic tenant / payload のみ。recipient / subject / body なし |
| 出力 | テスト標準出力 + `%TEMP%/amane-webhook-hol-bench-last.txt`（`AMANE_WEBHOOK_HOL_BENCH_OUT` で上書き可） |

CI でも常時実行する（遅延合計は数百 ms 級）。数値の絶対値は OS スケジューラで揺れるが、HOL 不等式は回帰として固定する。

### モデル（解析）

逐次 FIFO では、fast tenant の最初の claim 開始は、それより前に並んだ event の latency 合計に概ね比例する。本 harness では earlier = slow x 8 なので名義値は `8 * 25ms = 200ms`。

想定最大 backlog `B`・遅い endpoint 比率 `r`・timeout 近傍 latency `T_slow` のとき、逐次の最悪系 wall time はおよそ `B * (r * T_slow + (1-r) * T_fast)`。並行度 `C` でも tenant fairness 無しなら同一 tenant の遅い event が slot を埋め、他 tenant の改善は限定的。

### 実測結果（代表・ローカル実行 2026-07-25）

絶対 ms は環境依存。不等式と比率が正本。再実行は同 harness（出力は `%TEMP%/amane-webhook-hol-bench-last.txt`）。

| 指標 | Sequential (C=1) | Parallel sim (C=4) |
|------|------------------|--------------------|
| first_fast_start_ms | 721.5 | 202.6 |
| total_ms | 1068.1 | 630.4 |
| fast complete p95_ms | 1067.0 | 473.3 |
| max_inflight | 1 | 4 |
| claim_ms_sum | 306.3 | 1110.5 |
| finalize_ms_sum | 425.3 | 504.5 |
| hol_ratio (first_fast seq/par) | 3.56 | — |

名義 lower bound `0.7 * 8 * 25 = 140ms` に対し sequential first_fast は十分上（SQLite claim/finalize オーバーヘッド込み）。parallel は HOL を緩和するが claim_ms_sum が増える（同時 claim 競合の兆候）。

回帰 assertion:

1. `sequential.FirstFastStartMs >= 0.7 * SlowEventCount * SlowLatencyMs`
2. `sequential.FirstFastStartMs > parallel.FirstFastStartMs`

**結論（計測）:** 現行逐次は cross-tenant HOL を数値として再現できる。比較用並列は HOL を緩和するが、製品採用の根拠にはならない（次節）。

### SLO 仮定

公開リポジトリ上、Webhook enqueue→delivery 完了の正式 SLO は未定義。本判断では次を仮置きする（製品 SLA ではない）:

| 仮定 | 内容 |
|------|------|
| S1 | 小〜中規模（pending が数十〜数百、endpoint p95 が timeout より十分小さい）では逐次で運用可能 |
| S2 | 単一遅い tenant が timeout 近傍で連続すると、他 tenant の webhook 遅延は FIFO 長に比例して悪化しうる |
| S3 | ACS メール送信経路は既に制限付き並列。Webhook は別契約の通知経路であり、メール到達と同一並列度を要しない |

## Options compared

### A. 逐次維持（現行）

| 観点 | 評価 |
|------|------|
| 利点 | 単純。SQLite claim/finalize の同時 write が少ない。現行 lease / shutdown drain が単一 in-flight で正しい。テスト・運用説明が短い |
| 欠点 | Cross-tenant HOL。遅い endpoint 1 本が全体を直列化する |
| 互換性 | 完全維持 |

### B. グローバル concurrency 上限のみ

| 観点 | 評価 |
|------|------|
| 概要 | `MaxDeliveryConcurrency` + claim wave / `Task.WhenAll`（メール worker に近い） |
| 利点 | HOL を緩和。実装パターンを流用しやすい |
| 欠点 | Lease を全 wave の timeout x ceil(batch/C)+finalize へ再定義必須。shutdown drain 延長。tenant fairness 無しだと遅い tenant が slot を占有。SQLite write 競合増加 |
| 互換性 | 設定追加。既定 C=1 なら挙動互換可能 |

### C. グローバル上限 + tenant fairness（in-flight 上限 or round-robin）

| 観点 | 評価 |
|------|------|
| 利点 | Issue が求める fairness に近い。遅い tenant の継続阻害を抑えやすい |
| 欠点 | claim 順序・スケジューラ・観測が複雑。lease / shutdown / テスト面が大きい。需要確定前は過剰 |
| 互換性 | 設定・挙動の説明コストが高い |

## Decision

### D-01. Webhook 配送は逐次（案 A）を維持する

**判断:** 制限付き並列化（案 B / C）は現時点では採用しない。製品コードの配送ループは変更しない。

**理由（Issue 採用基準に対応）:**

1. **SLO 未達が確認できない** — Webhook 配送の正式 SLO と想定最大 backlog の運用実測が無い（S1–S3 は仮定）。synthetic HOL は「遅延しうる」ことを示すが、「満たせない」証拠ではない。
2. **継続的阻害の運用証拠が無い** — harness は意図的に slow-then-fast を並べた最悪系。実 deployment の tenant 別 backlog / timeout 率 / retry 率は未計測。
3. **余裕の確認が無い** — SQLite write 競合・shutdown drain・lease 再設計のコストに見合う headroom を current deployment で示していない。
4. **複雑性** — 並列化は lease・shutdown・fencing・strict config validation・fairness を同時に設計する必要があり（Issue「並列化する場合の必須方針」）、計測根拠なしに入れると回帰面だけが増える。
5. **契約非変更** — 本判断は HTTP / Consumer 契約に触れない（ADR 0017 と独立）。

### D-02. 採用する場合に必要な設計（将来・未実装）

再評価で案 B または C を採る場合の必須方針（Issue 原文を ADR に固定）:

1. Global concurrency 上限（strict integer validation、default は現行互換の 1 を推奨）
2. Tenant fairness（tenant 単位 in-flight 上限または round-robin claim）を可能なら持つ
3. Lease が全 wave の `DeliveryTimeout + FinalizeTimeout` を安全に覆う
4. Shutdown 開始後に新規 delivery を開始しない（メール worker #271 と同型）
5. Lock token fencing と at-least-once を維持
6. 無制限並列・fire-and-forget・外部 queue 導入は非対象のまま

### D-03. 再評価 trigger（不採用時）

次のいずれかが文書化または計測されたら本 ADR を再評価し、後継 ADR / 実装 Issue で案 B または C を採否する。

| # | Trigger |
|---|---------|
| T1 | Webhook enqueue→delivery の p95/p99 または backlog 年齢が、文書化した SLO / 運用目標を継続的に超過 |
| T2 | 単一 tenant（または単一 endpoint）の遅延・timeout が、他 tenant の webhook 完了を継続的に阻害していることが metrics / 運用記録で示される |
| T3 | 想定最大 pending 件数と endpoint latency 分布の実測が、逐次モデルで SLO 不可能と示される |
| T4 | Maintainer が並列化の実装 Issue を明示起票し、lease / fairness / shutdown の設計レビューを通す |

再評価時も根拠なしの `Task.WhenAll` 追加は禁止。計測条件・結果・採否を後継 ADR に残す。

## 採用時の子 Issue 分割（参考・今回は作成しない）

**AC #6 の解釈（#361）:** Issue 本文の受入条件「実装は別Issueへ分割される」は、想定工数の「採用時の実装…を別Issue化する」および受入条件 #4（採用時）と対になる。**並列化を採用しない本判断では子 Issue を起こさない**ことが意図どおりの充足であり、無条件の子 Issue 作成要求ではない。これは [ADR 0017](0017-webhook-first-wins-delivery-cycle.md)（不採用時は子 Issue なし・分割案のみ）と同型。再評価 trigger（D-03 / T4）成立後に Maintainer が実装 Issue を起票する。

本セッションでは Issue 作成禁止のため提案文のみ。現判断は不採用のため子 Issue は起こさない。

### 参考: 案 B 採択時

- Config: `MaxDeliveryConcurrency`（または同等）+ strict validation + lease 不等式更新
- Runtime: claim wave / semaphore / InflightTracker / shutdown drain
- Tests: concurrency・lease・shutdown・fencing 回帰
- Docs: service-spec / runbook / env 例

### 参考: 案 C 採択時

- 上記に加え tenant fairness（per-tenant in-flight または claim スケジューラ）
- 観測: tenant 別 backlog / in-flight（PII なし）
- HOL 再計測 harness の更新

## Rejected Alternatives（本判断時点）

| 案 | 却下理由 |
|----|----------|
| B. グローバル concurrency のみ | 採用基準未達。lease/shutdown 再設計コスト。fairness 不足 |
| C. fairness 付き並列 | 同上。需要・実測前は過剰 |
| 無制限並列 / fire-and-forget | Issue 非対象。到達保証と SQLite 安全性を損なう |
| 外部 queue / Redis | Issue 非対象。運用面が別プロダクト化 |
| retry backoff 変更で HOL を擬似緩和 | 配送並列度の問題を隠すだけで、FIFO claim の根本は残る |

## Consequences

- _positive:_ 現行の単純な配送モデル・lease・shutdown が維持される。
- _positive:_ HOL の存在と再評価条件が文書化され、将来の根拠付き実装が可能。
- _positive:_ CI で逐次 HOL 不等式を回帰できる harness が残る。
- _negative:_ 遅い endpoint 混在時の cross-tenant 遅延は、再評価まで受容する。
- _operational:_ T1–T4 を満たすまで並列化実装 Issue を起こさない。

## References

- [#361 design/performance: Webhook 配送の並列化要否と fairness](https://github.com/kooiei-in4a/amane-mailer/issues/361)
- `src/Amane.Mailer/Webhooks/WebhookDeliveryWorker.cs`
- `src/Amane.Mailer/Webhooks/DeliveryEventRepository.cs`（`TryClaimOneAsync`）
- `src/Amane.Mailer/Configuration/MailerWebhookOptions.cs`（単一 in-flight lease）
- `tests/Amane.Mailer.Tests/Webhooks/WebhookDeliverySequentialBenchmarkTests.cs`
- [ADR 0017: first-wins 維持](0017-webhook-first-wins-delivery-cycle.md)