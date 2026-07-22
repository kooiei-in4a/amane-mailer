# ADR 0015: `mail_requests` の手動再送・手動キャンセル状態遷移

- **Status:** Accepted
- **Date:** 2026-07-03
- **Tracks:** [#100](https://github.com/kooiei-in4a/amane-mailer/issues/100)
- **Implementation follow-up:** [#101](https://github.com/kooiei-in4a/amane-mailer/issues/101)
- **Supersedes ambiguity in:** [service-spec §3.4](../service-spec.md#34-状態遷移mail_requestsstatus)（`Failed` の意味、手動操作の未定義）
- **Aligns with:** [ADR 0013 D-08 配送操作監査](0013-admin-threat-model-and-pii-policy.md#d-08-管理操作監査ログは必須)、[ADR 0014 D-02 tenant scope 認可](0014-admin-session-tenant-throttle-audit-design.md#d-02-per-admin-tenant-scope-の要否と導入条件)

## Context

運用者は Admin UI から dead-letter や terminal failure を **手動再送**または**手動キャンセル**して復旧したい。[ADR 0013](0013-admin-threat-model-and-pii-policy.md) は配送操作の監査と tenant scope を要求しているが、状態遷移そのものは未定義だった。

採択時点（2026-07-03 / [#101](https://github.com/kooiei-in4a/amane-mailer/issues/101) 実装前）の runtime 事実:

| 項目 | 当時の現状 |
|------|------|
| `MailRequestState` | `Queued`, `Processing`, `Delivered`, `Failed`, `DeadLettered` の 5 値（`Cancelled` なし） |
| Worker claim | `Queued`（`next_attempt_at` 到来）または **期限切れ** `Processing` のみ |
| Worker finalize | `RetryScheduled` は **`Queued` に戻す**（`Failed` 状態には戻さない） |
| `Failed` | **終端**の非 retryable provider 失敗（`completed_at` / `failed_at` 設定） |
| `DeadLettered` | **終端**（最大試行超過または stale `Processing` の reaper） |
| Admin UI | Dead Letter 画面の「再送する」は disabled |
| DB CHECK | `mail_requests.status IN (0,1,2,3,4)`（[001_initial.sql](../../src/Amane.Mailer/Data/Migrations/001_initial.sql)） |
| HTTP 公開 API | 手動再送・キャンセルは **Admin 専用**。`POST /internal/mail-requests` 契約の対象外 |

**実装ステータス（2026-07-22）:** [#101](https://github.com/kooiei-in4a/amane-mailer/issues/101) により手動再送・手動キャンセルは実装済み。Admin UI の Dead Letter 一覧・詳細の「再送する」、詳細の「キャンセルする」（有効 lock 中の `Processing` は disabled）、`MailRequestState.Cancelled`、DB CHECK `0..5`（[007_mail_request_cancelled_status.sql](../../src/Amane.Mailer/Data/Migrations/007_mail_request_cancelled_status.sql)）を含む。本 ADR の Decision 節は設計判断の正本であり、上表は採択前ギャップの記録である。

手動操作は Worker の `TryClaimOneAsync` / `FinalizeAsync` / `DeadLetterExpiredProcessingAtMaxAttemptsAsync` と **同一 SQLite 行を競合更新**する。原子的 conditional `UPDATE` と tenant scope 認可が必須である。

前提:

- 単一 Mailer プロセス + SQLite（ADR 0013 D-11）。
- Admin は experimental・デフォルト無効・内部ネットワーク向け（ADR 0013 D-01）。
- Native AOT / full trimming 互換を維持する（実装は #101）。

## Decision

### D-01. `Cancelled` 状態を追加し終端とする

**判断:** `MailRequestState.Cancelled = 5` を追加する。手動キャンセル専用の **終端**状態とし、`Delivered` / `Failed` / `DeadLettered` と同様に Worker が再 claim しない。

| 項目 | 内容 |
|------|------|
| 意味 | 運用者が意図的に配送を打ち切った（再送しない） |
| 終端性 | **終端**。`attempt_count` は変更しない（履歴保持） |
| `completed_at` / `failed_at` | キャンセル成功時に **現在時刻**を設定する |
| `last_error_message` | 固定の理由コード `operator_cancelled` を設定する（PII なし）。以前の provider エラーは `mail_attempts` に残る |
| DB migration | #101 で `mail_requests.status` の CHECK を `0..5` に拡張する。`mail_attempts` の attempt status CHECK は変更しない（キャンセルは attempt 行を追加しない） |

**却下した代替案:** `Failed` を流用する — provider 終端失敗と運用者キャンセルが区別できず、監査・運用判断が曖昧になるため採用しない。

### D-02. 手動再送（manual retry）の許可状態と遷移先

**判断:** 手動再送は **終端状態からのみ** `Queued` へ戻す。`Processing`（有効 lock 保持中）と `Delivered` / `Cancelled` からは拒否する。

| 遷移元 | 許可 | 遷移先 | 備考 |
|--------|------|--------|------|
| `DeadLettered` | **Yes** | `Queued` | Dead Letter 画面の主用途 |
| `Failed` | **Yes** | `Queued` | 非 retryable provider 失敗の運用復旧 |
| `Queued` | **No** | — | 既に配送待ち。将来の `next_attempt_at` 繰り上げは本 ADR の対象外（#101 でも実装しない） |
| `Processing` | **No** | — | Worker が送信中。lock 失効後に `DeadLettered` / `Failed` / `Delivered` へ収束するのを待つ |
| `Delivered` | **No** | — | 配送済み |
| `Cancelled` | **No** | — | 運用者が打ち切り済み |

**遷移時に設定する列（`DeadLettered` / `Failed` → `Queued`）:**

| 列 | 値 |
|----|-----|
| `status` | `Queued` (0) |
| `attempt_count` | **0 にリセット**（新しい自動試行バジェット。`mail_attempts` に過去試行は残る） |
| `max_attempts` | **変更しない** |
| `next_attempt_at` | `NULL`（即時 dispatch 対象） |
| `lock_token` / `lock_expires_at` | `NULL` |
| `completed_at` / `delivered_at` / `failed_at` | `NULL` |
| `last_error_message` | **保持**（直前エラー文脈を Admin 詳細に残す。次回配送成功で Worker が上書き） |

成功後は既存の `IMailRequestQueue.TrySignalWorkAvailable()` で Worker / Sweep に再キュー信号を送る（#101）。

**`mail_attempts.attempt_number` と手動再送（D-05 参照）:** 手動再送で `attempt_count` を 0 に戻すと、次回 claim 以降の新規 `mail_attempts` 行は再び `attempt_number = 1` から始まりうる。過去サイクルの行と **数値が重複する**ため、`attempt_number` 単独では全履歴の順序を表せない。

### D-03. 手動キャンセル（manual cancel）の許可状態と遷移先

**判断:** 手動キャンセルは **まだ配送成功していない** request を `Cancelled` へ遷移させる。

| 遷移元 | 許可 | 条件 | 遷移先 |
|--------|------|------|--------|
| `Queued` | **Yes** | 常時 | `Cancelled` |
| `Processing` | **条件付き** | `lock_expires_at IS NOT NULL AND lock_expires_at <= @Now`（**stale processing のみ**） | `Cancelled` |
| `Processing` | **No** | 有効 lock（`lock_expires_at > @Now`） | — |
| `Failed` | **Yes** | 常時 | `Cancelled` |
| `DeadLettered` | **Yes** | 常時 | `Cancelled` |
| `Delivered` | **No** | — | — |
| `Cancelled` | **No** | 冪等拒否 | — |

**遷移時に設定する列:**

| 列 | 値 |
|----|-----|
| `status` | `Cancelled` (5) |
| `next_attempt_at` | `NULL` |
| `lock_token` / `lock_expires_at` | `NULL` |
| `completed_at` / `failed_at` | 現在時刻 |
| `last_error_message` | `operator_cancelled` |
| `attempt_count` | **変更しない** |

**却下した代替案:** 有効 lock 中の `Processing` を強制キャンセル — Worker の `FinalizeAsync`（`lock_expires_at > @Now` 条件）と競合し、二重配送リスクが残るため採用しない。運用者は lease 失効後にキャンセルまたは再送を行う。

### D-04. Worker claim / finalize / stale sweep との競合ルール

手動操作は **単一 SQLite transaction 内の conditional `UPDATE`** で行い、Worker と同じ行に対して **先に成功した更新が勝つ**（lost update は拒否、副作用なし）。

| 競合 | ルール |
|------|--------|
| Manual retry vs `TryClaimOneAsync` | 手動再送は `DeadLettered` / `Failed` のみ対象のため claim と直接競合しない |
| Manual cancel vs active `Processing` | 有効 lock がある間は **拒否**（D-03） |
| Manual cancel vs stale `Processing` claim (`TryClaimOneAsync`) | 双方とも `status = Processing AND lock_expires_at <= @Now` を対象とする（claim は追加で `attempt_count < max_attempts`）。**同一 transaction 内の conditional `UPDATE` で先勝ち**。cancel が負けた場合（claim が先に lock を更新）→ **409**、`error_code=lock_held`。claim が負けた場合（cancel が先に `Cancelled` へ）→ claim は 0 行。reaper（`attempt_count >= max_attempts`）とも競合しうる（D-03 既存行） |
| Manual cancel vs stale `Processing` (reaper) | reaper と競合しうる。`UPDATE ... WHERE status=Processing AND lock_expires_at <= @Now` で **先勝ち**。負け側は 0 行更新 → 呼び出し元は `invalid_state` または `lock_held` で失敗応答 |
| Manual cancel vs `FinalizeAsync` | finalize は `lock_expires_at > @Now` を要求。stale 行は finalize も cancel も失敗しうる — reaper が `DeadLettered` にした後は手動キャンセル可能 |
| Manual retry vs retention | retention は terminal 状態を対象とする場合 `Cancelled` を `Delivered` / `DeadLettered` と同様に terminal 扱い（#101 で retention クエリを更新） |

**実装要件（#101）:** repository メソッドは `bool` または `ManualMailRequestMutationResult` を返し、0 行更新時は HTTP **409 Conflict** と監査 `failure` を記録する。

**stale `Processing` キャンセルの限界:** lease 失効後のキャンセルは **DB 上の将来 dispatch を止める**だけである。Provider への送信が既に in-flight の場合、取り消しはできない（ADR 0012 at-least-once）。finalize が有効 lock で成功すれば `Delivered` / `Failed` / `DeadLettered` へ収束する。

### D-05. `attempt_count` / `max_attempts` / `next_attempt_at` / `mail_attempts` の扱い

| 操作 | `attempt_count` | `max_attempts` | `next_attempt_at` |
|------|-----------------|----------------|-------------------|
| 手動再送 | **0 にリセット** | 不変 | `NULL`（即時） |
| 手動キャンセル | 不変 | 不変 | `NULL` |
| Worker claim | +1 | 不変 | 不変（claim 時は触らない） |
| Worker retry schedule | 不変 | 不変 | backoff 時刻を設定し `status=Queued` |

**2 層の意味（手動再送後の履歴）:**

| フィールド | 意味 |
|------------|------|
| `mail_requests.attempt_count` | **現 dispatch サイクル**の試行予算カウンタ。手動再送で 0 に戻し、次回 claim から再カウントする |
| `mail_attempts.attempt_number` | その attempt 行が記録された時点の `mail_requests.attempt_count` のスナップショット（[MailRequestWorker](../../src/Amane.Mailer/Worker/MailRequestWorker.cs) が claim 後の値を書き込む） |

手動再送後は **過去の `mail_attempts` 行を削除しない**ため、異なるサイクルで同じ `attempt_number`（例: `1`）が複数行存在しうる。`attempt_number` は **グローバル連番ではない**。

**#101 表示・テスト要件:**

| 項目 | 内容 |
|------|------|
| 並び順 | Admin 試行履歴は **`started_at ASC, id ASC`** で表示する（現行 `ORDER BY attempt_number ASC` は手動再送後に誤順となりうるため **置き換える**） |
| UI | 時系列順を正とする。手動再送境界の明示ラベルは任意（監査イベント `mail_request.manual_retry_requested` の時刻で足りる） |
| 却下 | `retry_generation` 列の追加 — マイグレーションコストに対し、`started_at` ソートで十分なため本 ADR では採用しない |

### D-06. tenant scope 認可

[ADR 0014 D-02](0014-admin-session-tenant-throttle-audit-design.md#d-02-per-admin-tenant-scope-の要否と導入条件) に従う。

| 項目 | 内容 |
|------|------|
| 対象 | `mail_requests.tenant_id` が管理者の allowed tenant set に含まれること |
| scoped 管理者 | 自分の scope 内のみ再送・キャンセル可 |
| break-glass 管理者 | 全 tenant 可（追加の break-glass 専用 audit event は不要。`actor` と既存 break-glass ログイン監査で足りる） |
| 存在しない ID / scope 外 | **404 Not Found**、`error_code=not_found`、監査 `failure` — [既存 Admin 詳細](../../src/Amane.Mailer/Admin/AdminMailRequestDetailPage.cs) と同じ（`GetDetailForAdminAsync` が tenant フィルタ込みで `null` を返す） |
| 未認証・session 無効 | **403 Forbidden**（既存 Admin と同じ。mutation 対象の存在確認前） |

**方針:** tenant scope 外を **403 で区別しない**。unscoped existence lookup は行わず、常に allowed tenant 集合で query する（tenant 列挙の情報漏洩を避ける）。

### D-07. 監査イベント名と PII を含めない記録方針

[ADR 0013 D-08](0013-admin-threat-model-and-pii-policy.md#d-08-管理操作監査ログは必須) の「配送操作」を、既存 `auth.*` / `mail_request.body_viewed` と同じ **dotted snake_case** で凍結する。

| `event_type` | トリガー | `target_type` | `target_id` | 記録してよい追加情報 |
|--------------|----------|---------------|-------------|----------------------|
| `mail_request.manual_retry_requested` | 手動再送 API 処理時（成功・失敗とも） | `mail_request` | 内部 `mail_requests.id` (UUID) | `result`, `error_code`（例: `invalid_state`, `lock_held`, `not_found`） |
| `mail_request.manual_cancel_requested` | 手動キャンセル API 処理時（成功・失敗とも） | `mail_request` | 同上 | 同上 |

**記録しない:** 宛先、件名、本文、metadata 値、provider raw error、`mail_request_id`（Consumer 冪等キー）は `target_id` に使わない（内部 id を正とする。必要なら将来 `target_id` とは別列を追加するが #101 では不要）。

**記録する:** `actor`, `occurred_at`, `source_ip`（または hash 化）, `user_agent_summary`, `result`, `error_code`。

**Fail-closed との関係（ADR 0013 D-08）:** 本文閲覧（`mail_request.body_viewed`）は PII 露出のため **監査失敗時に操作を拒否**する（fail-closed）。手動再送・キャンセルは **状態変更のみ**で PII を新たに露出しない。auth 系（`auth.logout` 等）と同様、監査は **best-effort**（[AdminAuditLog.WriteBestEffortAsync](../../src/Amane.Mailer/Admin/AdminAuditLog.cs)）とする。

| 項目 | 内容 |
|------|------|
| 操作成否 | **DB 更新の成否**で決まる。監査失敗だけで再送・キャンセルをロールバックしない |
| #101 推奨 | 可能なら state `UPDATE` と audit `INSERT` を **同一 SQLite transaction** に含め、通常は両方成功させる |
| 監査のみ失敗 | 操作は成功、stdout に warning。運用者は `admin_audit_events` の欠落を retention / 外部ログで検知する（本文閲覧ほどの即時 deny は不要と判断） |

### D-08. Admin API / UI の最小契約

**HTTP 公開 Contracts / OpenAPI は変更しない。** 手動再送・キャンセルは Admin HTML / 内部 POST のみ（ADR 0013 D-01）。Consumer 向け `POST /internal/mail-requests` や `src/Amane.Mailer.Contracts` には手を入れない。

| 項目 | 契約 |
|------|------|
| 認証 | 既存 Cookie session + `RequireAuthorization()` |
| エンドポイント | `POST /admin/mail-requests/{id}/retry`、`POST /admin/mail-requests/{id}/cancel` |
| `{id}` | 内部 `mail_requests.id`（既存詳細 URL と同じ） |
| 応答 | 成功: **303 See Other** で詳細または一覧へリダイレクト（既存 Admin パターン）。失敗: **404**（存在しない / scope 外）、**409**（invalid state / lock held）、**403**（未認証・session 無効のみ） |
| UI | Dead Letter 一覧・詳細に操作ボタンを有効化。`Processing` かつ有効 lock 時はキャンセル disabled + tooltip |
| XSS | 既存 `HtmlEncoder` / inline エスケープパターンに従う（#101 テストで回帰） |
| CSRF | #101 で既存 Admin POST と同レベルの保護を適用（現行 login POST に倣い、同一サイト Cookie 前提の最小対策を文書化） |

**#101 実装外（follow-up 候補）:**

- 監査ログ UI（#103）での `mail_request.manual_*` イベント表示
- `db stats` / `db request-state` CLI への `Cancelled` 件数追加
- Queued の `next_attempt_at` 繰り上げ（expedite）
- `retry_generation` 列（D-05 で却下。`started_at` ソートで代替）

### D-09. 状態遷移図（手動操作込み）

```
                         ┌──manual retry──┐
                         │                │
0 Queued ──claim──▶ 1 Processing ──success──▶ 2 Delivered (終端)
   │                      │
   │ manual cancel        ├──retryable fail──▶ 0 Queued (next_attempt_at)
   │                      ├──terminal fail───▶ 3 Failed (終端)──manual retry──┐
   ▼                      └──max attempts────▶ 4 DeadLettered (終端)──manual retry──┤
5 Cancelled (終端)          stale + max attempts ──reaper──▶ 4 DeadLettered          │
   ▲                      stale + cancel (lock expired) ────────┘                    │
   │ manual cancel (Failed / DeadLettered / Queued / stale Processing)              │
   └──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    └──▶ 0 Queued (manual retry 成功時)
```

## Rejected Alternatives

| 案 | 却下理由 |
|----|----------|
| `Cancelled` を追加せず `Failed` で代用 | 運用者キャンセルと provider 終端失敗の区別がつかない |
| 手動再送で `attempt_count` を維持 | 直後に `DeadLettered` へ再落ちし、運用復旧にならない |
| `retry_generation` 列で attempt 履歴を区別 | `started_at` ソートで足りる。マイグレーションコストを避ける |
| 有効 lock 中の `Processing` を強制キャンセル | Worker finalize との二重配送・lost update リスク |
| 手動再送を `Queued` からも許可 | 既にパイプライン内。expedite は別機能として scope 外 |
| 手動操作を公開 REST / OpenAPI に追加 | Admin experimental の範囲外。HTTP 契約変更コストが不要 |
| 手動キャンセル時に `mail_attempts` 行を追加 | 配送試行ではなく運用操作。監査は `admin_audit_events` で足りる |

## Consequences

- _positive:_ #101 が参照できる明確な遷移表と競合ルールができる。
- _positive:_ `Failed` の意味が service-spec と runtime で一致する。
- _positive:_ 公開 HTTP 契約に触れず Admin のみで完結する。
- _negative:_ DB migration と enum 拡張が必要（CHECK 制約、Admin 表示、CLI stats）。
- _negative:_ stale `Processing` のキャンセルは reaper と競合し、409 になりうる。
- _operational:_ 手動再送は at-least-once セマンティクス（ADR 0012）を維持する。Provider 側の重複は運用許容。

## References

- [#100 ADR 手動再送・手動キャンセル状態遷移凍結](https://github.com/kooiei-in4a/amane-mailer/issues/100)
- [#101 Admin 手動再送・手動キャンセル実装](https://github.com/kooiei-in4a/amane-mailer/issues/101)
- [ADR 0013: 管理画面の脅威モデル・PII 取り扱い](0013-admin-threat-model-and-pii-policy.md)
- [ADR 0014: Admin session / tenant scope / audit](0014-admin-session-tenant-throttle-audit-design.md)
- [ADR 0012 D-05a at-least-once 配送](0012-mail-via-mailer-microservice.md)
- [service-spec §3.4](../service-spec.md#34-状態遷移mail_requestsstatus)
