# ADR 0022: 添付ファイルの公開契約・検証・短期spool配送境界

- **Status:** Draft
- **Date:** 2026-08-04
- **Tracks:** [#523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- **Planning:** [#517](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- **Implementation prerequisite:** [#533](https://github.com/kooiei-in4a/amane-mailer/issues/533)
- **Provider evidence:** [#525](https://github.com/kooiei-in4a/amane-mailer/issues/525) / PR [#529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- **Resource evidence:** [#526](https://github.com/kooiei-in4a/amane-mailer/issues/526) / PR [#531](https://github.com/kooiei-in4a/amane-mailer/pull/531)、[#532](https://github.com/kooiei-in4a/amane-mailer/issues/532) / PR [#535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- **Amends:** [ADR 0012 D-05 / D-05a / D-07](0012-mail-via-mailer-microservice.md)（添付時のpayload hash、durable acceptance、provider invocation後の再送禁止）
- **Explicit exception to:** [ADR 0015 D-02 / D-03 / D-05](0015-manual-retry-cancel-state-transitions.md)（DB上にcanonical attachment metadataまたはspool referenceを持つrequestのmanual retry／manual cancel境界。添付なしrequestの契約は変更しない）
- **Preserves:** [ADR 0013](0013-admin-threat-model-and-pii-policy.md)（PII）、[ADR 0019](0019-sqlite-single-process-boundaries.md)（SQLite／単一プロセス）、[ADR 0020](0020-bounce-ingestion-and-suppression.md)（配信結果）
- **Future work:** [#518](https://github.com/kooiei-in4a/amane-mailer/issues/518)（malware）、[#524](https://github.com/kooiei-in4a/amane-mailer/issues/524)（長期storage／online backup／retry reuse）、[#534](https://github.com/kooiei-in4a/amane-mailer/issues/534)（automatic retry）

## Context

Amane Mailer v1.3.0では、Consumerが `POST /internal/mail-requests` に添付を含められる能力を追加する。

既存Mailerは、POSTで依頼をSQLiteへ永続化して `202 Accepted` を返し、Workerが後からproviderへ送信する。添付でもこのdurable asynchronous modelを維持するため、decoded binaryをMailer管理の短期一時spoolへ保存する。

spoolはqueued、processing、provider結果のfinalize待ち、crash／restart recoveryだけに使用する。長期保管、Admin download、自動再送、Dead Letter後の再利用、既存requestのmanual retryには使用しない。

ADR 0015では、添付なしrequestの `Failed`／`DeadLettered` を手動再送すると `attempt_count` を0へ戻す。このため、異なるdispatch cycleで `mail_attempts.attempt_number` は再び1から始まり、数値が重複し得る。添付requestのprovider submission identityを `(request_id, attempt_number)` とすると、このAccepted済み契約と衝突する。また、terminal stateへのdurable遷移後にspoolを削除するため、添付requestを既存request IDのまま再送するbinaryも残らない。

本ADRは、添付requestをADR 0015の明示的例外とし、provider submission evidenceをrequest単位で一意にする。添付なしrequestのmanual retryとattempt履歴契約は変更しない。

### 確定済みMVP条件

| 項目 | v1.3.0 MVP |
|---|---:|
| 添付数 | 1メール最大5件 |
| 1ファイル | decoded binary最大2 MiB |
| 添付合計 | decoded binary最大5 MiB |
| provider送信データ | 件名、本文、宛先、添付を含む最大8 MiB |
| Consumer HTTP envelope | 最大16 MiB |
| 引き渡し | Mailer管理の短期一時spool |
| 自動再送 | provider invocation開始後は行わない |
| 添付requestのmanual retry | 全terminal stateから禁止 |
| 添付requestの再送方法 | 新しい `mail_request_id`、再添付、新しいPOST |
| malware scan | Mailerでは行わない。Consumer責任 |

### Resource evidence

#532／PR #535では実Docker／cgroup total-memory limitを使い、ACS JSON envelope経路を検証した。

| memory | 結果 |
|---|---|
| 256 MiB | 軽量・通常条件はPASS。最大条件はmanaged `OutOfMemoryException` |
| 512 MiB | 最大条件・concurrency 2を3回とも安定PASS |

運用要件:

- Minimum runnable memory: **256 MiB**
- Minimum memory for full attachment-limit support: **512 MiB**
- Recommended production memory: **512 MiB以上**

256 MiBでは最大添付条件と8 MiB近傍provider envelopeを保証しない。production runtimeとSMTP／MIME経路は実装後に再qualificationする。

## Decision drivers

1. durable `202 Accepted → Worker送信`を維持する。
2. provider呼び出し前にcount、size、integrity、type、provider budgetをfail-closedで判定する。
3. raw Base64とattachment-bearing request bodyを保存しない。
4. provider invocation後のcrashで二重送信しない。
5. spoolとSQLiteの不整合を有限時間で収束させる。
6. backupがspoolを含まないDB rowを成功snapshotへ取り込まない。
7. filenameをpotential PIIとして扱う。
8. C#／Python／TypeScriptで同じpayload hashを生成できる完全なprojectionを固定する。
9. ADR 0015の添付なしrequestのmanual retryと、`mail_attempts.attempt_number`のdispatch cycle間再利用を壊さない。
10. terminal cleanup後にbinaryが残らない添付requestを、既存request IDのまま再送可能と誤認させない。

## Decision

### D-01. Public attachment contract

`POST /internal/mail-requests` に `attachments` 配列を追加する。配列順は添付順であり、payload identityの一部とする。

| JSON field | 型 | 必須 | 意味 |
|---|---|---|---|
| `file_name` | string | yes | 表示・provider送信用filename |
| `content_type` | string | yes | Consumer申告値。Mailerが再検証する |
| `content_base64` | string | yes | RFC 4648標準Base64。whitespace不可 |
| `content_sha256` | string | yes | decoded binary SHA-256、64文字lowercase hex |
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

### D-03. Attachment identityとpayload hash

`content_sha256` は個別attachment binaryのidentity／integrityを表す。`payload_hash` はrequest全体の冪等性identityを表し、raw Base64は含めない。

#### Hash projectionの正本

添付なしの場合、requestで `attachments` が未指定でも空配列でも、**hash projectionから `attachments` propertyを省略する**。これにより既存の添付なしADR 0012 hashと互換性を維持する。

添付が1件以上ある場合だけ、既存ADR 0012 D-05のhash対象objectへ次の `attachments` propertyを追加する。

```json
{
  "attachments": [
    {
      "file_name": "請求書.pdf",
      "content_type": "application/pdf",
      "byte_length": 12345,
      "content_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "order": 0
    }
  ]
}
```

契約:

- attachment objectのfield集合は上記5項目で固定する。
- `order` は0始まりで、attachments配列indexと一致するMailer／SDK生成値とする。Consumer DTOには別のorder fieldを設けない。
- `byte_length` は0以上2,097,152以下のJSON integer。
- `order` は0以上4以下のJSON integer。
- 小数、指数表現、浮動小数点を使用しない。
- object property順はRFC 8785 JCSが決定する。
- filenameはNFC後の値、Content-TypeはD-06のcanonical値、digestはlowercase hexを使う。
- ADR 0012 D-05の「数値型をhash対象へ含めない」制約は、この `byte_length` と `order` に限り改訂する。
- C#／Python／TypeScriptで、添付なし、1件、複数件、順序違い、日本語filenameを含む共通hash vectorを固定する。

同一 `mail_request_id` でbinary、canonical metadata、orderのいずれかが異なる場合は `IDEMPOTENCY_CONFLICT` とする。

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
12. SQLite transaction commit。
13. `202 Accepted`。
14. queue signal。

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
| `.docx` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | ZIP構造、`[Content_Types].xml`、`word/document.xml`、macroなし |
| `.xlsx` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | ZIP構造、`[Content_Types].xml`、`xl/workbook.xml`、macroなし |
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

添付binaryはMailer管理のspool rootへdecoded binaryとして保存する。raw Base64とattachment-bearing Consumer request bodyは保存しない。

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
- DB rowあり／spool missing: providerを呼ばず `ATTACHMENT_STORAGE_MISSING` でterminal Failedへ遷移。
- staging残存: startup reconciliationで削除。
- idempotent repostで既存rowあり: 新しいstagingは削除し、identity一致なら `already_accepted`。

#### Provider submission identityとevidence

添付requestのprovider submission evidenceは、**request単位で最大1つ**とする。概念上のidentityは `request_id` uniqueである。実装上Mailer生成の一意submission IDを使用してもよいが、1つの添付requestに複数のprovider submission lifecycleを作ってはならない。

最低限、次のevidenceを保持する。

```text
request_id
submission_state
provider
submission_started_at
fencing / lock evidence
provider_message_id
completed_at
```

submission stateは少なくとも次を持つ。

```text
Started
Succeeded
DefinitiveFailed
Unknown
```

既存の `mail_attempts` は配送履歴として維持する。`mail_attempts.attempt_number` はADR 0015どおり現dispatch cycleのスナップショットであり、手動再送後の異なるdispatch cycle間で同じ値が重複可能である。

```text
mail_attempts.attempt_number:
  dispatch cycle間で重複可能

attachment submission evidence:
  requestごとに一意
```

`mail_attempts`全体へ `(request_id, attempt_number)` の一意制約を追加してはならない。submission evidenceのidentityに `attempt_number` を使用してはならない。

#### Provider invocation boundary

添付メールでは、providerを呼び出す前にprovider submission開始をSQLiteへdurableに記録する。provider invocationは添付requestごとに最大1回とする。

1. Workerがrequestをlease／lock tokenでclaimする。
2. spoolのsize／digestとprovider envelopeを再検証する。
3. 同じlock tokenでfenceしたSQLite transactionにより、request単位で一意なsubmission evidenceを作成し、`submission_state = Started`、provider、`submission_started_at`、fencing／lock evidenceをcommitする。配送履歴として必要な `mail_attempts` rowは、このrequest単位identityとは分離して記録する。
4. `Started` commit成功後だけproviderを呼び出す。
5. provider結果を受けた後、submission evidence、attempt結果、request terminal stateを同じSQLite transactionでcommitする。

submission evidenceが既に存在する場合、新しいprovider submission lifecycleを作成せず、evidenceに従って収束する。

`Started` commit後、実際のprovider call前にcrashした場合も、安全側に `DeliveryUnknown` へ収束させる。未送信の可能性より二重送信防止を優先する。

provider adapterは結果を次に分類する。

- `Succeeded`: provider受理が確定した。
- `DefinitiveFailed`: provider未受理または明示拒否を証明できる。
- `Ambiguous`: timeout、connection loss、cancellation、response parse／local persistence failure等でprovider受理を否定できない。

#### Recoveryとlease expiry

expired lease、startup recovery、periodic sweepはrequest単位のsubmission evidenceを先に確認する。

| submission evidence | provider再呼び出し | 収束先 |
|---|---|---|
| evidenceなし | provider未呼び出しと証明可能なため、同じspoolから初回sendを再開可能 | 通常処理を継続 |
| `Succeeded` | 禁止 | `Delivered` |
| `DefinitiveFailed` | 禁止 | `Failed` |
| `Started`のみでterminal evidenceなし | 禁止 | `DeliveryUnknown` |
| `Unknown` | 禁止 | `DeliveryUnknown` |

`Started` commit後、provider call前にcrashした場合も `DeliveryUnknown` へ収束させる。submission evidenceを削除、差し替え、再作成してproviderを再呼び出す経路を設けない。

provider送信後、DB finalizeに失敗した場合はspoolを残す。requestがDelivered、Failed、Cancelled、DeadLettered、DeliveryUnknownのいずれかへdurableに収束した後だけ削除する。

#### Delivery semantics

ADR 0012 D-07のat-least-once配送は、添付なしrequestでは維持する。

添付requestは次の明示的な例外とする。

- submission marker前は初回sendを再開できる。
- submission marker後は自動再送しない。
- provider受理を証明できない場合は `DeliveryUnknown` で終端する。
- provider invocationはrequestごとに最大1回であり、新しいattempt numberを作って再送してはならない。

これにより、添付requestはprovider invocationについてat-most-onceを優先し、結果不明を明示する。

#### Manual retryとADR 0015例外

添付requestは、**すべてのterminal stateからmanual retryを禁止**する。

| 遷移元 | 添付requestのmanual retry |
|---|---|
| `Delivered` | 禁止 |
| `Failed` | 禁止 |
| `DeadLettered` | 禁止 |
| `Cancelled` | 禁止 |
| `DeliveryUnknown` | 禁止 |

添付requestを既存requestのまま `Queued` へ戻す操作を禁止する。Admin manual retry APIが添付requestを受けた場合はHTTP 409と固定reason code `ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED` を返す。

添付requestかどうかは、公開入力の `attachment_count` 等を再解釈して判断せず、DB上のcanonical attachment metadataまたはspool referenceの存在で判断する。terminal cleanup後にspoolが削除済みでも、canonical attachment metadataにより同じ禁止判定を維持する。

利用者が再送する場合は、次の方法だけを許可する。

```text
新しい mail_request_id
+
ファイルを再添付
+
新しい POST /internal/mail-requests
```

添付なしrequestのADR 0015契約は変更しない。

```text
Failed → Queued: 許可
DeadLettered → Queued: 許可
attempt_count: 0へリセット
mail_attempts.attempt_number: dispatch cycle間で重複可能
```

#### Manual cancel boundary

添付requestのmanual cancelはsubmission marker前だけ許可可能とする。

- `Queued`: submission evidenceが存在しない場合に限りcancel可能。
- stale `Processing`: submission evidenceが存在しない場合に限り、ADR 0015のconditional update／先勝ち境界でcancel可能。
- `Started`、`Succeeded`、`DefinitiveFailed`、`Unknown`のsubmission evidenceが存在する場合: manual cancel禁止。

submission marker後は、provider resultまたはrecoveryにより `Delivered`、`Failed`、`DeliveryUnknown` のいずれかへ収束させる。provider invocation後のrequestを `Cancelled` へ上書きしてはならない。

#### `DeliveryUnknown` state

`DeliveryUnknown` はfailure categoryだけではなく、新しいDB／domain／public terminal statusとする。

```text
DB / domain enum: DeliveryUnknown
Public status: delivery_unknown
last_error_code / category: DELIVERY_UNKNOWN
Terminal: yes
Automatic retry: no
Manual retry: no for attachment request
Provider invocation: prohibited after transition
Spool cleanup: durable transition後に実行
```

GET status、Admin表示、internal delivery eventは `delivery_unknown` を通常の `failed` と区別する。既存sweep、expired lease、manual retryは、添付requestの `DeliveryUnknown` をQueued／Processingへ戻してはならない。利用者の再送は新しい `mail_request_id` と新しい添付uploadで行う。

#### Spool lifecycle

spoolはqueued、processing、provider結果のfinalize待ち、crash／restart recoveryだけに保持する。manual retry用には保持しない。

次のterminal stateへdurableに遷移した後に削除する。

- Delivered
- Failed
- DeadLettered
- Cancelled
- DeliveryUnknown

添付requestは全terminal stateからmanual retryを禁止するため、terminal後にbinaryを必要とする正規経路はない。削除はbest-effort一回ではなく、startup reconciliationとperiodic cleanupで収束させる。cleanup失敗は既に確定した送信結果を巻き戻さず、requestをnon-terminalへ戻さない。

### D-09. Backup acceptance gate

MVP routine backupはactive spoolをbackup setへ含めない。そのため、backup preflightとSQLite snapshotの間に新規添付acceptanceが入り込まないcross-process gateを必須とする。

#### Durable maintenance lease

SQLiteにattachment acceptanceを制御するdurable maintenance leaseを置く。Admin backupとCLI backupは同じBackup Coordinatorを使用する。

backup sequence:

1. owner token、fencing token、`expires_at`を持つexclusive backup leaseをSQLiteへcommitする。
2. attachment acceptance transactionは、同じSQLite transaction内で有効なbackup leaseがないことを確認する。有効なleaseがある場合はspool／DB commitを行わず503を返す。
3. backup lease取得後にnon-terminal attachment requestを確認する。存在する場合はleaseを解放してbackupをFAILする。
4. active requestがなければ、leaseを保持したままSQLite Online Backup snapshotを完了する。
5. temp backupの検証とpublishが完了または失敗した後にleaseを解放する。

SQLite write serializationにより、acceptanceが先にcommitした場合はbackup側のactive-row確認で検出し、backup leaseが先にcommitした場合はacceptance側が503で停止する。

backupが長時間化する場合、Coordinatorはleaseをheartbeat更新する。lease更新またはfencingを失った場合はbackupを中止し、temp backupを成功artifactとしてpublishしない。crashで残った期限切れleaseはstartup／次回backupで回収する。

成功したroutine backup setにはnon-terminal attachment rowを含めない。restore後にDB rowがspoolを要求する不整合を検出した場合はproviderを呼ばず `ATTACHMENT_STORAGE_MISSING` で終端する。active spoolを含むonline backupは#524の将来範囲とする。

### D-10. Runtime memory contract

| 項目 | 値 |
|---|---:|
| Minimum runnable memory | 256 MiB |
| Minimum memory for full attachment-limit support | 512 MiB |
| Recommended production memory | 512 MiB以上 |

256 MiBでは最大条件を保証しない。production実装後、ACS／SMTP、256／512 MiB、concurrency 2、Native AOTを再qualificationする。

### D-11. Provider mapping

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
ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED
DELIVERY_UNKNOWN
```

- validation／size／type: 422
- same ID／different identity: 409
- 添付requestへのAdmin manual retry: 409、`ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED`
- spool／DB／backup gate failure before acceptance: 503
- auth／allowlist: ADR 0012を維持

`DELIVERY_UNKNOWN` はD-08のterminal statusに対応する固定categoryであり、単なるFailedの別名にはしない。`ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED` はDB上のcanonical attachment metadataまたはspool referenceを持つrequestへのmanual retry拒否を表す。DB／logには固定categoryのみを記録し、provider raw messageを保存しない。

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
- attachment-bearing request body
- private spool path
- credential／secret
- provider raw response
- PIIを含むexception
- digest全文
- raw filename

### D-14. Filename PIIとUser／Admin visibility

filenameは個人名、顧客番号、案件名等を含み得るpotential PIIとして扱う。

#### General observability

- 一般log、metrics、stdout、Issue／PR Evidence、failure categoryへraw filenameを出さない。
- cleanup、spool、validation logはattachment index、canonical type、size category、固定reason codeだけを使用する。
- audit log metadataにもraw filenameを入れない。

#### Admin

- Admin一覧と詳細ではfilenameをデフォルトで非表示またはマスクする。
- 非マスクfilename表示はADR 0013のPII reveal capabilityを要求する明示操作とする。
- reveal操作を `attachment_filename_reveal` として監査し、actor、request identity、結果を記録する。監査ログにfilename本文を保存しない。
- `MAILER_ADMIN_PII_LIST_MODE=visible` 相当を利用する場合も、ADR 0013の到達制限、権限、監査を適用する。

#### Consumer

filenameをConsumer APIへ返す場合は、POSTと同じBearer tenant／source_service境界とrequest ownershipを適用する。他tenant、未認証、一般公開面へ返さない。

表示可能な非binary metadata:

- マスク済みまたは権限確認済みfile name
- file size
- attachment count
- validated type
- success／failure／delivery unknown
- 理解可能な固定理由

Admin binary downloadは非目標。

### D-15. Implementation gate

本ADRがDraftの間、production implementationはHOLDする。

Accepted前に必要:

1. B-01、M-01、M-02、M-03、M-04反映後の独立再レビュー。
2. M-04について、`(request_id, attempt_number)` identityが削除され、request単位で一意なsubmission evidence、添付requestの全terminal manual retry禁止、Started後のmanual cancel禁止、ADR 0015の通常メール契約維持が確認されること。
3. provider submission marker、DeliveryUnknown state、backup maintenance leaseの実証計画。
4. hash vectors、filename、file type、ZIP上限、failure categoryの確認。
5. KooによるADR Accepted。

Accepted後の推奨順:

1. domain／DB state: DeliveryUnknown、request単位submission state、backup maintenance lease。
2. #533 spool core／reconciliation／backup coordinator。
3. Contracts／OpenAPI／SDK hash vectors。
4. request／attachment validation。
5. metadata／spool reference schemaとmigration。
6. ACS／SMTP mapping、envelope gate、submission marker。
7. Admin PII表示／SDK／docs。
8. production Docker／Native AOT／provider qualification。

Acceptedだけでlive ACS、release、publish、tagを許可しない。

## Consequences

### Positive

- durable `202 Accepted → Worker`を維持できる。
- provider invocation後の二重送信を避け、結果不明を明示できる。
- request単位のsubmission evidenceにより、ADR 0015のattempt number再利用と衝突しない。
- 添付なしrequestの既存manual retryを維持できる。
- terminal後のspool cleanupと、添付requestの再送方法が矛盾しない。
- backup成功artifactへspoolなしactive rowが混入しない。
- SDK間でattachment hash inputが一意になる。
- filename PIIを既存ADR 0013境界へ統合できる。

### Negative

- submission marker後、provider call前にcrashした場合もDeliveryUnknownとなり、未送信でも自動回復しない。
- 添付requestは、`Failed`／`DeadLettered`を含むすべてのterminal stateから既存requestのmanual retryを利用できない。
- 利用者の再送には、新しい `mail_request_id` とファイル再添付が必要になる。
- filesystemとSQLiteを跨ぐreconciliationが必要。
- backup中は新規添付acceptanceが一時的に503となる。
- spool volumeとmaintenance leaseの運用が増える。
- 添付メールは既存のat-least-once配送と異なる結果意味論を持つ。

## Alternatives considered

### A. Request-lifetime send

不採用。既存202意味論、API latency、timeout、Consumer retryを大きく変更する。

### B. Process-local memory handoff

不採用。crashでbinaryを失い、durable acceptedを維持できない。

### C. DB BLOB／長期storage

MVPでは不採用。DB肥大化、retention、backup、encryption、自動再送の責任が増える。将来#524で再評価する。

### D. Managed short-lived spool

**採用。** 既存非同期Workerとdurable acceptanceを維持しつつ、保存範囲をterminalまでに限定する。

### E. Provider call後も既存at-least-once retryを維持する

不採用。post-invocation crashで二重送信し得る。添付requestはsubmission marker後の再送を禁止し、DeliveryUnknownへ収束させる。

### F. `(request_id, attempt_number)`をsubmission identityとする

不採用。ADR 0015のmanual retryでは `attempt_count` が0へ戻り、異なるdispatch cycleで同じ `attempt_number` が再利用される。`mail_attempts`の履歴契約を壊す一意制約は追加せず、attachment submission evidenceをrequest単位で一意とする。

### G. terminal後もmanual retry用にspoolを保持する

不採用。MVPの短期spool責任を長期retentionへ拡張し、cleanup、backup、security、容量契約を複雑化する。添付requestはmanual retryを禁止し、再送は新規requestと再添付で行う。

## Review gates before Accepted

- submission markerがprovider call前にdurableか。
- attachment submission evidenceがrequest単位で一意であり、`(request_id, attempt_number)`一意identityを要求していないか。
- `mail_attempts.attempt_number`がADR 0015どおりdispatch cycle間で重複可能か。
- 添付requestのmanual retryがDelivered、Failed、DeadLettered、Cancelled、DeliveryUnknownのすべてから禁止されているか。
- 添付なしrequestのADR 0015 manual retryが維持されているか。
- Admin manual retryが添付requestへ409／`ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED`を返すか。
- submission marker後にproviderを再呼び出す経路がないか。
- submission marker後にmanual cancelし、provider結果をCancelledで上書きする経路がないか。
- terminal commit後にspoolを削除し、manual retry用に保持しない契約が一貫しているか。
- expired lease／sweepがStartedまたはUnknown submissionを再送しないか。
- DeliveryUnknownがDB、Contracts、Admin、event、cleanupで一意か。
- backup maintenance leaseがAdmin／CLIの両経路とacceptanceに共通か。
- hash projection、0-based order、canonical MIME値がSDK間で一致するか。
- filenameが一般観測面へ漏れず、Admin revealが監査されるか。
- spool commitとDB commit、orphan／missing／staging recoveryが収束するか。
- 16 MiB Consumer capと8 MiB provider policyが実装可能か。

## References

- [Issue #523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- [Issue #525 / PR #529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- [Issue #526 / PR #531](https://github.com/kooiei-in4a/amane-mailer/pull/531)
- [Issue #532 / PR #535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- [Issue #533](https://github.com/kooiei-in4a/amane-mailer/issues/533)
- [ADR 0015](0015-manual-retry-cancel-state-transitions.md)
- [Docker qualification evidence](../cd/reports/2026-08-04-issue-532-docker-memory-qualification.md)
