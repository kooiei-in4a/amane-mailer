# ADR 0017: 配送結果 Webhook の first-wins 維持（delivery cycle 拡張の見送り）

- **Status:** Accepted
- **Date:** 2026-07-25
- **Tracks:** [#362](https://github.com/kooiei-in4a/amane-mailer/issues/362)
- **Implementation follow-up:** なし（契約変更を採用しない。再評価 trigger 成立時に新 Issue / ADR を起こす）
- **Preserves:** [#273](https://github.com/kooiei-in4a/amane-mailer/issues/273) で明文化した first-wins Consumer 契約
- **Aligns with:** [ADR 0015](0015-manual-retry-cancel-state-transitions.md)（手動再送は dispatch サイクルを戻すが `retry_generation` 列は却下）、[service-spec §3.5](../service-spec.md#35-配送結果-webhookoutbound)、[webhook-verification.md](../consumer/webhook-verification.md)
- **Related future work:** [#307](https://github.com/kooiei-in4a/amane-mailer/issues/307)（`bounced` 通知は本 ADR の first-wins とは別事実クラスとして identity を設計する）

## Context

調査基準の意図は `develop@da748f598f4ab0f19cd479d1210e887967a78263`。本 ADR の事実は 2026-07-25 時点の `develop` 系実装に基づく。

[#273](https://github.com/kooiei-in4a/amane-mailer/issues/273) で、同一 `(tenant_id, source_service, mail_request_id)` について **最初に enqueue された終端状態だけ**を通知する first-wins 契約が Consumer 向け文書・OpenAPI・Contracts・テストで明文化された。実装（`delivery_events` の `UNIQUE (tenant_id, source_service, mail_request_id)` + `ON CONFLICT DO NOTHING`）と文書は一致しており、バグではない。

典型ギャップ:

1. 配送が `failed` になり Webhook を通知
2. Admin が manual retry（[ADR 0015](0015-manual-retry-cancel-state-transitions.md)）
3. 配送が `delivered` になる
4. 2 つ目の Webhook event は作成されない

最新状態は `GET /internal/mail-requests/{mail_request_id}` を正とする、と既に文書化されている。一方、Consumer が「常に最新終端を push で受けたい」場合は first-wins では不足する。#362 は **契約を暗黙に変更せず**、拡張要否を判断する設計 Issue である。

### 現行契約の要約

| 項目 | 現行 |
|------|------|
| Outbox unique | `(tenant_id, source_service, mail_request_id)` 高々 1 行 |
| Enqueue | terminal 到達時。衝突時は `DO NOTHING`（上書きなし） |
| Reconcile | outbox 欠落の terminal request のみ補完。既存 event は触らない |
| HTTP 再 POST | 同一 `event_id` + 同一 body（at-least-once） |
| Consumer 冪等 | `event_id` |
| Retention | terminal `mail_requests` と matching `delivery_events` を同一 transaction で purge。purge 後の同一 `mail_request_id` 再利用は新しい `event_id` |
| Manual retry | outbox 行を削除・更新しない。後続終端は通知しない |
| 最新状態の正本 | status GET |

### 検討ユースケースと現状の扱い

| ユースケース | first-wins 下の結果 | 代替 |
|--------------|---------------------|------|
| `failed` → manual retry → `delivered` | 最初の `failed` のみ通知 | status GET |
| `dead_lettered` → manual retry → `delivered` / `failed` | 最初の `dead_lettered` のみ | status GET |
| `failed` / `dead_lettered` → manual cancel → `cancelled`（2つ目の terminal） | 最初の終端のみ通知。`cancelled` は enqueue されない（現行 ADR 0015 で発生しうる） | status GET |
| `cancelled` 後の再開（将来） | 現状再開不可（ADR 0015）。再開を許可するなら identity 再設計が必要 | — |
| delivery 後の bounce 等の別事実（#307） | 現行 UNIQUE では `delivered` 後に `bounced` を追加できない | #307 で identity を別途設計 |
| retention 後の同一 `mail_request_id` 再利用 | 新世代として新しい `event_id` を発行（現行どおり） | Consumer は `event_id` で世代を区別 |

### Consumer 要求の整理（#362 判断時点）

| 観点 | 観測 |
|------|------|
| 公開リポジトリ上の必須要求 | 「最新終端を必ず webhook で再通知せよ」という具体 Consumer / 製品要件は未確認 |
| 既に提供されている代替 | status GET（#216）が現行 mail-request 状態の正本。service-spec / OpenAPI / webhook-verification が明示 |
| Webhook の位置づけ | 実メール送信とは別契約の **one-shot first-terminal 通知**（#273） |
| 運用上のギャップ | Admin 手動再送後に status と webhook 終端が食い違うことは **既知・文書化済み**。バグ扱いにしない |

結論: **現時点で契約拡張を正当化する実利用要求は不足**。polling / status GET で代替可能な設計を維持する。

## Options compared

### A. first-wins 維持（現行）

| 観点 | 評価 |
|------|------|
| 利点 | Consumer・DB・Contracts・OpenAPI・テストを変更しない。#273 文書と一致。冪等が単純（`event_id` のみ）。reconcile / retention が単純 |
| 欠点 | 手動再送後の最新終端は push されない。webhook だけを見る Consumer は古い終端のまま |
| Migration | なし |
| 互換性 | 完全後方互換 |

### B. delivery cycle ID を導入

| 観点 | 評価 |
|------|------|
| 概要 | manual retry 等で cycle を増やし、`(tenant, source, request, cycle[, event_type])` 等で event を識別。payload に cycle を載せる |
| 利点 | 手動再送後の別終端を通知できる。dispatch サイクルと webhook を揃えやすい |
| 欠点 | DB unique / payload / Consumer 重複排除・順序の再教育が必要。ADR 0015 で却下した `retry_generation` 系の永続化コストが webhook 経路に再登場する。at-least-once 下の順序逆転対処が複雑化 |
| Migration | `delivery_events` 制約変更 + 既存行の cycle=0（または 1）埋め。retention / reconcile 条件の更新 |
| 互換性 | **破壊的**（同一 `mail_request_id` に複数 `event_id` が来る前提変更） |

### C. `event_type` まで unique を拡張

| 観点 | 評価 |
|------|------|
| 概要 | unique を `(tenant, source, request, event_type)` に広げ、異なる type の追加発行を許可。同一 type の再発行規則は別定義 |
| 利点 | `failed` → `delivered` や将来の `bounced`（#307）を、cycle 列なしで部分的に解ける |
| 欠点 | 1 request 複数 event は Consumer 破壊的。同一 type の再発行（例: `failed` → retry → `failed`）は未解決。順序・「どれが最新か」は Consumer 側負担。#307 の事実クラスと terminal delivery を同一軸に載せると意味が混ざる |
| Migration | unique 制約変更 + CHECK 拡張の可能性。reconcile を type 単位に再定義 |
| 互換性 | **破壊的**（複数 event / 複数 `event_id`） |

## Decision

### D-01. 配送結果 Webhook は first-wins（案 A）を維持する

**判断:** 現行の first-wins 契約を **採用し続ける**。delivery cycle 単位の再通知（案 B）および `event_type` 単位の複数 event 許可（案 C）は、現時点では **採用しない**。

**理由:**

1. 最新終端の push 必須という実 Consumer 要求が確認できない。
2. status GET が最新状態の正本として既に提供・文書化されている。
3. 案 B / C はいずれも公開 Webhook 契約の破壊的変更であり、migration・Consumer 対応コストが大きい。
4. ADR 0015 は attempt 履歴のために `retry_generation` を却下済み。webhook だけに cycle を足すとドメインモデルが分岐する。
5. #273 で凍結した one-shot 契約を、需要なしに再開放しない。

**#273 文書の扱い:** service-spec / OpenAPI / Contracts / webhook-verification / 回帰テストに書かれた first-wins は **引き続き正本**。本 ADR はそれを再確認し、拡張見送りを記録する。

### D-02. Event identity・順序・重複排除・retention・reconcile（維持時の規則）

本節は現行実装と #273 文書を ADR として凍結する。変更しない限りこれが正である。

| 規則 | 定義 |
|------|------|
| Event identity（outbox） | `(tenant_id, source_service, mail_request_id)` につき、対応する `mail_requests` 行が存在する間は高々 1 行 |
| Payload identity | `event_id`（UUID）。HTTP 再 POST でも不変 |
| 発行タイミング | mail request が terminal（`delivered` / `failed` / `dead_lettered` / `cancelled`）に達したとき enqueue を試みる |
| First-wins | 最初に成功した INSERT のみ残る。後続 terminal（手動再送後を含む）は INSERT せず、既存行を UPDATE しない |
| 順序 | 同一 request 世代では Webhook 終端は高々 1 つ。複数終端の順序問題は発生しない。at-least-once 再 POST の順序は `event_id` 冪等で吸収する |
| Consumer 重複排除 | `event_id` のみ。`mail_request_id` だけで「最新終端」とみなしてはならない |
| Retention | request retention は matching `delivery_events` を同一 transaction で削除する。purge 後の同一 idempotency key 再利用は **新世代**（新しい `event_id`） |
| Reconcile | terminal かつ outbox 欠落のみ INSERT。既存 event の上書き・差し替え・type 変更はしない |
| Manual retry / cancel | outbox を削除しない。cancel が **最初の** terminal ならその event が残る。retry 後の別 terminal は通知しない |
| 最新状態の正本 | 常に status GET。Webhook は live mirror ではない |

### D-03. #307（bounce 等）との関係

`bounced` のような **delivery 後に発生する別事実**は、本 ADR の first-wins（terminal delivery-result の one-shot）とは別クラスである。

| 方針 | 内容 |
|------|------|
| 本 ADR の範囲 | terminal delivery-result（現行 4 `event_type`）の first-wins を維持 |
| #307 | UNIQUE `(tenant, source, request)` のままでは `delivered` 後の追加 event は不可能。#307 は identity（例: event_type 拡張、別 outbox、または将来の cycle）を **その Issue / 後継 ADR で設計**する |
| 禁止 | #307 実装のために、本 ADR を経ずに first-wins を暗黙変更すること |
| 再評価 | #307 の設計が「同一 outbox・複数 terminal-like event」を要求する場合は、下表の再評価 trigger に該当しうる |

### D-04. 再評価 trigger（維持時）

次のいずれかが **文書化された要求または設計制約**として現れたら、本 ADR を再評価し、必要なら後継 ADR で案 B / C / 別案を採否する。

| # | Trigger |
|---|---------|
| T1 | 実 Consumer / 製品要件が「手動再送後の最新終端を webhook で必ず受けたい」と明示し、status GET / polling では SLA・運用上足りない |
| T2 | #307（または同類）が同一 `delivery_events` outbox で複数事実を載せる設計を採用し、現行 UNIQUE では成立しない |
| T3 | `cancelled` 後の再開など、同一 `mail_request_id` 世代で複数 terminal を正規のライフサイクルとする機能を採択する |
| T4 | Maintainer が破壊的 Webhook 契約変更（major）を明示スケジュールし、Consumer 移行計画を用意する |

再評価時も **runtime だけ変えて Consumer 文書を遅らせない**（#362 非対象の再掲）。Contracts / OpenAPI / DB / Consumer 検証文書を同一変更集合で扱う。

## 採用時の子 Issue 分割（参考・今回は作成しない）

案 B または C を将来採択する場合の分割例。**本セッションでは Issue 作成禁止のため提案文のみ。** 現判断は維持のため **子 Issue は起こさない**。

### 参考: 案 B 採択時

- Contracts / OpenAPI: payload に cycle（または同等）を追加し、multiple events を記述
- DB: unique / migration / retention / reconcile
- Runtime: manual retry で cycle 増加、enqueue 規則、テスト
- Consumer: webhook-verification・重複排除・順序ガイド

### 参考: 案 C 採択時

- Contracts / OpenAPI: 同一 request 複数 `event_type` の意味と同一 type 再発行規則
- DB: unique を `event_type` まで拡張、CHECK、reconcile
- Runtime / tests / Consumer 文書の三点（四点）同期
- #307 との identity 共有方針を明記

## Rejected Alternatives（本判断時点）

| 案 | 却下理由 |
|----|----------|
| B. delivery cycle ID | 実要求不足。破壊的。ADR 0015 で却下した generation 永続化コストの再導入 |
| C. event_type 単位複数 event | 部分的に有用だが Consumer 破壊的。同一 type 再発行と #307 事実クラスの混在が残る。需要確定後に再評価 |
| 既存 event 行の上書きで最新化 | #362 非対象。at-least-once 再 POST 中の Consumer が見る body が変わり、`event_id` 冪等と矛盾しうる |
| runtime のみ複数 event 化 | Consumer 非通知の契約変更。禁止 |
| webhook at-least-once の廃止 | #362 非対象。配送到達保証を弱める |

## Consequences

- _positive:_ #273 契約が維持され、Consumer・DB・テストに追加負荷がない。
- _positive:_ 手動再送後の「status と webhook の食い違い」が仕様として説明可能（バグではない）。
- _positive:_ #307 は first-wins を暗黙破壊せず、独自 identity 設計を強制される。
- _negative:_ push-only Consumer は手動再送後の最新終端を受け取れない（status GET が必要）。
- _operational:_ 再評価 trigger（D-04）を満たさない限り、cycle / multi-event 実装 Issue を起こさない。

## References

- [#362 design/webhook: first-wins を delivery cycle へ拡張する要否](https://github.com/kooiei-in4a/amane-mailer/issues/362)
- [#273 docs: Webhook first-wins 明文化](https://github.com/kooiei-in4a/amane-mailer/issues/273)
- [#307 webhook: bounced 配信イベント](https://github.com/kooiei-in4a/amane-mailer/issues/307)
- [ADR 0015: 手動再送・手動キャンセル状態遷移](0015-manual-retry-cancel-state-transitions.md)
- [docs/consumer/webhook-verification.md](../consumer/webhook-verification.md)
- [docs/service-spec.md §3.5](../service-spec.md#35-配送結果-webhookoutbound)
- `src/Amane.Mailer/Data/Migrations/008_delivery_events.sql`
- `src/Amane.Mailer/Webhooks/DeliveryEventRepository.cs`
- `tests/Amane.Mailer.Tests/Webhooks/WebhookDeliveryTests.cs`（manual retry 後の first-wins 回帰）
