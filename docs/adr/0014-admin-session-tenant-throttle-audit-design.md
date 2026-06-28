# ADR 0014: Admin UI の durable session / tenant scope / throttle / audit 追跡の設計判断

- **Status:** Accepted
- **Date:** 2026-06-28
- **Supersedes planning gaps in:** [ADR 0013: 管理画面の脅威モデル・公開範囲・PII 取り扱い方針](0013-admin-threat-model-and-pii-policy.md) 実装ステータス表
- **Tracks:** [#55](https://github.com/kooiei-in4a/amane-mailer/issues/55)

## Context

[ADR 0013](0013-admin-threat-model-and-pii-policy.md) は管理画面の脅威モデル、Cookie + PBKDF2 認証、tenant scope、login throttle、監査ログの**目標**を定めた。公開レビュー後も、次のギャップが stricter 環境での採用 blocker として残っている。

| ギャップ | ADR 0013 参照 | 現状（2026-06-28） |
|----------|---------------|-------------------|
| durable server-side session と即時失効 | D-04, D-11 | ASP.NET Cookie auth のみ。server-side store なし。資格情報変更・無効化時の既存 session 即時失効なし |
| 管理者ごとの tenant scope | D-10 | 単一 `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH` |
| login throttle の再起動耐性 | D-04, D-11 | in-memory `ConcurrentDictionary` のみ |
| 監査イベントの残件 | D-08 | login success/failure と body view は永続化済み。logout / session expired / login rate limited、retention sweep、network identifier hash 化は未実装 |

[#6](https://github.com/kooiei-in4a/amane-mailer/issues/6) で audit 永続化の基盤（`admin_audit_events` テーブル、login/body view 記録）は完了した。本 ADR は**残りの Admin 基盤強化を広い実装に入る前に凍結する設計判断**を記録する。

前提:

- SQLite deployment は単一 Mailer プロセス（ADR 0013 D-11）。
- 管理画面はデフォルト無効・内部ネットワーク向け・experimental（ADR 0013 D-01, D-02）。
- Native AOT / full trimming 互換を維持する（[ADR 0013 Supplement](0013-admin-ui-aot-compatibility-supplement.md)）。
- HTTP 配送 API 契約（ADR 0012）は本 ADR の対象外。

## Decision

### D-01. durable server-side session と即時失効の follow-up scope

**判断:** SQLite を正本とする server-side session store を実装する。Cookie は opaque session id のみを運び、各 request の `OnValidatePrincipal` で DB 上の session 行を検証する。

**Phase 1（必須 follow-up）に含める:**

| 項目 | 内容 |
|------|------|
| データモデル | `admin_sessions` テーブル（session id、actor、issued_at、last_seen_at、absolute_expires_at、idle_expires_at、revoked_at、revoke_reason、credential_epoch）、`admin_config` テーブル（`applied_password_hash`、`credential_epoch`） |
| Cookie 形状 | 既存 Cookie 認証 middleware を維持。principal に server-side session id を載せ、DB lookup で有効性を確認 |
| 失効トリガー | 明示 logout、absolute / idle 期限切れ、**資格情報ハッシュ変更**（`AMANE_ADMIN_PASSWORD_HASH` 変更時は全 session 失効）、**管理者無効化**（将来の `admin_users.disabled`） |
| 同時 session 上限 | ADR 0013 D-04 に従い設定可能にする。production 既定は管理者あたり 3 本まで（env で上書き可）。超過時は最古 session から失効 |
| 監査 | session 失効時に `auth.session_expired` または `auth.logout` を記録（D-04 参照） |

**Phase 1 に含めない（明示 out-of-scope）:**

- 複数管理者アカウント（`admin_users` テーブル）は Phase 2 とする（D-02 と整合）。
- 水平スケール・複数 Mailer プロセス間の session 共有。
- OIDC / MFA / インターネット直接公開向け session 保護（ADR 0013 D-02 の「別 ADR」扱いを維持）。

**資格情報変更の即時失効（単一 env 管理者の interim）:** Phase 1 では `admin_config` に **適用済み** `applied_password_hash`（env の `AMANE_ADMIN_PASSWORD_HASH` と同形式の PBKDF2 文字列。平文パスワードは保存しない）と `credential_epoch`（整数）を保持する。startup 時に env の hash と `applied_password_hash` を比較し、不一致なら `credential_epoch` をインクリメントして `applied_password_hash` を更新し、既存 session を全失効する。新規 login で発行する session 行にはその時点の `credential_epoch` を保存し、`OnValidatePrincipal` で不一致なら reject する。将来 `admin_users` に移行したら per-user `credential_epoch` + `applied_password_hash` に置き換える。

### D-02. per-admin tenant scope の要否と導入条件

**判断:** tenant scope は **2 件以上の tenant が同一 Mailer SQLite DB に存在し、かつ Admin UI が有効な環境では必須**とする。tenant 数の判定は **`tenants.json` の登録件数と DB 内の distinct `tenant_id` の大きい方**を用いる（`mail_requests` 等に過去 tenant の行が残る場合も含む。restore 直後の空 DB は除く）。単一 tenant のみの Mailer、または Admin UI を有効にしない環境では、現行の env 単一管理者モデルを Phase 1〜2 の間は許容する。

| 条件 | tenant scope |
|------|----------------|
| 登録 tenant が 1 件のみ、かつ DB 内 distinct `tenant_id` も 1 件以下、staging/production 同居なし | **不要**（単一 tenant ローカル / 専用 Mailer。Phase 1〜2 の間は env 単一管理者を維持可） |
| 2 件以上の tenant が設定または DB に存在 | **必須**（Phase 2 完了前の Admin 有効化は **blocker**。shared Mailer 典型） |
| production tenant と non-production tenant の同居（ADR 0012 D-04a の典型） | **必須**。break-glass 管理者のみ全 tenant 横断を許可 |

**Phase 2（tenant scope follow-up）に含める:**

| 項目 | 内容 |
|------|------|
| データモデル | `admin_users`（username、password hash、disabled、credential_epoch、is_break_glass）、`admin_user_tenant_scopes`（admin_id、tenant_id） |
| 認可 | 一覧・詳細・本文・再送・キャンセルは scoped tenant のみ。DB backup は break-glass または全 tenant scope のみ（ADR 0013 D-09） |
| bootstrap | 初回 migration で env の username/hash から 1 行を seed。以降は CLI `admin user` サブコマンドで追加（実装 issue で詳細化） |
| 失効 | tenant scope 変更時は対象管理者の全 session を即時失効（ADR 0013 D-04） |

**Phase 2 に含めない:**

- Consumer `admin_users` との共有（ADR 0013 D-04 で却下済み）。
- tenant ごとの物理 DB 分離（ADR 0013 D-10 の neutral 項を維持）。

### D-03. login throttle の再起動耐性

**判断:** **はい。login throttle はプロセス再起動後も有効であるべき**とする（ADR 0013 D-04, D-11）。SQLite を正本とし、in-memory state はキャッシュに限定する。

**Phase 1 follow-up に含める（session store と同一 PR または直前の小 PR を推奨）:**

| 項目 | 内容 |
|------|------|
| データモデル | `admin_login_throttle`（key = normalized username + source identifier、failure_count、locked_until、updated_at） |
| source identifier | 既定は normalized source IP（`Connection.RemoteIpAddress`）。`MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true` 時は keyed hash を throttle key に使用し、raw IP をテーブルに保存しない（Phase 1 で実装。D-04 と同一 env フラグ） |
| 挙動 | 現行 `AdminLoginThrottle` と同じ threshold / cooldown（`LoginFailureLimit`、`LoginCooldown`）。再起動後も locked_until が未来なら 429 を返す |
| 監査 | 閾値到達で lock 作成時は `auth.account_temporarily_locked`、既に lock 中の試行で 429 を返す時は `auth.login_rate_limited` を記録（D-04） |
| 最適化 | 読み取りは in-memory cache 可。書き込みは SQLite transaction で正本を更新 |

**却下した代替案:** 再起動で throttle をリセットし運用でカバーする案 — ADR 0013 D-04 の「必須」要件とレビュー blocker に反するため採用しない。

### D-04. 監査 follow-up（logout / session expired / rate limited / retention sweep）

**判断:** ADR 0013 D-08 のイベント一覧を満たす残件を **Phase 1（auth イベント）と Phase 3（保持運用）に分割**して実装する。

**Phase 1 — auth 系イベント（session / throttle follow-up と同梱）:**

| イベント | トリガー | 記録方針 |
|----------|----------|----------|
| `auth.logout` | 明示 logout POST 成功時 | `WriteBestEffortAsync`（login と同様。認証フローを落とさない） |
| `auth.session_expired` | `OnValidatePrincipal` で absolute / idle 期限切れにより reject 時 | best-effort。同一 session で短時間に重複しないよう session id で dedupe（in-memory、5 分 TTL で十分） |
| `auth.account_temporarily_locked` | 失敗回数が閾値に達し `locked_until` が新規設定された時 | best-effort。actor は入力 username の normalized 値（ADR 0013 D-08 の `account temporarily locked`） |
| `auth.login_rate_limited` | 既に lock 中の状態で login 試行が 429 になる時 | best-effort。actor は入力 username の normalized 値（ADR 0013 D-08 の `login rate limited`） |
| network identifier hash | `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true` のとき Phase 1 auth イベントの `source_ip` に keyed SHA-256（server secret + salt）を保存。raw IP / 完全 UA は永続化しない。throttle key も同一フラグ（D-03） |

**Phase 3 — 保持運用（独立 follow-up）:**

| 項目 | 内容 |
|------|------|
| retention sweep | `MAILER_ADMIN_AUDIT_RETENTION_DAYS`（既定 180 日）を超えた `admin_audit_events` 行を削除。worker 起動時または日次タイマーで batch delete。30 日未満の設定は non-local で拒否（ADR 0013 D-08） |
| purge CLI | `admin audit purge` または `db admin-audit purge --older-than-days N` で明示削除（runbook 用） |
| backup / restore | retention sweep 後も backup に audit が含まれることを [restore runbook](../ops/restore-verification.md) に追記（実装 issue で doc 更新） |

**stdout ミラー:** 正本は引き続き SQLite。stdout structured log は二次チャンネル（ADR 0013 D-08、#6 完了方針を維持）。

### D-05. 実装フェーズと依存関係

広い Admin UI 変更に入る前に、次の順序で follow-up 実装する。

```text
Phase 1 — Session + throttle + auth audit 残件
  ├─ migration: admin_sessions, admin_login_throttle, admin_config
  ├─ server-side session validation + revocation (applied_password_hash + credential_epoch)
  ├─ durable login throttle (+ optional network identifier hash)
  └─ auth.logout / session_expired / account_temporarily_locked / login_rate_limited audit

Phase 2 — Multi-admin + tenant scope
  ├─ migration: admin_users, admin_user_tenant_scopes
  ├─ env bootstrap → admin_users seed
  ├─ tenant-scoped authorization on Admin API / UI
  └─ break-glass 監査強化

Phase 3 — Audit 運用完成
  ├─ retention sweep + purge CLI
  └─ backup/restore runbook 更新
```

| Phase | リスククラス | Native AOT | HTTP 契約 | ブロッカー |
|-------|-------------|------------|-----------|-----------|
| 1 | Security / Admin | 低（既存 Cookie + SQLite パターン） | 変更なし | shared Mailer で Admin 有効化する stricter 環境 |
| 2 | Security / Admin / PII | 低 | 変更なし | 2+ tenant 同居 + Admin 有効化 |
| 3 | Security / PII / Ops | 低 | 変更なし | 長期 audit 保持ポリシーがある環境 |

**提案 follow-up issue タイトル（maintainer が起票）:**

1. `[P2] Admin durable session store と即時失効を実装する`（Phase 1）
2. `[P2] Admin login throttle を SQLite 正本にする`（Phase 1、#1 と同 PR 可）
3. `[P2] Admin auth 監査イベント（logout / session expired / account locked / rate limited）を追加する`（Phase 1、#1 と同 PR 可）
4. `[P2] Admin multi-user と tenant scope 認可を実装する`（Phase 2）
5. `[P3] Admin audit retention sweep と purge CLI を実装する`（Phase 3）

## Alternatives Considered

| 案 | 却下理由 |
|----|----------|
| session / throttle を引き続き in-memory のみとする | ADR 0013 D-04/D-11 の要件と #55 の acceptance criteria に反する。再起動・資格情報変更後の漏洩リスクが残る |
| tenant scope を常時必須とする | 単一 tenant ローカル開発の摩擦が大きい。導入条件を「2+ tenant 同居」に限定しつつ shared Mailer では blocker とする |
| tenant scope なしで shared Mailer + Admin を許可する | production / staging 誤閲覧リスク（ADR 0013 D-10）が解消されない |
| Redis 等の外部 session store | 単一プロセス SQLite deployment と Native AOT 自己完結性を損なう |
| throttle のみ先に実装し session は後回し | 資格情報変更後の session 存続の方がインシデント影響が大きい。同一 Phase で設計整合を取る |
| audit retention を stdout 側だけで運用 | ADR 0013 D-08 は SQLite 正本。DB 肥大化と compliance の両方で sweep が必要 |

## Consequences

- _positive:_ ADR 0013 の「目標」と「現状」のギャップがフェーズ付きで追跡可能になる。
- _positive:_ stricter 環境の採用判断が「いつ blocker か」で明文化される。
- _positive:_ Phase 1 は HTTP 契約・OpenAPI に触れず、既存 Admin テストパターンを拡張できる。
- _negative:_ Phase 1 で migration 3 表 + session 検証が増え、Admin 基盤の複雑さが上がる。
- _negative:_ Phase 2 の multi-admin は bootstrap / CLI / 運用 runbook の更新が必要。
- _neutral:_ 単一 tenant ローカル開発は Phase 2 完了まで現行 env 管理者モデルを維持できる。
- _operational:_ **2+ tenant が DB または設定に存在する環境では、Phase 2 完了前の Admin 有効化を許可しない**（blocker）。単一 tenant 専用 Mailer で Phase 1 のみ先行する場合は、session/throttle の既知 limitation を SECURITY.md に明記する（実装 PR で更新）。

## References

- [ADR 0013: 管理画面の脅威モデル・公開範囲・PII 取り扱い方針](0013-admin-threat-model-and-pii-policy.md)
- [ADR 0013 Supplement: Admin UI Native AOT Compatibility](0013-admin-ui-aot-compatibility-supplement.md)
- [ADR 0012: メール送信マイクロサービス](0012-mail-via-mailer-microservice.md)
- [#55 Admin UI durable session / tenant scope / throttle 設計](https://github.com/kooiei-in4a/amane-mailer/issues/55)
- [#6 admin audit log 永続化](https://github.com/kooiei-in4a/amane-mailer/issues/6)（完了）
