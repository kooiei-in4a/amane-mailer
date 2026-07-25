# ADR 0016: provider／status／error code の内部強い型化の範囲

- **Status:** Accepted
- **Date:** 2026-07-25
- **Tracks:** [#360](https://github.com/kooiei-in4a/amane-mailer/issues/360)
- **Implementation follow-up:** 本 ADR の「提案子 Issue」節（maintainer が Issue 化する。本 PR では作成しない）
- **Aligns with:** [ADR 0012 D-01 Contracts 正本](0012-mail-via-mailer-microservice.md)、[ADR 0015 `MailRequestState`](0015-manual-retry-cancel-state-transitions.md)

## Context

調査基準の意図は `develop@da748f598f4ab0f19cd479d1210e887967a78263`。本 ADR の事実表は 2026-07-25 時点の `main` 系実装に基づく。

現状は constants、enum、DB integer、公開 JSON string が混在する。HTTP 契約は `scripts/check-contract-drift.mjs` でかなり保護されているが、新しい provider／status／error code 追加時は複数 file の変更が必要で、**compiler だけでは漏れを検出できない**箇所がある。

一方、公開 HTTP／DB 境界まで複雑な domain type へ置換すると、serialization、SQLite mapping、Native AOT / source-generated JSON のコストが過剰になる。

#360 は設計・spike であり、全 string 定数の一括置換を目的としない。本 ADR は採否と境界配置を固定し、実装は子 Issue に分割する。

### 調査対象ごとの現状

| 領域 | 現行表現 | 主な変更経路（新規値追加時） | 漏れ検出 |
|------|----------|------------------------------|----------|
| Mail provider (`mailpit` / `acs`) | runtime `string`（共有 constants なし） | tenant validate、`MailerOptions`、router switch、`tenants.schema.json`、platform sender allowlist、CLI | startup validate + router `_ => UnknownProvider`。**compiler 非検知** |
| `MailRequestState` ↔ `MailRequestStatus` | 内部 `enum : byte`（DB INTEGER）／公開 `const string` | enum + DB CHECK migration + Contracts + OpenAPI + `ToDeliveryStatus` + webhook event map + Admin/CLI display | HTTP は drift。mapper は `_ => throw`（実行時）。Admin/CLI は別 map で soft-fail しうる |
| `MailDeliveryErrorCodes` | Contracts `const string`（DB/HTTP TEXT） | Contracts + producer + tests。OpenAPI は closed enum ではない | 定数参照は typo 抑制。exception→code 漏れは classifier の fall-through。**drift 対象外** |
| `MailerErrorCodes` | Contracts `const string` | Contracts + OpenAPI Error.code + handlers + tests | **drift set-equality**（強い） |
| Readiness reason | runtime `const string` + `All[]` | evaluator + `All` + metrics gauges + tests | `All` 未登録は metrics 欠落。HTTP `/readyz` は reason を返さない |
| Webhook pipeline state | `DeliveryEventState` / `FinalizeOutcome` enum | enum + DB CHECK + repository | 既に強い。finalize map は throw |
| Webhook `event_type` | `MailDeliveryEventType` `const string` | Contracts + enqueue map + OpenAPI | OpenAPI との **drift 未接続** |
| Webhook transport error | ad-hoc string literals（`WEBHOOK_*`） | validator / client / worker | **constants なし**。typo はテスト依存 |

採否判断は #360 の 6 基準（変更箇所数、compiler/CI 検知、境界集約、可読性、AOT、維持コスト）で記録する。

## Decision

### D-01. 全体方針: runtime 内部を寄せ、HTTP／DB 境界は明示変換

**判断:**

1. runtime 内部だけを enum / readonly named values へ寄せる候補を限定する。
2. 公開 HTTP JSON と DB 永続表現は既存の string／integer のままとし、境界で明示変換する。
3. mapping は一か所に集約し、可能な範囲で exhaustive switch と focused test で守る。
4. 既に単純な string switch で安全な箇所（例: provider router の discard→`UNKNOWN_PROVIDER`）を無理に pattern 化しない。ただし **内部型化した後**は enum switch を正とする。

**却下した代替案:**

| 案 | 却下理由 |
|----|----------|
| 公開 HTTP DTO を CLR enum 化 | `JsonStringEnumConverter` / source-gen inventory / AOT コスト。Contracts の `const string` + drift が既に正本 |
| discriminated union ライブラリ導入 | #360 非対象。依存と AOT リスク |
| 全 primitive の value object 化 | 過剰抽象。変更コストに見合わない |
| generic result framework | 範囲外 |

### D-02. 採否表

| 領域 | 採否 | 理由（要約） | 変更箇所目安 | compiler/CI | 境界集約 | 可読性 | AOT | 維持コスト |
|------|------|--------------|--------------|-------------|----------|--------|-----|------------|
| Provider | **採用（内部 enum）** | call site が少ない一方、string switch は新 provider 追加漏れを compile できない | 8–15 | enum + 境界 Parse で向上 | config/CLI で Parse/Format | 向上 | 低（wire は string） | 低〜中 |
| `MailRequestState` / `MailRequestStatus` | **維持 + 集約** | 既に最良モデル。問題は Admin/CLI の重複 map | 集約のみ | 現状 + exhaustiveness test | `ToDeliveryStatus` を単一正本に | 維持 | なし | 低 |
| `MailDeliveryErrorCodes` | **維持（constants）** | 公開寄り安定 string。OpenAPI closed enum 化は契約硬化で別判断 | 3–8 | constants + 任意 catalog drift | persist/HTTP は string のまま | 十分 | なし | 低 |
| `MailerErrorCodes` | **維持（constants）** | drift が既に強い。HTTP enum 化は純コスト | 既存 | drift | Contracts 正本 | 十分 | なし | 低 |
| Readiness reason | **維持（constants + `All[]`）** | metrics 固定 cardinality 設計が既に正しい | 少 | `All` + tests | 現状 | 十分 | なし | 低 |
| Webhook pipeline enums | **維持** | 既に強い型 | — | 既存 | 既存 | 十分 | なし | 低 |
| `MailDeliveryEventType` | **維持 + OpenAPI drift 追加** | constants は妥当。drift 未接続がギャップ | 少（script） | CI 強化 | enqueue map 維持 | 維持 | なし | 低 |
| Webhook transport `WEBHOOK_*` | **採用（内部 constants class）** | 散在 literal。公開契約ではない | 3–5 file | constants 参照 | validator/client/worker | 向上 | なし | 低 |

不採用領域は **現行 constants + drift / `All[]` / 既存 enum を維持**する。理由は上表どおり、追加型の利益より境界・AOT・契約同期コストが大きい、または既に十分な保護があるため。

### D-03. Boundary conversion の配置

| 境界 | 変換 | 置き場所（案） |
|------|------|----------------|
| Tenant / platform JSON → runtime | `string` → `MailProvider` | `MailerTenant` / `MailerOptions` の validate 近傍に `Parse`。失敗は既存と同様 startup fail |
| Runtime → delivery router | `MailProvider` → provider 実装 | `MailDeliveryProviderRouter` の enum switch（discard は throw または既存 `UNKNOWN_PROVIDER` 方針を子 Issue で固定） |
| Runtime → HTTP status | `MailRequestState` → `MailRequestStatus.*` | **唯一** `MailRequestHttpErrorMapper.ToDeliveryStatus`。Admin / `db request-state` はこれを呼ぶ（Title Case UI は別ヘルパで表示整形のみ） |
| Runtime → webhook `event_type` | terminal `MailRequestState` → `MailDeliveryEventType.*` | **唯一** `DeliveryEventRepository.MapTerminalStatusToEventType` |
| Delivery / webhook error → DB/HTTP | 内部定数 → `string` | repository insert / response DTO 代入時のみ。公開名は変更しない |
| Readiness → metrics/logs | `MailerReadinessReasons.*` | 現状維持。`All[]` が catalog |

公開 HTTP の status / error code **名称変更はしない**（#360 非対象）。DB migration も行わない。

### D-04. Breaking-change / AOT 影響

| 項目 | 評価 |
|------|------|
| 公開 HTTP JSON | **破壊なし**（wire string 維持） |
| Tenant JSON `provider` | **破壊なし**（wire string 維持。内部だけ enum） |
| DB schema | **変更なし**（status INTEGER / error TEXT 維持） |
| Native AOT / trimming | 内部 enum + 明示 Parse/Format は source-gen JSON を増やさない限り **低リスク**。公開 DTO を CLR enum にしない |
| Contracts package | 公開 constants の削除・改名はしない。追加 constants（webhook transport）は runtime 内部に置き、Contracts に載せない（消費者向け安定契約ではないため） |

### D-05. 段階実装順

1. **Provider 内部 enum + 境界 Parse/Format**（router / validate / schema 同期）
2. **Status 表示 map 集約 + `MailRequestState` exhaustiveness test**
3. **Webhook transport error constants + `MailDeliveryEventType` OpenAPI drift**
4. （任意）`MailDeliveryErrorCodes` catalog を drift 対象に追加、または SDK の `MailerErrorCodes` 同期 — #360 本線より優先度低

各段階は独立 PR 可能な粒度とする。大規模一括置換は行わない。

## 提案子 Issue（maintainer 作成用）

> 本セッションでは Issue 作成が禁止のため、以下は ADR 上の提案文である。採択後に maintainer が GitHub Issue 化する。

### 子 Issue A — Provider 内部 enum

**Title 案:** `[P2] feat: mail provider を runtime 内部 enum 化し config 境界で変換する`

**受け入れ条件案:**

- [ ] `MailProvider`（仮）enum または同等の強い内部型を導入する
- [ ] tenant / `MAILER_PROVIDER` / platform sender 境界で `Parse`/`Format` する（wire string は `mailpit`/`acs` のまま）
- [ ] `MailDeliveryProviderRouter` は内部型で分岐する
- [ ] `tenants.schema.json` と startup validate の許容値を同期する
- [ ] focused tests + 既存 provider 検証を維持する
- [ ] Native AOT / source-gen JSON に新たな reflection 依存を入れない

### 子 Issue B — Status map 集約

**Title 案:** `[P2] refactor: MailRequestState→公開 status 変換を単一ヘルパに集約する`

**受け入れ条件案:**

- [ ] Admin / CLI の status 文字列が `ToDeliveryStatus`（または単一ヘルパ）経由になる
- [ ] `Enum.GetValues<MailRequestState>()` ベースの mapping exhaustiveness test を追加する
- [ ] 公開 `MailRequestStatus` 名・OpenAPI は変更しない
- [ ] DB migration なし

### 子 Issue C — Webhook transport constants + event type drift

**Title 案:** `[P2] chore: webhook transport error constants と MailDeliveryEventType drift を追加する`

**受け入れ条件案:**

- [ ] `WEBHOOK_*` literal を runtime 内部 constants class に集約する（公開 Contracts には載せない）
- [ ] `scripts/check-contract-drift.mjs` が `MailDeliveryEventType` と OpenAPI `event_type`/`status` を set-equality する
- [ ] 既存 webhook テストが通る
- [ ] 公開 webhook payload の値は変更しない

### 任意 follow-up D

- `MailDeliveryErrorCodes` の catalog drift（OpenAPI は closed enum にしない前提の description／例同期）
- TypeScript SDK の `MailerErrorCodes` サブセット同期

## Rejected Alternatives（領域横断）

| 案 | 却下理由 |
|----|----------|
| `MailerErrorCodes` / `MailRequestStatus` の CLR enum 化 | drift が既に強く、AOT/JSON コストが大きい |
| readiness の enum 化 | `All[]` + metrics 固定ラベルが既に目的適合。enum→label 変換が純増 |
| delivery error の OpenAPI closed enum 化 | 契約硬化。#360 の「名称変更なし」と別判断が必要 |
| provider router の現状 string switch を放置 | 新 provider 追加時の漏れが最も起きやすい領域のため、内部 enum を優先採用 |
| 本 Issue 内で production 大規模置換 | #360 は設計固定が完了条件。実装は子 Issue |

## Consequences

- _positive:_ 型導入する／しない境界が明確になり、過剰抽象を避けられる。
- _positive:_ 実装可能な子 Issue に分割できる（provider / status 集約 / webhook constants+drift）。
- _positive:_ 不採用領域でも現行 constants + drift / `All[]` を維持する理由が残る。
- _negative:_ `_ => throw` 付き switch は CS8509 を抑止するため、enum 追加だけでは build が落ちない。exhaustiveness test または discard 除去が子 Issue の必須補完になる。
- _operational:_ 公開契約・DB・AOT を壊さない段階実装が可能。

## References

- [#360 design: provider／status／error code の内部表現を強い型へ寄せる範囲](https://github.com/kooiei-in4a/amane-mailer/issues/360)
- [ADR 0012: mail via mailer microservice](0012-mail-via-mailer-microservice.md)
- [ADR 0015: 手動再送・手動キャンセル状態遷移](0015-manual-retry-cancel-state-transitions.md)
- `src/Amane.Mailer/Data/Sqlite/MailRequestState.cs`
- `src/Amane.Mailer.Contracts/MailRequests/MailRequestStatus.cs`
- `src/Amane.Mailer.Contracts/MailRequests/MailDeliveryErrorCodes.cs`
- `src/Amane.Mailer.Contracts/MailRequests/MailerErrorCodes.cs`
- `src/Amane.Mailer/Operations/MailerReadinessReasons.cs`
- `src/Amane.Mailer.Contracts/MailRequests/MailDeliveryEventType.cs`
- `scripts/check-contract-drift.mjs`
