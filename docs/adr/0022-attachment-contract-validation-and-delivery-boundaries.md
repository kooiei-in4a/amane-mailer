# ADR 0022: 添付ファイルの公開契約・検証・短期spool配送境界

- **Status:** Draft
- **Date:** 2026-08-04
- **Tracks:** [#523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- **Planning:** [#517](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- **Implementation prerequisite:** [#533](https://github.com/kooiei-in4a/amane-mailer/issues/533)
- **Provider evidence:** [#525](https://github.com/kooiei-in4a/amane-mailer/issues/525) / PR [#529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- **Resource evidence:** [#526](https://github.com/kooiei-in4a/amane-mailer/issues/526) / PR [#531](https://github.com/kooiei-in4a/amane-mailer/pull/531)、[#532](https://github.com/kooiei-in4a/amane-mailer/issues/532) / PR [#535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- **Amends:** [ADR 0012 D-05 / D-05a](0012-mail-via-mailer-microservice.md)（添付対応時の `payload_hash`、数値 `byte_length`、durable `202 Accepted` の引き渡し）
- **Preserves:** [ADR 0012](0012-mail-via-mailer-microservice.md)（Contracts正本・認証・冪等性・Worker配送）、[ADR 0013](0013-admin-threat-model-and-pii-policy.md)（PII）、[ADR 0019](0019-sqlite-single-process-boundaries.md)（SQLite／単一プロセス）、[ADR 0020](0020-bounce-ingestion-and-suppression.md)（配信結果）
- **Future work:** [#518](https://github.com/kooiei-in4a/amane-mailer/issues/518)（malware）、[#524](https://github.com/kooiei-in4a/amane-mailer/issues/524)（長期storage／retry／retention）、[#534](https://github.com/kooiei-in4a/amane-mailer/issues/534)（automatic retry）

## Context

Amane Mailer v1.3.0では、Consumerが `POST /internal/mail-requests` に添付を含められる能力を追加する。

既存Mailerは、POSTで依頼をSQLiteへ永続化して `202 Accepted` を返し、Workerが後からDBを読みproviderへ送信する。添付binaryを一切保存せずにこの方式を維持することはできないため、Koo決定（2026-08-04）として次を採用する。

> 添付binaryは、Mailer管理の短期一時spoolへ保存し、既存のdurable `202 Accepted` と非同期Worker配送を維持する。
> spoolは送信完了までの引き渡し専用であり、長期保管、利用者ダウンロード、自動再送のための保存には使わない。

### 確定済みMVP条件

| 項目 | v1.3.0 MVP |
|---|---:|
| 添付数 | 1メール最大5件 |
| 1ファイル | decoded binary最大2 MiB |
| 添付合計 | decoded binary最大5 MiB |
| provider送信データ | 件名、本文、宛先、添付を含む最大8 MiB |
| Consumer HTTP envelope | 最大16 MiB |
| 引き渡し | Mailer管理の短期一時spool |
| 自動再送 | 添付メールでは行わない |
| malware scan | Mailerでは行わない。Consumer責任 |

### Resource evidence

#532／PR #535では実Docker／cgroup total-memory limitを使ってACS JSON envelope経路を検証した。

| memory | 結果 |
|---|---|
| 256 MiB | 軽量・通常条件はPASS。最大条件はmanaged `OutOfMemoryException` |
| 512 MiB | 最大条件・concurrency 2を3回とも安定PASS |

運用要件は次とする。

- Minimum runnable memory: **256 MiB**
- Minimum memory for full attachment-limit support: **512 MiB**
- Recommended production memory: **512 MiB以上**

256 MiBでは最大添付条件と8 MiB近傍provider envelopeを保証しない。production runtimeとSMTP／MIME経路は実装後に再qualificationする。

## Decision drivers

1. 既存のdurable `202 Accepted → Worker送信`を維持する。
2. provider呼び出し前にcount、size、integrity、type、provider budgetをfail-closedで判定する。
3. raw Base64をDBへ保存しない。
4. spoolを長期保管、自動再送、Admin downloadへ拡張しない。
5. crash／restart後もaccepted requestを送信可能にする。
6. attachment content、private path、PII、secret、provider raw responseを観測面へ出さない。
7. 同一 `mail_request_id` で異なるbinaryを同一内容として扱わない。

## Decision

### D-01. Public attachment contract

`POST /internal/mail-requests` に `attachments` 配列を追加する。配列順は添付順であり、payload identityの一部とする。

各attachment DTOは次を持つ。

| JSON field | 型 | 必須 | 意味 |
|---|---|---|---|
| `file_name` | string | yes | 表示・provider送信用filename |
| `content_type` | string | yes | Consumer申告値。Mailerが再検証する |
| `content_base64` | string | yes | RFC 4648標準Base64。whitespace不可 |
| `content_sha256` | string | yes | decoded binary SHA-256、lowercase hex |
| `byte_length` | integer | yes | decoded binary byte数 |

- `attachments` 未指定または空配列は添付なし。
- 最大5件。超過はproviderを呼ばず422。
- inline image、Content-ID、外部URL、upload sessionは非目標。
- Consumer申告値を信用せず、Mailerがdecoded binaryから再計算する。

### D-02. Byte budget

MiBは `1 MiB = 1,048,576 bytes` とする。

| budget | 上限 | 定義 |
|---|---:|---|
| per-file decoded | 2,097,152 | Base64 decode後 |
| total decoded | 5,242,880 | 1メール内合計 |
| provider envelope | 8,388,608 | providerへ渡すserialized body。transport header除外 |
| Consumer HTTP envelope | 16,777,216 | POST request body。HTTP header除外 |

- `Content-Length` が上限超過ならread前に拒否する。
- `Content-Length` がない場合もcapped streamで上限+1 byteまでに制限する。
- request全体を無制限にbufferしてから判定しない。
- provider envelopeはprovider固有のqualified estimatorまたはexact pre-serializationで呼び出し前に判定する。
- SMTP／MIMEはheader folding、boundary、Base64 wrappingを含むserialized messageを実装時にqualificationする。

### D-03. Attachment integrityとpayload identity

`content_sha256` は個別attachment binaryのidentity／integrityを表す。

`payload_hash` はrequest全体の冪等性identityを表し、raw Base64は含めない。各attachmentについて次を配列順で含める。

```text
file_name      … NFC正規化後
content_type   … validation後のcanonical Content-Type
byte_length    … JSON integer
content_sha256 … lowercase hex
order          … attachments配列位置
```

ADR 0012 D-05の「数値型をhash対象へ含めない」制約は、attachment-enabled payloadの `byte_length` と `order` に限り改訂する。C#／Python／TypeScriptで同じhash vectorを用意する。

同一 `mail_request_id` でbinary、metadata、orderのいずれかが異なる場合は `IDEMPOTENCY_CONFLICT` とする。

### D-04. Validationとacceptanceの順序

初回POSTと冪等再送の両方で次を行う。

1. Consumer HTTP envelope cap。
2. JSON syntax、duplicate property、strict UTF-8。
3. attachment count。
4. bounded Base64 decode。
5. per-file／total cap、SHA-256、decoded length。
6. digest／length照合。
7. filename／file type validation。
8. canonical metadataと`payload_hash`再計算。
9. 既存request identity比較。
10. provider envelope事前判定。
11. D-08のspool commit。
12. DB transaction commit。
13. `202 Accepted`。

既存requestがあることを理由にdecode／digest／length検証を省略しない。

### D-05. Filename contract

`file_name` は保存pathではない。original filenameをpath componentとして使用しない。

1. Unicode NFC。
2. NFC後UTF-8 1〜255 bytes。
3. empty、`.`、`..` を拒否。
4. `/`、`\`、NUL、control characterを拒否。
5. trailing dot／spaceを拒否。
6. Windows予約名をcase-insensitiveに拒否。
7. 同一メール内のcase-insensitive duplicateを拒否。

添付順は配列位置で保持し、filenameでsortしない。

### D-06. Allowed file types

| extension | canonical Content-Type | validation |
|---|---|---|
| `.pdf` | `application/pdf` | signature、構造parse、暗号化拒否、`%%EOF`後はASCII whitespaceのみ |
| `.jpg`, `.jpeg` | `image/jpeg` | SOI、marker、EOI、trailing payloadなし |
| `.png` | `image/png` | signature、chunk length／CRC、IHDR、IEND、trailing payloadなし |
| `.docx` | OOXML Word | ZIP構造、必須entry、macroなし |
| `.xlsx` | OOXML Excel | ZIP構造、必須entry、macroなし |
| `.csv` | `text/csv` | D-07＋quote／record構造 |
| `.txt` | `text/plain` | D-07 |

extension、declared Content-Type、構造結果の3つが一致しない場合は拒否する。

明示的に拒否するもの:

- executable／installer／script
- macro-enabled Office
- legacy binary Office
- generic archive
- encrypted／password-protected PDF
- polyglot／trailing payload
- extension／type mismatch

DOCX／XLSXのZIP上限:

- entry数最大1,024
- 合計uncompressed最大32 MiB
- 1 entry最大16 MiB
- traversal、absolute path、NULを含むentry名を拒否

### D-07. TXT／CSV contract

- strict UTF-8。BOMは許可しcanonical処理では除去。
- NUL拒否。
- TAB、CR、LF以外のC0 controlとDEL拒否。
- 1行最大64 KiB UTF-8 bytes。
- CSV delimiterはcomma固定。
- quote／record構造のみ検証し、業務列schemaは検証しない。
- encoding推測やShift_JIS変換を行わない。

### D-08. Short-lived durable spool

添付binaryはMailer管理のspool rootへdecoded binaryとして保存する。raw Base64とConsumer request bodyは保存しない。

#### Storage identity

- spool keyはMailer生成のopaque ID。
- original filename、tenant ID、email、subjectをpath componentへ使用しない。
- DBにはopaque spool keyとsanitized metadataを保存する。
- public result、Admin、log、metricsへprivate pathを出さない。
- spool rootはSQLite dataと同じローカルdeployment境界のpersistent volumeに置き、process／container restartを跨ぐ。

#### Acceptance sequence

1. request-scoped staging directoryを作成。
2. Base64をbounded decodeしながらgenerated filenameへwrite。
3. size、digest、length、typeを検証。
4. fileをflushし、committed request directoryへatomic rename。
5. SQLite transactionでmail request、attachment metadata、opaque spool keyを保存。
6. DB commit後に `202 Accepted` を返す。
7. queue signalはDB commit後。

filesystemとSQLiteは単一transactionではないため、次をcanonical recoveryとする。

- committed spoolあり／DB rowなし: grace period後にorphanとして削除。
- DB rowあり／spool missing: providerを呼ばず `ATTACHMENT_STORAGE_MISSING` で終端。
- staging残存: startup reconciliationで削除。
- idempotent repostで既存rowあり: 新しいstagingは削除し、identity一致なら `already_accepted`。

#### Worker sequence

1. DB rowとattachment metadataをclaim。
2. opaque keyからspool fileをopen。
3. file size／digestを再照合。
4. provider envelope gate。
5. provider send。
6. result／attemptをDBへdurable finalize。
7. requestがterminalになった後だけspoolを削除。

provider send後にfinalizeが失敗した場合はspoolを残す。既存のprior-success evidenceでDeliveredへ収束した後に削除する。

#### Lifecycle

spoolは次のためだけに保持する。

- queued
- processing
- provider結果のdurable finalize待ち
- crash／restart recovery

次では削除する。

- Delivered
- terminal Failed
- DeadLettered
- Cancelled
- DeliveryUnknown
- validation／acceptance失敗時のstaging

削除はbest-effort一回ではなく、startup reconciliationとperiodic cleanupで収束させる。

#### Automatic retry

添付メールはprovider送信失敗後に自動再送しない。

- retryable provider failureも添付メールではterminal failure。
- `DELIVERY_UNKNOWN`も自動再送しない。
- process crashなどprovider invocation前と証明できる場合のみ、lease reclaim後に同一spoolから初回sendを継続できる。
- 利用者の再送は新しい送信操作として再添付する。

#### Backup／restore

MVP spoolは長期保存データではないが、accepted requestの引き渡し中はdurable stateである。

- routine backupはnon-terminal attachment requestが存在する場合に開始しない。
- backup preflightはqueued／processing／finalize-pending attachment requestを検出して明示的にFAILする。
- したがって成功したbackup setにはactive spoolを含めない。
- restore後にDB rowがspoolを要求する不整合を検出した場合はproviderを呼ばず `ATTACHMENT_STORAGE_MISSING`。
- spoolを含む一貫したonline backup、自動再送、長期retentionは#524／#534の将来範囲。

### D-09. Runtime memory contract

| 項目 | 値 |
|---|---:|
| Minimum runnable memory | 256 MiB |
| Minimum memory for full attachment-limit support | 512 MiB |
| Recommended production memory | 512 MiB以上 |

256 MiBでは最大条件を保証しない。production実装後、ACS／SMTP、256／512 MiB、concurrency 2、Native AOTを再qualificationする。

### D-10. Provider mapping

#### SMTP／MIME

- attachment `Content-Transfer-Encoding` はBase64固定。
- filename、Content-Type、orderはvalidated metadataから設定。
- serialized MIME全体を8 MiB gateで判定。

#### ACS

- `EmailAttachment`／REST JSON Base64 transport。
- SMTPのContent-Transfer-Encoding概念は適用しない。
- request-side mappingは#525で承認済み。
- delivered-side byte integrityはKoo決定によりdescope済み。

estimatorはactualを過小評価してはならない。SDK／MimeKit version変更時はexact capture comparisonを更新する。

### D-11. Failureと`DELIVERY_UNKNOWN`

validation、size、integrity、type、provider envelope、spool write失敗はprovider invocation前のnon-retryable failureとする。

providerへ送信された可能性を否定できない場合は通常失敗と分けて `DELIVERY_UNKNOWN` とし、自動再送しない。

### D-12. Fixed failure categories

```text
TOO_MANY_ATTACHMENTS
ATTACHMENT_TOO_LARGE
ATTACHMENT_TOTAL_TOO_LARGE
MAIL_PAYLOAD_TOO_LARGE
ATTACHMENT_INVALID_BASE64
ATTACHMENT_DIGEST_MISMATCH
ATTACHMENT_LENGTH_MISMATCH
ATTACHMENT_FILENAME_INVALID
ATTACHMENT_DUPLICATE_FILENAME
ATTACHMENT_TYPE_NOT_ALLOWED
ATTACHMENT_CONTENT_MISMATCH
ATTACHMENT_ENCRYPTED
ATTACHMENT_STORAGE_UNAVAILABLE
ATTACHMENT_STORAGE_MISSING
DELIVERY_UNKNOWN
```

- validation／size／type: 422
- same ID／different identity: 409
- temporary spool／DB failure before acceptance: 503
- auth／allowlist: ADR 0012を維持

DB／logには固定categoryのみを記録し、provider raw messageを保存しない。

### D-13. Security、privacy、malware

Mailer担当:

- count／size cap
- Base64／UTF-8
- digest／length
- filename
- allowed type／structure
- encrypted PDF、macro、archive、script拒否
- provider envelope
- spool isolation／cleanup

Consumer担当:

- 添付元の信頼性
- malware scan／DLP
- 添付してよい情報の判断

v1.3.0でMailerへmalware scannerを導入しない。

画面、一般log、metrics、Issue／PR Evidenceへ次を出さない。

- attachment content
- raw Base64
- request body
- private spool path
- credential／secret
- provider raw response
- PIIを含むexception
- digest全文

### D-14. User／Admin visibility

表示可:

- file name
- file size
- attachment count
- validated type
- success／failure／delivery unknown
- 理解可能な固定理由

Admin binary downloadは非目標。

### D-15. Implementation gate

本ADRがDraftの間、production implementationはHOLD。

Accepted前に必要:

1. #533のspool契約と本ADRの独立レビュー。
2. Consumer cap、filename、file type、ZIP上限、failure categoryのレビュー。
3. filesystem／SQLite crash windowとreconciliationの実証計画。
4. KooによるADR Accepted。

Accepted後の推奨順:

1. #533 spool core／reconciliation／backup preflight。
2. Contracts／OpenAPI／SDK hash vectors。
3. request／attachment validation。
4. metadata／spool reference schemaとmigration。
5. ACS／SMTP mapping、envelope gate、`DELIVERY_UNKNOWN`。
6. Admin／SDK／docs。
7. production Docker／Native AOT／provider qualification。

Acceptedだけでlive ACS、release、publish、tagを許可しない。

## Consequences

### Positive

- 既存のdurable `202 Accepted → Worker`を維持できる。
- process／container restart後もaccepted attachmentを送信できる。
- raw Base64をDBへ保存しない。
- provider invocation前に不正・超過を拒否できる。
- spoolをterminalまでに限定し、長期storageへ拡張しない。

### Negative

- filesystemとSQLiteを跨ぐreconciliationが必要。
- backupはactive attachment requestがある間FAILする。
- spool volumeのdisk full／permission／orphan運用が増える。
- 添付メールのprovider failureは自動再送しない。
- strict validationにより一部の曖昧なfileを拒否する。

## Alternatives considered

### A. Request-lifetime send

不採用。既存202意味論、API latency、timeout、Consumer retryを大きく変更する。

### B. Process-local memory handoff

不採用。crashでbinaryを失い、durable acceptedを維持できない。

### C. DB BLOB／長期storage

MVPでは不採用。DB肥大化、retention、backup、encryption、自動再送の責任が増える。将来#524で再評価する。

### D. Managed short-lived spool

**採用。** 既存非同期Workerとdurable acceptanceを維持しつつ、保存範囲をterminalまでに限定する。

## Review gates before Accepted

- spool commitとDB commitの順序に、acceptedなのにbinaryがない経路がないか。
- orphan／missing／staging recoveryが収束するか。
- provider invocation後のfinalize failureでspoolを早期削除しないか。
- backup preflightがactive spoolを確実に検出するか。
- no-automatic-retryとpre-invocation crash recoveryが矛盾しないか。
- 16 MiB Consumer capが5 MiB decoded／8 MiB provider条件を受け付けるか。
- 8 MiB policyをACS／SMTPで測定できるか。
- hash vector、filename、file validation、failure categoryが一意か。
- private path／binary／Base64が観測面へ漏れないか。

## References

- [Issue #523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- [Issue #525 / PR #529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- [Issue #526 / PR #531](https://github.com/kooiei-in4a/amane-mailer/pull/531)
- [Issue #532 / PR #535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- [Issue #533](https://github.com/kooiei-in4a/amane-mailer/issues/533)
- [Docker qualification evidence](../cd/reports/2026-08-04-issue-532-docker-memory-qualification.md)
