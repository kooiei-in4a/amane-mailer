# ADR 0024: Sender と managed API Key identity

- **Status:** Accepted
- **Date:** 2026-09-04
- **Tracks:** [Issue #730](https://github.com/kooiei-in4a/amane-mailer/issues/730)

## Decision

- Sender は durable resource owner、API Key は revoke 可能な credential とする。Sender email は trim と lowercase-invariant で正規化し、instance 内で一意にする。
- Consumer identity と Sender selection は `amk_<key-id>.<secret>` の API Key だけで決める。secret は 256 bit CSPRNG で生成し、DB には SHA-256 digest だけを保存して fixed-time comparison する。
- mail request の idempotency namespace は `(sender_id, mail_request_id)` とし、受理時の `accepted_api_key_id` は audit evidence として保存する。key revoke 後も既存 request の所有権と配送は変えない。
- 既存配送 schema は compatibility boundary 経由で `tenant_id = sender_id`、`source_service = internal v2 sentinel` として利用する。sentinel は canonical payload、Provider operation identity、公開 API、Admin primary UX へ露出させない。
- Provider operation identity は `(sender_id, mail_request_id)` から生成する。suppression は stable instance identity に map し、instance-wide とする。
- Worker は disabled Sender を含む DB Sender の historical identity を解決する。新規 authentication は revoked key または disabled Sender を同じ unauthorized response で拒否する。
- v2 では tenant-scoped outbound webhook を起動しない。代替 webhook framework は導入しない。
- populated v1 mail state を Sender state として自動変換しない。v2 identity migration は populated v1 state を unsupported major upgrade として fail-safe に拒否する。

## Consequences

Provider の managed configuration と Sender/API Key の管理 UI はそれぞれ #731 と #732 に残る。#730 は既存 provider configuration を compatibility adapter の入力としてのみ利用し、Sender identity の正本にはしない。
