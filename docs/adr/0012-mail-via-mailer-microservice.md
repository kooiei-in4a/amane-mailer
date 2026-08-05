# ADR 0012: メール送信マイクロサービス

- **Status:** Approved
- **Date:** 2026-06-18
- **Approved:** 2026-06-19
- **Amended by:** [ADR 0023](0023-multiple-recipient-contract-and-delivery-semantics.md)（2026-08-05）
- **Related PR:** [#539](https://github.com/kooiei-in4a/amane-mailer/pull/539)
- **Related issues:** [#519](https://github.com/kooiei-in4a/amane-mailer/issues/519)、[#517](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- **Decision owner:** Koo
- **Design approval:** 2026-08-05（production implementationは未承認・未実施）

## Context

メール送信の責務を Consumer アプリから Mailer マイクロサービスへ分離する。Consumer は App Outbox と app-worker で非同期配送し、ACS を直接知る責務を Mailer 側に集約する。

```text
app -> mail_outbox -> app-worker -> Mailer API -> Mailer Worker -> ACS / Mailpit
```

HTTP 契約、冪等ハッシュ、認証・テナント境界、設定 schema を設計着手前に凍結する。

## Decision

### D-01. 契約正本は `src/Amane.Mailer.Contracts/`

`POST /internal/mail-requests` の Request/Response DTO、状態値、エラーコードは `src/Amane.Mailer.Contracts/` を正本とする。OpenAPI を生成する場合も Contracts から生成し、Consumer 固有の Contracts には置かない。

エラーレスポンスの `code` は `MailerErrorCodes` の SCREAMING_SNAKE を正本とする。

`docs/api/openapi.yaml` は Consumer 向けの HTTP reference / 公開 schema として維持し、Contracts と runtime 実装に同期される成果物とする。現行 CI は OpenAPI の構造を `scripts/validate-openapi.mjs` で検証する。自動 drift check 追加までの運用として、HTTP 契約変更 PR は DTO property、required / nullable、`MailerErrorCodes`、`MailRequestAcceptanceStatus`、`MailRequestStatus`、payload hash 対象、JSON unknown / duplicate property 挙動について Contracts / runtime / OpenAPI / tests を比較した結果を validation notes に残す。自動 drift check は後続タスクとして追加する。

### D-02. `to[]` は配列、MVP は最大1件

将来の複数宛先対応を壊さないため、リクエスト形状は `to[]` とする。MVP では最大1件だけを許可し、超過時は `TOO_MANY_RECIPIENTS` で 422 を返す。

### D-03. App からの `from` 上書きは禁止

送信元は tenant 設定の `default_from` のみを使う。App からの `from` 指定は受け付けない。ACS 検証済みドメイン外の送信元を避けるため、初期契約で凍結する。

### D-04. Tenant Bearer tokens and source_service allowlist

Mailer API requires a Bearer token per tenant. Tenant config also owns the
`source_services` allowlist; an unregistered `source_service` returns 403.

develop, staging, and production use the same shared `MAILER_BASE_URL`, but
must use separate `tenant_id` values and separate Bearer tokens.

### D-04a. Shared Mailer service

develop, staging, and production may use one shared Mailer service. Each
consumer compose connects through a dedicated Docker network (configured via
`MAILER_NETWORK_NAME`). The shared Mailer service owns its own SQLite database,
and backup/restore/monitoring is operated from the shared Mailer compose directory.

Production uses a tenant with `live_sending=true`. Develop and staging stay
`live_sending=false` unless a temporary tenant JSON is mounted for a narrow
live-send drill.

### D-05. `payload_hash` は RFC 8785 JCS + SHA-256

App と Mailer は同じ payload を JCS canonical JSON に正規化し、SHA-256 hex を比較する。同一 `mail_request_id` かつ異なる `payload_hash` は `IDEMPOTENCY_CONFLICT` とする。

`payload_hash` は配送 payload の内容一致検証であり、ルーティング envelope は含めない。

| 区分 | フィールド | 理由 |
|------|------------|------|
| hash 対象 | `source_service`, `purpose`, `to`, `subject`, `html_body`, `text_body`, `reply_to`, `metadata` | 宛先・件名・本文・目的・送信元サービス・App が付与する配送メタデータの内容一致を検証する |
| hash 除外 | `tenant_id` | 認証・ルーティング属性。tenant は Bearer token と URL 境界で検証する |
| hash 除外 | `mail_request_id` | 冪等キーそのもの。内容ハッシュに含めると再生成・比較の責務が混ざる |
| hash 除外 | `payload_hash` | 自己参照になるため除外 |
| hash 除外 | `scheduled_at` | 初回配送スケジュール envelope。内容一致検証と分離し、再スケジュールを専用 API で行う |

任意フィールドは、App が payload JSON に出力した場合だけ JCS 対象になる。明示的な `null` は `null` としてハッシュ対象に含まれる。

hash 対象 JSON に数値型は含めない。`metadata` の値は string のみとし、数値を送る場合も App が文字列化する。これにより現在の自前 canonicalizer はメール payload 契約に限定され、RFC 8785 の数値表現差異を契約面で回避する。

### D-05a. `POST /internal/mail-requests` のHTTP結果

Mailer API の `202 Accepted` は「Mailer が依頼を永続化した」ことを表す。

| 状況 | HTTP | body `status` / `code` |
|------|------|------------------------|
| 初回受付 | 202 | `accepted` |
| 冪等再送（同一内容） | 202 | `already_accepted` |
| 同一 ID・異 hash | 409 | `IDEMPOTENCY_CONFLICT` |
| 宛先超過 | 422 | `TOO_MANY_RECIPIENTS` |
| 未認証 tenant token | 401 | `UNAUTHORIZED_TENANT` |
| 未許可 `source_service` | 403 | `SOURCE_SERVICE_NOT_ALLOWED` |
| Mailer 一時障害 | 503 | retryable error response |

### D-06. ACS SDK は `WaitUntil.Completed` で固定する

現時点では ACS SDK の `WaitUntil.Completed` を採用する。Mailer Worker は ACS 送信呼び出しで終端結果を待ち、`delivered` / `failed` までを Worker 内で確定する。

この決定により、Event Grid は production rollout 前の必須ゲートではなく、将来の配信結果・バウンス精緻化として扱う。Mailer API の `202 Accepted` は引き続き「Mailer が依頼を永続化した」ことだけを表し、ACS SDK の非同期 202 とは別レイヤーである。

| 選択 | Event Grid の扱い |
|------|-------------------|
| `WaitUntil.Completed` | **採用**。production rollout 前の必須条件ではない |
| `202 + 非同期追跡` | 不採用。採用する場合は配信結果追跡の設計を前倒しで更新する必要がある |

### D-07. SQLite deployment の Worker は 1 レプリカ固定、配送は at-least-once

現在の SQLite deployment では Mailer Worker は 1 レプリカ固定で運用する。実装は SQLite 上の `lock_token` / `lock_expires_at` lease と fencing で stale `processing` を再 claim できるが、単一 SQLite ファイルを共有する複数 Worker の水平化は現在の運用対象外とする。

配送セマンティクスは at-least-once とする。lease 失効後の finalize 競合では delivered attempt 証跡を残し reclaim 時の再送を抑止する（#238）が、Finalize 自体の例外 / timeout や Admin 手動リトライなどでは同一依頼が再送されうる。詳細な運用上の注意は metrics runbook（`mail_finalize_skipped_total`）を参照する。

### D-08. GET 状態確認 API は MVP 外

**Status:** Superseded by D-08a (#216)

`GET /internal/mail-requests/{mailRequestId}` は将来予約に留める。MVP では Consumer は `dispatched` までを管理し、配信結果は Mailer 側の運用確認に閉じる。

### D-08a. Consumer 向け GET 配送ステータス照会 API

`GET /internal/mail-requests/{mail_request_id}?tenant_id={uuid}&source_service={name}` を Consumer 向け HTTP API として提供する。

- POST と同様に Bearer 認証 + `tenant_id` + `source_service` allowlist を適用する。
- 返却は PII を含まない最小セット（`mail_request_id`, Worker 配送 `status`, `attempt_count`, `max_attempts`, `next_attempt_at`, `scheduled_at`, `accepted_at`, `delivered_at`, `last_error_code`）。
- 存在しない ID および他 tenant の ID は **404 統一**（存在有無を漏らさない）。
- Contracts 正本は `MailRequestStatusResponse`（`MailRequestCreateResponse.status` の acceptance 値と混同しない）。

### D-09. ADR 0023 amendment: request-level provider submission evidence

[ADR 0023](0023-multiple-recipient-contract-and-delivery-semantics.md) は、本ADRの添付なし配送契約を全面的に書き直さず、v1.3.0の複数recipientとprovider submission uncertaintyに必要な境界だけをamendする。複数To／CC／BCCの公開契約、recipient canonical persistence、recipient feedback、BCC privacyの正本はADR 0023とし、本ADRの既存single-recipient契約は後方互換境界として維持する。

添付なしのplain requestにもrequest単位のdurable provider submission evidenceを導入する。provider call前に `Started` をunique `request_id` でcommitし、Started以上のstartup recovery、stale claim recovery、periodic sweep、manual retryはproviderを再呼び出ししない。通常応答時はevidence terminal stateとrequest／attempt finalizeを同一transactionで保存し、provider acceptanceが不明なStartedは `Unknown` evidence、request `DeliveryUnknown`へ収束させる。

evidence／dispositionの意味は次で固定する。

| 分類 | 意味 |
|---|---|
| `NoEvidence` | ADR 0023 D-06の厳格な7条件を満たし、provider call前と証明できるrowなし。enum値として保存しない |
| `Accepted` | provider acceptanceを確認可能な証拠がある。存在しないprovider情報を推測・生成しない |
| `DefinitelyRejected` | provider未受理を明示的かつ一意に証明できる。単なるFailed、例外、timeout、履歴不足ではない |
| `Unknown` | acceptanceも明示拒否も証明できない。requestは`DeliveryUnknown`へ収束 |

`Unknown`／`DeliveryUnknown`はautomatic retry、whole-request manual retry、provider再呼び出しを禁止する。legacy requestのclassification不能もUnknownへ寄せ、migration 018とWorker readinessをfail-closedにする。migration 018はSQL実装を本PRで行わない。

Started insert、DefinitelyNotSubmittedからStartedへのtransition、terminal finalizeはcurrent claim tokenと未期限切れleaseによるclaim／lease fencingを要求する。affected rowsが0ならproviderを呼ばず、partial finalizeはrollbackする。lease expiry後のstale WorkerはStarted commit／finalizeを成功できず、reclaim後も同じevidenceを先に読む。

このamendmentのdesign approvalはproduction implementation、migration SQL、releaseを承認しない。既存の添付requestのStarted marker、request単位at-most-once、Started-only recovery、terminal後spool cleanupはADR 0022で正本化し、ADR 0023と矛盾しない範囲で参照する。

## Consequences

- _positive:_ Mailer と Consumer の境界が Contracts と JSON schema で固定され、並行作業時の契約乖離を抑えられる。
- _positive:_ ACS は Mailer だけが知るため、Consumer 側から ACS 依存を削除できる。
- _positive:_ Event Grid を本番前必須にするか後続強化にするかを明確にできる。
- _negative:_ `payload_json` と Mailer DB は宛先・本文を保持するため、ログマスク、DB権限分離、将来のTTL/暗号化が必要になる。

## References

- [Service spec](../service-spec.md)
- [OpenAPI HTTP reference](../api/openapi.yaml)
