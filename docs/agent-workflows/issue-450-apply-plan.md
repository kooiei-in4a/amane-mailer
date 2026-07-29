# Issue #450 Apply Plan Handoff (Rev. 3)

> **Status:** Plan-only handoff for continuation on another machine.
> **Not for merge as product code.** Implementation has not started.
> **Base develop HEAD at planning time:** `3b02cda42540b22a9a36817cae6e62f18fbc13a4`
> **Plan revision:** 3 (reflects all Blocker/Major/Minor findings from plan reviews through 2026-07-29)

This document is the approved implementation plan for [#450](https://github.com/kooiei-in4a/amane-mailer/issues/450).
When continuing, treat this file as the plan authority, then implement on a new branch
(`issue/450-bundle-apply-rollback`) from latest `develop`. Do not implement from this handoff branch.

---
# Issue #450: Bundle apply / verify / rollback（改訂 3 版）

## 前提ゲート（記録・通過）

| 項目 | 値 |
|------|-----|
| latest `develop` HEAD | `3b02cda42540b22a9a36817cae6e62f18fbc13a4` |
| Issue #449 | CLOSED（2026-07-29） |
| #449 merged PR | [#467](https://github.com/kooiei-in4a/amane-mailer/pull/467)、merge SHA = develop HEAD |
| #449 Agent B | 最終 SHA `a65441e…` で Approve（未解決 Blocker／Major なし） |
| #450 | OPEN、対象 branch／PR なし |
| 現在 branch | `develop`（clean、origin 同期） |
| ADR 0021 | Accepted |
| `easy-setup` status | partial（#447／#448／#449 evidence 済み） |
| #447／#448 | develop 済み（PR #466／#465） |

Risk class: Deployment / Security / Data durability / Cross-platform / Native AOT。

## 今回の承認条件 5 点＋Minor 2 件の反映

| # | 条件 | 反映箇所 |
|---|------|----------|
| 1 | external snapshot と ACTIVE 依存 Compose snapshot の分離 | §2 |
| 2 | pre-TX verifier 異常の durable state 定義 | §3／§10 |
| 3 | `NoManaged` から orphan record／binding を除外 | §3 |
| 4 | ACTIVE replace 前の external 再確認 | §5／§9 |
| 5 | SQLite sidecar を fresh 判定から除外 | §8 |
| Minor 1 | secret zero 化表現の精密化 | §2 |
| Minor 2 | purge を全 inspection 経路の不変条件へ | §10 |

---

## 1. Scope: send-ready は #450 の対象外

`SetupDoctorCommand` を呼ばず、doctor 相当判定も行わない。到達点は ADR 0021 D-07 の `Deployment configuration applied`（＋ verification record commit）に限定する。

根拠（実コード）:

```720:724:src/Amane.Mailer/Operations/SetupDoctorCommand.cs
        // Do not open the live SQLite file from setup doctor: even ReadOnly opens can create or
        // update WAL/SHM sidecars. Presence is reported here; schema currency stays a host ACTION.
        _report.AddPass("db_schema", "Database file exists.");
        _report.AddAction(
            "db_schema",
```

doctor は `RunPortAvailabilityCheck()` を含む preflight で、recreate 後は Mailer 自身が port を保持するため post-apply gate として成立しない。`--mode` / `--compose-file` 文字列引数と ambient config にも依存する。

canonical result 必須フィールド:

```text
deploymentState        = Active
configurationApplied   = true
verificationCommitted  = true
sendReadyAsserted      = false
sendReadyEvaluation    = not-evaluated
sendReadyReasonCode    = doctor-operation-not-available
actionCode             = complete_send_ready_evaluation
```

**所有権（PR 本文に明記。Issue 更新は禁止事項のため行わない）**: ambient 非依存 doctor engine ＋ typed `RunSetupDoctorAsync` と send-ready 成立は **#451**、read-only 表示は **#454**、Gate 実行は **#456**。

readiness gate（#450 の範囲）は「container が固定 healthcheck に PASS したこと」のみで、send-ready と呼ばない。

---

## 2. Transaction 入力の固定（2 層 snapshot）

**現状の欠陥**: 各操作が独立に compose 入力を再計算する。

```350:353:src/Amane.Mailer/Setup/SetupHostDockerAdapter.cs
            var composeEnvResult = _envComposer.TryCompose(
                session.Layout,
                out var composeEnv,
                out _);
```

`RunEffectiveInspectionAsync` も同じく再計算する（214-217 行）。apply 中に `managed/external.env` が変更されると migration と recreate／inspection の DB が分離し得る。

**修正**: [`SetupHostDockerSession`](src/Amane.Mailer/Setup/SetupHostDockerSession.cs) に **2 層の snapshot** を持たせる。external 層は ACTIVE に依存しないため fresh でも pin できる。

```text
SetupExternalInputSnapshot            … ACTIVE 不要
  ExternalInputDigest                 … external.env 正規化 canonical bytes の SHA-256
  NormalizedRuntimeIdentityInputs     … 正規化済み MAILER_DATA_PATH / MAILER_CONNECTION_STRING（host-only）
  RuntimeIdentityBinding              … 上記から derive した MAC（host-only）
  ExternalEnvironmentValues           … allowlisted external 値（process memory のみ）
  PinnedAt

SetupComposeInputSnapshot             … ACTIVE 必須
  ExternalInputSnapshot
  ExpectedActiveBundleId
  ExpectedActivationGeneration
  ComposedEnvironment
  ComposedAt
```

adapter API（caller は raw path／env／argv を渡さない）:

| API | 役割 |
|-----|------|
| `PinExternalInputsAsync(session, ct)` | external 入力を 1 回だけ読み、digest と runtime-identity binding を pin。**ACTIVE 不要** |
| `ComposeCurrentActiveInputAsync(session, ct)` | 現行 ACTIVE を strict 解釈して compose snapshot を作成（existing 専用） |
| `ComposeExpectedActiveInputAsync(session, expectedPointer, ct)` | 期待する `(bundleId, activationGeneration)` を明示して compose snapshot を作成（切替後・rollback 後）。on-disk ACTIVE が一致しなければ `ActiveGenerationMismatch` |
| `VerifyExternalInputsUnchangedAsync(session, ct)` | external 入力を再読込し pin 済み digest と照合 |

動作規則:

1. session 取得直後に `PinExternalInputs`（以後 transaction 中は snapshot を正本とする）
2. existing ACTIVE 用・candidate 用・rollback 用の compose 環境を **generation 単位で固定**
3. `ValidateCompose` / `RunMigration` / `StartOrRecreateMailer` / `StopFailedMailer` / `RunEffectiveInspection` / `AwaitMailerHealthy` / `InspectMigrationStatus` は compose snapshot のみ使用。未作成は `ComposeInputNotPinned` で失敗
4. 各操作は on-disk ACTIVE が snapshot の `(bundleId, activationGeneration)` と一致することを確認（不一致は `ActiveGenerationMismatch`）
5. `VerifyExternalInputsUnchanged` を **TX 作成前 / ACTIVE replace 直前 / verification commit 直前** の 3 点で実行（§5）

新 result code: `setup.docker.compose_input_not_pinned` / `setup.docker.external_input_not_pinned` / `setup.docker.external_input_changed` / `setup.docker.active_generation_mismatch`。

secret 取り扱い（Minor 1 の正確な表現）:

```text
所有する byte buffer は zero 化する。
managed string は参照を速やかに破棄し、session 外へ保持しない。
managed string の確実なメモリ上書きは保証しない。
```

snapshot の env・external 値・binding は log／result／record／stdout／stderr に出さない。比較と記録には値ではなく digest／enum のみ使う。

`ManagedComposeEnvComposer` には external 層のみを読む internal 経路を追加し、compose 全体合成と共通の validation を再利用する（二重実装しない）。

---

## 3. 状態モデルと永続ファイル

**Durable deployment state**

| state | 復元条件 |
|-------|----------|
| `NoManaged` | TX 無し ＋ ACTIVE 無し ＋ PREVIOUS 無し ＋ committed verification record 無し ＋ `runtime-identity.bind` 無し ＋ unsafe verifier residue 無し |
| `Active` | TX 無し ＋ ACTIVE 有り ＋ committed record が bundleId／generation 一致 ＋ binding stamp 一致 ＋ PREVIOUS 無し ＋ unsafe verifier residue 無し |
| `TransactionInProgress` | TX 有り ＋ `terminal=false` ＋ APPLY.lock 取得不可（保持プロセス生存） |
| `RecoveryRequired` | TX 有り ＋ `terminal=false` ＋ APPLY.lock 取得可／TX 無し ＋ PREVIOUS 有り（orphan）／TX 無し ＋ record または binding が単独残存 |
| `NeedsIntervention` | TX 有り ＋ `terminal=true`（`reasonCode` 付き）／TX 無しで ACTIVE・record・binding が不整合／`managed/tmp` に削除不能または未知の entry が存在 |

**orphan 残存物の扱い（Major 2／Major 1）**

| 残存 | 収束 |
|------|------|
| `invalidated` record のみ（strict-valid・path 安全） | recovery で durable delete → `NoManaged` |
| committed record のみ（ACTIVE 無し） | `RecoveryRequired` → 内容が strict-valid かつ ACTIVE 無しを確認できれば durable delete → `NoManaged`、不整合なら `NeedsIntervention` |
| `runtime-identity.bind` のみ | 同上 |
| PREVIOUS のみ（TX 無し） | `RecoveryRequired`。ACTIVE と committed record が整合し PREVIOUS が strict-valid なら PREVIOUS を durable delete → `Active`、それ以外は `NeedsIntervention` |
| record／binding の内容不正・相互不整合 | `NeedsIntervention` |
| unsafe verifier residue（削除不能／未知 entry） | `NeedsIntervention`。**TX が無い時点では `terminal=true` と表現せず** `outcome=NeedsIntervention` / `reasonCode=unsafe_verifier_residue` を返す。既存 TX の recovery 中に検出した場合のみ TX を `terminal=true` へ書換える |

**Operation outcome**（状態正本にしない）: `ApplySucceeded` / `FreshApplyFailed` / `ApplyFailedRollbackSucceeded` / `ApplyFailedRollbackFailed` / `RollbackSucceeded` / `CancelledBeforeActivation` / `UpgradeRequired` / `ConcurrentApplyRejected` / `RecoveryRequired` / `NeedsIntervention`。

`activationGeneration` は rollback でも過去値へ戻さず単調増加。

**永続ファイル**

```text
state/ACTIVE                      state/ACTIVE.tmp
state/PREVIOUS                    state/PREVIOUS.tmp      （TX スコープ）
state/APPLY.lock                  （#449）
state/TX.stamp                    state/TX.stamp.tmp
verification/last-record.json     (+ .tmp)
verification/runtime-identity.bind (+ .tmp)                （owner-only）
managed/tmp/                      （#449 所有の ephemeral verifier 置き場）
```

**ACTIVE**（strict。bare bundleId 不許可）

```json
{"schemaVersion":1,"bundleId":"…","activationGeneration":7}
```

**TX.stamp**

```json
{
  "schemaVersion": 1,
  "kind": "Apply|Rollback",
  "phase": "Prepared|ActiveSwitchPending|CandidateComposeValidating|MigrationPending|Migrating|Recreating|Inspecting|ReadinessChecking|BindingPending|VerificationPending|VerificationCommitted|RollbackPending",
  "terminal": false,
  "reasonCode": null,
  "candidateBundleId": "…",
  "targetActivationGeneration": 8,
  "previousBundleId": null,
  "previousActivationGeneration": null,
  "persistentSideEffectMayRemain": false,
  "persistentSideEffectKind": "none|database-migration",
  "startedAt": "…"
}
```

**verification/last-record.json**（secret／HMAC／salt／session key／private path なし）

```json
{
  "schemaVersion": 1,
  "status": "committed|invalidated",
  "bundleId": "…",
  "activationGeneration": 8,
  "fingerprintComparison": "matched|mismatch",
  "hostAtRest": "matched|mismatch|not-verified",
  "mountAttestation": "matched|mismatch|…",
  "bundleIntegrity": "matched|mismatch|not-verified|…",
  "imageReference": "repo@sha256:…",
  "composeIdentity": "…",
  "recordedSchemaVersion": 1,
  "runtimeIdentityBinding": "matched|mismatch|missing",
  "readiness": "passed|failed|not-evaluated",
  "sendReadyEvaluation": "not-evaluated",
  "committedAt": null
}
```

`status=committed` の必須条件: `fingerprintComparison=matched`、`bundleIntegrity=matched`、`readiness=passed`、`runtimeIdentityBinding=matched`、`committedAt` 非 null。`status=invalidated` は `committedAt=null` で判定材料にしない。

**PREVIOUS**: 直前の成功 ACTIVE の同一 schema コピー。TX スコープで、apply 成功時・rollback 成功時のいずれも TX 完了時に durable delete（bundle 本体は削除しない）。

---

## 4. runtime-identity binding stamp

```json
{"schemaVersion":1,"bundleId":"…","activationGeneration":8,"bindingMac":"…"}
```

- 入力: 正規化した `MAILER_DATA_PATH`（＋設定時は `MAILER_CONNECTION_STRING`）
- 派生: host sealing key から固定 info ラベル `amane-runtime-identity-v1` で derive した専用鍵の HMAC-SHA256
- 保存: `verification/runtime-identity.bind` に owner-only。public record には `matched|mismatch|missing` の enum のみ。stamp・path 原文を stdout／stderr／log／public result／record に出さない
- commit 順序: inspection／readiness 成功後に **binding を先に** 新 generation で atomic commit → verification record を atomic commit（**record が最終成功 authority**）
- `bind.activationGeneration > record.activationGeneration` は verification pending として扱い、recovery で再検証する
- binding commit 後に record commit が失敗した場合は TX を残し、次回 recovery で再検証（apply 成功にしない）
- rollback 時も rollback generation で binding を再 commit する
- `bind` 欠落・ACTIVE 不一致・digest 不一致は `missing`／`mismatch` として commit しない

---

## 5. Apply の write-ahead 順序

```text
 1. CheckDocker → AcquireSession（APPLY.lock）
 2. 既存 TX／orphan（PREVIOUS・record・binding）を確認（有れば recovery 経路へ）
 3. PurgeStaleMountVerifiers（§10 の不変条件）
 4. PinExternalInputs                                  … ACTIVE 不要
 5. candidate 静的検証（FINALIZED／recorded／fingerprint 再計算／host at-rest seal）
 6. image・compose identity・recorded schema 互換判定（不一致は切替前に UpgradeRequired）
 7. EnsurePinnedImageAvailable（ACTIVE 不要）
 8. existing: ComposeCurrentActiveInput → binding 照合 → PREVIOUS 適格性（§6）→ InspectMigrationStatus → migration 判定（§8）
    fresh   : external snapshot から DB 存在判定のみ（§8）。InspectMigrationStatus は呼ばない
 9. PREVIOUS を durable 化（existing かつ適格時のみ）
10. VerifyExternalInputsUnchanged（1 回目: TX 作成前）
11. TX.stamp phase=Prepared を作成                     ← write-ahead
12. verification record を invalidated へ atomic 置換
13. TX phase=ActiveSwitchPending
14. VerifyExternalInputsUnchanged（2 回目: ACTIVE replace 直前）
15. ACTIVE atomic 切替（新 activationGeneration = 前世代+1、fresh は 1）
16. ComposeExpectedActiveInput(candidate bundleId, 新 generation)（race はここで検出）
17. TX phase=CandidateComposeValidating → ValidateCompose（candidate env。ここまで無変更）
18. TX phase=MigrationPending → 必要時のみ persistentSideEffect* を write-ahead → phase=Migrating → RunMigration
19. TX phase=Recreating → StartOrRecreateMailer
20. TX phase=Inspecting → verifier 生成 → RunEffectiveInspection → integrity merge／fingerprint／image／composeIdentity／schema／runtime-identity 照合
21. TX phase=ReadinessChecking → AwaitMailerHealthy
22. VerifyExternalInputsUnchanged（3 回目: commit 直前。変化時は commit せず rollback／NeedsIntervention）
23. TX phase=BindingPending → runtime-identity.bind を atomic commit
24. TX phase=VerificationPending → verification record を committed で atomic commit（失敗は apply 失敗）
25. TX phase=VerificationCommitted → PREVIOUS durable delete → TX durable delete → session dispose
```

`ValidateCompose` は candidate env に対して切替後・変更前に実行する（composer が ACTIVE を必要とするため fresh では切替前に実行できない）。この位置なら失敗時も無変更で rollback／ACTIVE 除去が可能。

---

## 6. Previous 適格性と phase 別 recovery

**PREVIOUS 適格条件**

```text
ACTIVE 有り + committed record + record.bundleId == ACTIVE.bundleId
+ record.activationGeneration == ACTIVE.activationGeneration
+ binding stamp 一致（pin 済み external snapshot の binding と一致）
+ TX 無し + previous bundle が FINALIZED かつ host at-rest 検証可
```

| 分類 | 挙動 |
|------|------|
| `NoneFresh` | fresh apply。失敗は `FreshApplyFailed`。rollback 成功と表現しない |
| `Eligible` | existing apply。失敗時 rollback 対象 |
| `IneligibleExistingActive` | apply を開始せず `RecoveryRequired`。先に現行 ACTIVE の再検証を要求（ロールバック不能な状態で ACTIVE を切り替えない） |

**旧 Active の復元手順（`Prepared` / `ActiveSwitchPending` で ACTIVE が previous のまま crash した場合）**

invalidated record の削除だけでは `Active` に復元しない。

```text
PurgeStaleMountVerifiers → PinExternalInputs
→ previous ACTIVE を static validate（FINALIZED / recorded / fingerprint / host at-rest）
→ ComposeExpectedActiveInput(previous)
→ RunEffectiveInspection → integrity merge
→ AwaitMailerHealthy
→ runtime-identity 照合
→ previous generation で binding を再 commit
→ previous generation の verification record を再 commit
→ PREVIOUS / TX を durable delete → Active
```

再検証できない場合は `RecoveryRequired`（再試行可）または `NeedsIntervention`（`terminal=true`）。

**phase 別 recovery**

| TX phase | previous | 収束 |
|----------|----------|------|
| `Prepared` / `ActiveSwitchPending`（ACTIVE=previous） | Eligible | 上記「旧 Active の復元手順」 |
| `Prepared` / `ActiveSwitchPending`（ACTIVE 無し） | NoneFresh | TX と invalidated record を破棄して `NoManaged` |
| `ActiveSwitchPending`（ACTIVE=candidate） | Eligible | rollback（§9） |
| `ActiveSwitchPending`（ACTIVE=candidate） | NoneFresh | ACTIVE を除去して `NoManaged`（無変更のため安全） |
| `CandidateComposeValidating` | Eligible / NoneFresh | 無変更。rollback ／ ACTIVE 除去 |
| `MigrationPending` | Eligible / NoneFresh | migration 未実行。rollback ／ ACTIVE 除去 |
| `Migrating` | Eligible | rollback（migration は戻さない。`persistentSideEffectMayRemain=true` を継承） |
| `Migrating` | NoneFresh | `NeedsIntervention`（`terminal=true`、ACTION `review_database_schema`） |
| `Recreating` / `Inspecting` / `ReadinessChecking` | Eligible | rollback |
| `Recreating` / `Inspecting` / `ReadinessChecking` | NoneFresh | `StopFailedMailer` →副作用なしなら ACTIVE 除去して `NoManaged`、副作用有りなら `NeedsIntervention` |
| `BindingPending` | 任意 | 再 inspection／readiness →成功なら binding→record を再 commit、失敗は Eligible=rollback ／ fresh=`NeedsIntervention` |
| `VerificationPending` | 任意 | 同上（record 未 commit のため成功扱いしない） |
| `VerificationCommitted` | 任意 | record と binding を検証し一致すれば PREVIOUS／TX を削除して `Active`、不一致は `NeedsIntervention` |
| `RollbackPending` | Eligible | rollback を継続。再失敗は `NeedsIntervention` |
| `terminal=true` | 任意 | `NeedsIntervention` 維持。自動修復しない |

**stale APPLY.lock**: 生存プロセス保持中は削除せず `ConcurrentApplyRejected`。lock を取得できた場合のみ TX 照合で recovery 判定に入る。lock ファイル自体を推測削除しない。

**cancellation**: ACTIVE 切替前は `CancelledBeforeActivation`（TX 破棄）。切替後は呼出元 token が cancel 済みでも補償を完遂するため **bounded な内部 token**（rollback 全体 180s）で rollback し、期限内に完了できなければ TX を残して `RecoveryRequired`、不明確なら `terminal=true` で `NeedsIntervention`。

---

## 7. Integrity 統合と compose identity

- 新規 `SetupIntegrityMerger.Merge(hostAtRest, mountAttestation)` を ADR 0021 D-04 の表どおり実装。**両方 matched のときだけ最終 `matched`**。fingerprint 一致のみで integrity matched にしない
- host at-rest は [`SetupIntegritySealer.TryVerifySeal`](src/Amane.Mailer/Setup/SetupIntegritySealer.cs) と新規静的 validator。mount は `RunEffectiveInspectionAsync` の結果
- verifier 文書生成は [`SetupMountAttestation`](src/Amane.Mailer/Setup/SetupMountAttestation.cs) の既存 API を host 側 factory から使用
- `composeIdentity` は `TrustedReleaseInventory` の compose SHA256 群＋manifest schema version から導出した非 secret 識別子。record に保存し、不一致は stale
- **`ManagedComposeEnvComposer` 統合**: [`TryParseActiveBundleId`](src/Amane.Mailer/Setup/ManagedComposeEnvComposer.cs) の文字列走査を廃止し、新 `SetupActivePointer.TryParse`（strict: `schemaVersion==1`、`bundleId` 文字集合、`activationGeneration>=1`、bare 文字列拒否）へ差し替える。ACTIVE 正本の二重実装を作らない

---

## 8. Migration 判定

**read-only classification**

- [`SqlMigrationRunner`](src/Amane.Mailer/Operations/SqlMigrationRunner.cs) に `ClassifySchemaAsync` を追加（`OpenSchemaProbeConnectionAsync` のみ。書込・WAL 生成なし）
  - `DatabaseAbsent`: DB ファイル無し、または `schema_migrations` 無し
  - `Current`: bundled 全適用＋checksum 一致＋必須オブジェクト有り
  - `Behind`: 適用済みが bundled 先頭からの **checksum 一致した連続 prefix**（途中欠落は `AheadOrUnsupported`）
  - `AheadOrUnsupported`: 未知 version、途中欠落、checksum drift
  - `Unknown`: probe 失敗
- CLI: `db migrate --status --format json`（read-only、source-generated JSON、secret／path 非出力）。既定の `db migrate` 挙動は変更しない
- adapter typed op: `InspectMigrationStatusAsync(session, ct)` — 固定 argv `compose --profile ops run --rm --pull never mailer-migrate db migrate --status --format json`（compose snapshot を使用）

**経路分離**

```text
fresh / ACTIVE なし
  → external snapshot から host 側 DB ファイル群の存在確認のみ
  → InspectMigrationStatusAsync は呼ばない

existing / 有効な ACTIVE 有り
  → ComposeCurrentActiveInput → InspectMigrationStatusAsync
```

**fresh の DB 判定（SQLite sidecar 除外）**

```text
DB main file 無し
+ <db>-wal 無し
+ <db>-shm 無し
+ <db>-journal 無し
+ DB path が通常ファイルとして作成可能（親が存在し、symlink／reparse でない）
→ DatabaseAbsent → MigrationRequired
```

いずれかの sidecar が存在する場合は `NeedsIntervention` / `actionCode=review_database_files`。

**connection string 解析（fail-closed）**: 文字列操作ではなく `SqliteConnectionStringBuilder` で解析し、次はすべて fail-closed（`UpgradeRequired` または `NeedsIntervention`、path 原文は出さない）。

- `:memory:`
- URI 形式など host path へ安全に写像できない形式
- container 内の `/app/data` 外を指す path
- `Data Source` が directory
- relative path の解決が曖昧

**決定表**

| 状況 | 判定 |
|------|------|
| fresh ＋ DB／sidecar すべて不在 ＋ 作成可能 | `MigrationRequired`（ACTIVE 切替後に実行） |
| fresh ＋ DB 存在 | `UpgradeRequired` |
| fresh ＋ sidecar のみ存在 | `NeedsIntervention`（`review_database_files`） |
| fresh ＋ connection string／path 写像不能 | `UpgradeRequired` |
| 既存 ACTIVE ＋ `Current` ＋ candidate と previous の image digest 一致 | `MigrationNotRequired` |
| 既存 ACTIVE ＋ image digest 不一致 | `UpgradeRequired`（image upgrade は非目標） |
| 既存 ACTIVE ＋ `Behind` | `UpgradeRequired` |
| 既存 ACTIVE ＋ `AheadOrUnsupported` | `UpgradeRequired` |
| 既存 ACTIVE ＋ `Unknown` / `DatabaseAbsent` | `NeedsIntervention` |
| recorded schemaVersion ≠ 製品対応値、compose／launcher／digest 互換不明 | `UpgradeRequired` |
| committed verification 欠落 | migration 理由にせず `IneligibleExistingActive` → recovery |
| migration 中 crash | §6 の phase 表に従う。DB を自動で戻さない |

rollback 経路では migration を実行しない。

**副作用の分離**: `RunMigrationAsync` 直前に TX へ `persistentSideEffectMayRemain=true` / `persistentSideEffectKind=database-migration` を write-ahead。以降の result は次を分離する。

```text
configRollbackStatus          = succeeded|failed|not-applicable
persistentSideEffectMayRemain = true|false
persistentSideEffectKind      = none|database-migration
actionCode                    = review_database_schema | review_database_files | manual_intervention_required | …
```

---

## 9. 通常 rollback の write-ahead 順序

```text
 1. TX を kind=Rollback / phase=RollbackPending へ atomic 書換（persistentSideEffect* は保持）
 2. PREVIOUS と previous bundle を再検証（strict ACTIVE schema / FINALIZED / recorded / fingerprint / host at-rest）
 3. VerifyExternalInputsUnchanged（ACTIVE replace 前）
 4. rollback 用 activationGeneration を採番し、TX の targetActivationGeneration へ write-ahead
 5. ACTIVE を previous bundleId ＋ 新 generation へ atomic 切替（過去値へ戻さない）
 6. ComposeExpectedActiveInput(previous bundleId, 新 generation)（race はここで検出）
 7. migration は実行しない
 8. TX phase=Recreating → StartOrRecreateMailer
 9. TX phase=Inspecting → PurgeStaleMountVerifiers 済みを確認 → RunEffectiveInspection → integrity merge / fingerprint / image / composeIdentity / schema / runtime-identity
10. TX phase=ReadinessChecking → AwaitMailerHealthy
11. VerifyExternalInputsUnchanged（commit 直前）
12. TX phase=BindingPending → runtime-identity.bind を rollback generation で atomic commit
13. TX phase=VerificationPending → rollback verification record を committed で atomic commit
14. TX phase=VerificationCommitted
15. PREVIOUS durable delete → TX durable delete
```

rollback を成功と呼ばない条件: previous ACTIVE 無し、previous bundle 欠落、FINALIZED 無し、integrity 検証不能、ACTIVE replace 失敗、recreate 失敗、inspection 失敗、fingerprint／integrity 不一致、image／composeIdentity／schema 不一致、readiness 失敗、external 入力変化、binding／record commit 失敗、TX 完了を durable 化できない。

途中失敗時は TX に `terminal=true` と安全な `reasonCode` を保存し、新旧どちらが有効かを推測せず `NeedsIntervention` と ACTION を返す。

---

## 10. Ephemeral verifier の扱いと stale recovery

**記述修正**: session key は「メモリのみ」ではない。実装は次のとおり。

```202:212:src/Amane.Mailer/Setup/SetupHostDockerAdapter.cs
            hostVerifierPath = Path.GetFullPath(
                Path.Combine(verifierDir, $"mount-verifier-{Guid.NewGuid():N}.json"));
```

正しい契約:

```text
session key は単回 one-shot 用の短命 owner-only temp file にのみ保持する。
通常 env・通常 mount・record・log には残さない。
メモリ上の byte buffer は zero 化する。
```

**不変条件（Minor 2）**

```text
APPLY.lock 取得後、RunEffectiveInspection を実行する前には
必ず PurgeStaleMountVerifiers が成功していること。
```

apply／rollback／旧 Active 再検証／`BindingPending`・`VerificationPending` recovery のすべてに共通適用する（engine 側で assert する）。

`PurgeStaleMountVerifiersAsync(session, ct)` の規則:

1. `managed/tmp/` 直下のみを対象にし、`mount-verifier-<32 hex>.json` に厳密一致する名前だけを削除候補にする
2. owner-only かつ symlink／reparse でないこと、root 配下であることを確認（`SetupPathGuard`）
3. 削除できない、または想定外のエントリがある場合は unsafe verifier residue として `NeedsIntervention`。TX 未作成時は `reasonCode=unsafe_verifier_residue` を outcome で返し、既存 TX の recovery 中なら TX を `terminal=true` へ書換える
4. 残存の記録は enum／理由コードのみ。path も内容も出さない

---

## 11. Readiness operation

`AwaitMailerHealthyAsync(session, ct)` — 固定 argv `compose exec -T mailer /app/Amane.Mailer healthcheck`、compose snapshot 使用。

```text
overall timeout     : 120s
per-attempt timeout : 10s
retry interval      : 2s
```

adapter 既定の 5 分 timeout は使わず per-attempt を明示指定する。出力は公開せず canonical code のみ。`readiness=passed` は container 固定 healthcheck の PASS を意味し、send-ready ではない。

---

## 12. 変更予定ファイル

**新規（src）**

- `Setup/SetupApplyEngine.cs`（apply／rollback／recovery orchestration）
- `Setup/SetupExternalInputSnapshot.cs` / `Setup/SetupComposeInputSnapshot.cs`
- `Setup/SetupActivePointer.cs` / `SetupTransactionStamp.cs` / `SetupVerificationRecord.cs` / `SetupRuntimeIdentityBinding.cs`
- `Setup/SetupDurableAtomicWriter.cs`（stale tmp 削除・durable delete 含む）
- `Setup/SetupBundleStaticValidator.cs` / `SetupIntegrityMerger.cs` / `SetupMountVerifierFactory.cs`
- `Setup/SetupMigrationDecision.cs` / `Setup/SetupDatabaseFileProbe.cs`
- `Setup/SetupApplyResult.cs` / `SetupApplyResultCode.cs` / `SetupManagedDeploymentState.cs`

**変更（src）**

- `Setup/SetupHostDockerSession.cs` — external／compose の 2 層 snapshot 保持
- `Setup/SetupHostDockerAdapter.cs` — `PinExternalInputsAsync` / `ComposeCurrentActiveInputAsync` / `ComposeExpectedActiveInputAsync` / `VerifyExternalInputsUnchangedAsync` / `AwaitMailerHealthyAsync` / `InspectMigrationStatusAsync` / `PurgeStaleMountVerifiersAsync`、compose 系操作の snapshot 化
- `Setup/ManagedComposeEnvComposer.cs` — strict ACTIVE parser 統合＋external 層単独読取
- `Setup/TrustedSetupHostLayout.cs` / `Setup/SetupBundleLayout.cs` — PREVIOUS／TX／verification／binding／verifier tmp path
- `Setup/SetupDockerResultCode.cs` — pin／external／generation 系 code 追加
- `Setup/SetupJsonContext.cs` / `Setup/SetupHostDockerJsonContext.cs` — 新 DTO（source-generated）
- `Setup/SetupHostDockerSelfCheckCommand.cs` — strict ACTIVE 書込＋pin 追随
- `Operations/SqlMigrationRunner.cs` / `Operations/DbMigrateCommand.cs` / `Program.cs`（usage 1 行）— read-only `--status --format json`

**変更（tests）**: `Setup/SetupHostDockerAdapterTests.cs` / `Setup/SetupCoreHostDockerIntegrationTests.cs` — pin 前提と strict ACTIVE へ追随。

**変更（docs）**: `docs/implementation-status.json` — `easy-setup` evidence 追加（status は partial 維持）。

**触らない**: bundle 生成本体、ACS／#451、Admin bootstrap／#459、配布／#455、qualification／#456、OpenAPI／Contracts、DB migration 追加、data volume 操作。

---

## 13. テスト

**fake 中心（fault injection）** — `tests/Amane.Mailer.Tests/Setup/SetupApplyEngineTests.cs`

fresh／existing apply 成功、fresh 環境で ACTIVE 無しでも external pin と DB 判定が成立すること、fresh 切替前／切替後失敗、existing 切替前失敗、**apply 中の external.env 変更を 3 点（TX 前／replace 直前／commit 直前）で検出し ACTIVE を切り替えずに停止**、replace 後 race の `ComposeExpectedActiveInput` 検出、未 pin 操作拒否、ACTIVE generation 不一致、migration 不要／必要／失敗、`UpgradeRequired` 各分類（fresh＋DB 存在、Behind、途中欠落＝AheadOrUnsupported、digest 不一致、schema 超過、connection string 各 fail-closed）、sidecar 残存の `review_database_files`、`ValidateCompose` 失敗、recreate 失敗、inspection 失敗、fingerprint／host at-rest／mount attestation mismatch、secret 差し替え／古い secret／別 bundle secret／誤 mount、image／composeIdentity／schema／runtime-identity mismatch、readiness 失敗、binding commit 失敗、verification commit 失敗、rollback 成功／失敗、previous bundle 欠落、`IneligibleExistingActive`、orphan PREVIOUS／orphan record／orphan binding／invalidated のみ残存、stale verifier 残存（削除可／不可、TX 有／無）、concurrent apply 拒否、cancellation（切替前／migration 中／recreate 中／inspection 中）、crash recovery（TX 全 phase × previous 有無）、`persistentSideEffectMayRemain` の伝播、secret／private path／raw process output 非漏えい、send-ready を主張しないこと。

**実 FS（Docker 不要）** — `tests/Amane.Mailer.Tests/Setup/SetupDurableAtomicWriterHostTests.cs`

実 `HostSetupFileSystem` ＋ 実 temp directory で、fresh ACTIVE 作成、existing ACTIVE 置換、stale `.tmp` 残存時の上書き、TX durable delete ＋ parent flush、fresh failure 時の ACTIVE 除去、binding stamp の owner-only、SQLite sidecar 検出、symlink／reparse 拒否。Windows／Linux 双方の CI で実行。

実 Docker 操作・実 migration・実 ACTIVE 切替・実 rollback はこのセッションで行わない。live smoke は #456 へ引き渡す。

---

## 14. 検証

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build
node scripts/check-implementation-status.mjs
git diff --check
```

Native AOT: ローカル host では linux-x64 cross publish 不可・win-x64 は C++ linker 欠如のため、**Draft PR 作成は可能だが merge acceptance は CI の linux-x64 Native AOT publish 成功まで未達**として PR に明記する。低頻度 AOT path smoke は #456。

---

## 15. 計画セルフレビュー

- #449 の責任を再実装していない（typed op 追加と snapshot 化に限定。Docker argv は固定定数のみ）
- #451／#459／#455／#456 へ creep していない（send-ready と doctor を #451 へ明示委譲）
- ACTIVE 以外の activation authority を作らない（composer も strict DTO へ統合）
- transaction 中の Compose 入力が固定され、external 変更は ACTIVE 切替前に検出される
- fresh 環境で ACTIVE を要求する API に依存しない（external snapshot 層で判定）
- ambiguous state を成功扱いしない（`terminal` + `reasonCode`、pre-TX は outcome + reasonCode）
- `NoManaged` を orphan record／binding／verifier residue から区別する
- config rollback と persistent side effect を分離（write-ahead フラグと独立フィールド）
- secret／HMAC／private path 漏えい経路なし（binding は owner-only 別ファイル、public は enum のみ、verifier は短命 0600＋stale 掃除）。zero 化の限界を正確に記述
- recovery 不能を推測修復しない（phase 表で収束、`NeedsIntervention` は自動修復しない）
- 過度な複雑化を避ける（migration 実行は fresh＋DB 完全不在に限定、PREVIOUS は TX スコープ）

---

## 16. 引き渡し

- **#451**: ambient 非依存 doctor engine ＋ typed `RunSetupDoctorAsync`、send-ready 成立、mode 別 ACTION 変換、ACS／Staging workflow
- **#459**: Admin SQLite 副作用と config rollback の分離表示
- **#455**: release manifest の実配布と compose identity 供給
- **#456**: live Docker／migration／ACTIVE 切替／rollback／crash 実機シナリオ、Windows Desktop／Linux Engine、Native AOT path smoke、Gate 分類
