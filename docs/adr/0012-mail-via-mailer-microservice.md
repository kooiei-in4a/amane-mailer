# ADR 0012: メール送信マイクロサービス

- **Status:** Approved
- **Date:** 2026-06-18
- **Approved:** 2026-06-19

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

配送セマンティクスは at-least-once とする。Provider 送信成功後、Mailer DB の `delivered` 更新前にプロセスが停止した場合、stale `processing` 復旧により同じメールが再送される可能性がある。現在の SQLite deployment では許容リスクとして runbook に記載し、将来必要に応じて provider message id 確認または配信結果照会を追加する。

### D-08. GET 状態確認 API は MVP 外

`GET /internal/mail-requests/{mailRequestId}` は将来予約に留める。MVP では Consumer は `dispatched` までを管理し、配信結果は Mailer 側の運用確認に閉じる。

## Consequences

- _positive:_ Mailer と Consumer の境界が Contracts と JSON schema で固定され、並行作業時の契約乖離を抑えられる。
- _positive:_ ACS は Mailer だけが知るため、Consumer 側から ACS 依存を削除できる。
- _positive:_ Event Grid を本番前必須にするか後続強化にするかを明確にできる。
- _negative:_ `payload_json` と Mailer DB は宛先・本文を保持するため、ログマスク、DB権限分離、将来のTTL/暗号化が必要になる。

## References

- [Service spec](../service-spec.md)
- [OpenAPI contract](../api/openapi.yaml)
