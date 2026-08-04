# ADR 0022: 添付ファイルの公開契約・検証・配送境界

- **Status:** Draft
- **Date:** 2026-08-04
- **Tracks:** [#523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- **Planning:** [#517](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- **Provider evidence:** [#525](https://github.com/kooiei-in4a/amane-mailer/issues/525) / PR [#529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- **Resource evidence:** [#526](https://github.com/kooiei-in4a/amane-mailer/issues/526) / PR [#531](https://github.com/kooiei-in4a/amane-mailer/pull/531)、[#532](https://github.com/kooiei-in4a/amane-mailer/issues/532) / PR [#535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- **Amends:** [ADR 0012 D-05](0012-mail-via-mailer-microservice.md)（添付対応時の `payload_hash` と数値 `byte_length`）
- **Preserves:** [ADR 0012](0012-mail-via-mailer-microservice.md)（Contracts 正本・認証・冪等性）、[ADR 0013](0013-admin-threat-model-and-pii-policy.md)（PII／到達制限）、[ADR 0019](0019-sqlite-single-process-boundaries.md)（SQLite／単一プロセス）、[ADR 0020](0020-bounce-ingestion-and-suppression.md)（配信結果と抑制）
- **Future work:** [#518](https://github.com/kooiei-in4a/amane-mailer/issues/518)（malware）、[#524](https://github.com/kooiei-in4a/amane-mailer/issues/524)（storage ADR）、[#533](https://github.com/kooiei-in4a/amane-mailer/issues/533)（temp-file spool）、[#534](https://github.com/kooiei-in4a/amane-mailer/issues/534)（automatic retry）

## Context

Amane Mailer v1.3.0 では、Consumer が `POST /internal/mail-requests` に添付を含められる能力を追加する。添付は利用価値が高い一方、次の責任を同時に固定しなければならない。

- Consumer HTTP request と provider request の byte budget
- Base64、decoded binary、digest、length の整合性
- `payload_hash` と個別添付 digest の責任分離
- filename と file type の安全な検証
- provider ごとの transport 差
- 添付本体の保存、processing handoff、retry、ambiguous delivery の扱い
- 実コンテナで成立するメモリ条件
- ログ、DB、Admin、metrics への値漏洩防止

Issue #523 で Koo が確定した MVP 条件と、#525／#526／#532 の実測結果を本 ADR に固定する。

### 確定済みの事業条件

| 項目 | v1.3.0 MVP |
|---|---:|
| 添付数 | 1メール最大5件 |
| 1ファイル | decoded binary 最大2 MiB |
| 添付合計 | decoded binary 最大5 MiB |
| provider送信データ | 件名、本文、宛先、添付を含む最大8 MiB |
| 添付本体の保存 | DB／file storageへ永続保存しない |
| 処理方式 | 送信処理中のみ扱う。production temp-file spoolは採用しない |
| 自動再送 | 添付メールでは行わない |
| malware scan | Mailerでは行わない。Consumer責任 |

### Provider evidence

- SMTP／MIME は attachment `Content-Transfer-Encoding` を Base64 に固定すると、Mailpit で decoded digest、length、filename、Content-Type、order、NFC filename の一致を確認できた。
- ACS は REST／JSON Base64 transport であり、SMTP／MIME の `Content-Transfer-Encoding` 自動選択問題は存在しない。
- ACS request-side では PDF／PNG／TXT、複数attachment、NFC日本語filename、provider acceptance、Event Grid `Delivered` 到達を確認した。
- ACS delivered-side の decoded digest、length、filename、Content-Type、order の受信箱検証は、Koo 決定により #525 の完了条件から descope 済みである。

### Resource evidence

#532／PR #535 では実 Docker／cgroup total-memory limit を使ってACS JSON envelope経路を検証した。

| memory | 結果 |
|---|---|
| 256 MiB | Q00〜Q02の軽量・通常条件はPASS。5 MiB添付＋8 MiB近傍provider envelopeの最大条件はmanaged `OutOfMemoryException` |
| 512 MiB | 最大条件・concurrency 2を3回とも安定PASS。OOM／container killなし |

したがって次を区別する。

- Minimum runnable memory: **256 MiB**
- Minimum memory for full attachment-limit support: **512 MiB**
- Recommended production memory: **512 MiB以上**

256 MiB では最大添付条件と 8 MiB 近傍の provider envelope を保証しない。SMTP／MIME経路とproduction runtimeは実装後に再qualificationする。

### Draft blocker: durable processing handoff

既存契約（ADR 0012 D-05a）は、POSTの `202 Accepted` を「Mailerが依頼をDBへ永続化した」と定義し、その後にWorkerがproviderへ送信する。現行実装は送信に必要なpayloadをDBから再取得できることを前提とする。

一方、本Issueで確定したMVP方針は、attachment binaryとraw Base64をDB／file storageへ永続保存しない。この2つをそのまま組み合わせると、POST完了後またはprocess restart後にWorkerがattachmentを再取得できず、既存のdurable async handoffは成立しない。

したがって、次のprocessing contractは **ADR Accepted前の未解決ブロッカー** とする。実装Issueへ推測させない。

| option | 概要 | 主な影響 |
|---|---|---|
| A. request-lifetime send | attachmentメールだけPOST処理中にprovider outcomeまで進める | ADR 0012 D-05aの202意味論、timeout、Consumer retry、API latencyを改訂する必要がある |
| B. bounded process-local handoff | binaryをprocess memory内のbounded queueへ渡す | crashで喪失するため、202をdurable acceptanceとして返せない。明示的な非耐久契約が必要 |
| C. durable spool/storage | binaryを一時file／DB／object storageへ保存しWorkerが取得する | v1.3.0非目標の#533／#524を前倒しし、retention／cleanup／backup境界を決める必要がある |

本DraftはA〜Cを選択しない。Koo決定と独立レビューを経てprocessing handoffを一意にした後でのみAccepted化できる。

## Decision drivers

1. v1.3.0 の導入を止めず、添付機能を理解しやすい固定上限で提供する。
2. provider 呼び出し前に、件数、size、integrity、type、provider budget を fail-closed で判定する。
3. 添付binaryとraw Base64を永続化せず、retention／purge／backupの新責任を増やさない。
4. 同一 `mail_request_id` で異なるbinaryを同一内容として扱わない。
5. Windows／Linux、ACS／SMTPで filename と binary identity の意味を揃える。
6. attachment content、Base64、private path、PII、secret、provider raw responseを観測面へ出さない。
7. 一時ファイルspool、自動再送、malware scannerをv1.3.0の必須条件へしない。
8. durable acceptanceを装いながらbinaryを喪失する設計を許可しない。

## Decision

### D-01. Public attachment contract

`POST /internal/mail-requests` に `attachments` 配列を追加する。配列順は添付順であり、payload identity の一部とする。

各 attachment DTO は次のフィールドを持つ。

| JSON field | 型 | 必須 | 意味 |
|---|---|---|---|
| `file_name` | string | yes | 利用者が指定した表示名。D-05で正規化・検証する |
| `content_type` | string | yes | Consumer申告値。D-06でextension／structureと照合する |
| `content_base64` | string | yes | decoded binaryのRFC 4648標準Base64。whitespaceを許可しない |
| `content_sha256` | string | yes | decoded binaryのSHA-256。64文字lowercase hex |
| `byte_length` | integer | yes | decoded binary byte数。0以上のJSON integer |

追加契約:

- `attachments` 未指定または空配列は添付なしとして扱う。
- 最大5件。6件以上はproviderを呼ばず422で拒否する。
- inline image、Content-ID、disposition指定、外部URL参照、multipart upload sessionはv1.3.0非目標とする。
- Consumer申告の `content_type`、`content_sha256`、`byte_length` は信用せず、Mailerがdecoded binaryから再計算・再検証する。

### D-02. Byte budgetを3層に分離する

MiB は `1 MiB = 1,048,576 bytes` とする。

| budget | 上限 | byte definition | enforcement |
|---|---:|---|---|
| per-file decoded binary | 2,097,152 | Base64 decode後のbinary byte数 | decode中に上限+1 byteで拒否 |
| total decoded binary | 5,242,880 | 1メール内のdecoded attachment合計 | running totalで拒否 |
| provider envelope | 8,388,608 | 選択providerへ渡すserialized request／message body。transport headerは除外 | provider固有のqualified estimatorまたはexact pre-serializationで呼出前に拒否 |
| Consumer HTTP envelope | 16,777,216 | `POST /internal/mail-requests` のHTTP request body bytes。HTTP headerは除外 | `Content-Length`早期拒否＋capped read |

Consumer HTTP envelope と provider envelope は同じ値にしない。Base64、JSON property、本文、宛先、metadataによりConsumer envelopeの方が大きくなり得る。#532のACS最大受理fixtureでは、Consumer envelope 8,679,497 bytes、ACS provider envelope 8,191,324 bytesだった。

実装は request body 全体を上限なしにbufferしてから判定してはならない。`Content-Length` が存在する場合はread前に拒否し、存在しない／信用できない場合もcapped streamで上限+1 byteまでに制限する。

8 MiB provider policyはACSとSMTP／MIMEの両経路へ適用する。ACSは#532 Evidenceを持つ。SMTP／MIMEはBase64 CTE、header folding、line wrappingを含むserialized message sizeを実装時にoffline exact captureし、release前に同じ境界をqualificationする。

### D-03. Attachment integrity と request identity を分離する

`content_sha256` は個々の decoded attachment binary の identity／integrityを表す。

`payload_hash` はメール送信request全体の冪等性identityを表す。raw Base64はhash対象に含めず、attachmentごとに次のcanonical metadataを配列順で含める。

```text
file_name      … NFC正規化後
content_type   … validation後のcanonical Content-Type
byte_length    … decoded binaryのJSON integer
content_sha256 … lowercase hex
order          … attachments配列位置
```

既存のADR 0012 D-05にある「hash対象JSONに数値型を含めない」という制約は、attachment-enabled payloadについて次の範囲で改訂する。

- `byte_length` と `order` は非負整数としてJCS canonicalization対象に含める。
- 小数、指数表現、浮動小数点は添付canonical metadataに使用しない。
- C#／Python／TypeScriptで同じhash vectorを用意し、同一入力で完全一致させる。

`payload_hash` の対象は、ADR 0012 D-05の既存フィールドに `attachments` canonical metadataを追加したものとする。

### D-04. Decode、integrity、idempotency の順序

初回POST、同一IDの冪等再送のどちらでも、Mailerは次を行う。

1. Consumer HTTP envelope capを適用する。
2. JSON syntax、duplicate property、strict UTF-8を検証する。
3. attachment countを検証する。
4. Base64をbounded decodeしながらper-file／total cap、SHA-256、decoded lengthを計算する。
5. `content_sha256` と `byte_length` を再計算値と照合する。
6. filenameとfile typeを検証し、canonical metadataを確定する。
7. Mailer側で `payload_hash` を再計算する。
8. 同一 `mail_request_id` の既存identityと比較する。
9. provider envelope estimatorを適用する。
10. D-08aで確定するprocessing handoffへ進む。

同一 `mail_request_id` でも、attachment binary、metadata、orderのいずれかが異なる場合は `IDEMPOTENCY_CONFLICT` とする。既存requestがあることを理由にdecode／digest／length検証を省略しない。

### D-05. Filename contract

`file_name` は保存名ではなく表示・provider送信用metadataである。original filenameをpath componentとして使用しない。

検証順序:

1. Unicode NFCへ正規化する。
2. NFC後のUTF-8 byte数が1〜255 bytesであることを確認する。
3. empty、`.`、`..` を拒否する。
4. `/`、`\`、NUL、Unicode control characterを拒否する。
5. trailing dot／spaceを拒否する。
6. Windows予約名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`〜`COM9`、`LPT1`〜`LPT9`。extension付きも同じ）をcase-insensitiveに拒否する。
7. NFC後filenameのcase-insensitive duplicateを同一メール内で拒否する。

添付順は配列位置で保持する。filenameのsortで順序を変更しない。

### D-06. Allowed file types と content validation

v1.3.0で許可する組合せを固定する。

| extension | canonical Content-Type | validation |
|---|---|---|
| `.pdf` | `application/pdf` | `%PDF-` signature、構造parse、暗号化／password protection拒否、最後の`%%EOF`後はASCII whitespaceのみ |
| `.jpg`, `.jpeg` | `image/jpeg` | SOI、marker構造、EOI、EOI後payloadなし |
| `.png` | `image/png` | PNG signature、chunk length／CRC、必須IHDR、終端IEND、IEND後payloadなし |
| `.docx` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | ZIP構造、`[Content_Types].xml`、`word/document.xml`、macro entryなし |
| `.xlsx` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | ZIP構造、`[Content_Types].xml`、`xl/workbook.xml`、macro entryなし |
| `.csv` | `text/csv` | D-07のtext条件＋quote／record構造。業務列schemaは検証しない |
| `.txt` | `text/plain` | D-07のtext条件 |

extension、declared Content-Type、構造検証結果の3つが一致しない場合は拒否する。extensionだけ、またはContent-Typeだけで許可しない。

明示的に拒否するもの:

- executable／installer／script（`.exe`、`.dll`、`.msi`、`.bat`、`.cmd`、`.ps1`、`.sh`、`.js`、`.vbs`、`.scr`等）
- macro-enabled Office（`.docm`、`.xlsm`等）
- legacy binary Office（`.doc`、`.xls`）
- generic archive（`.zip`、`.7z`、`.rar`等）
- encrypted／password-protected PDF
- polyglot、trailing payload、extension／type mismatch

DOCX／XLSXのZIP parserは、zip bomb対策として次を上限とする。

- entry数最大1,024
- 合計uncompressed bytes最大32 MiB
- 1 entry最大16 MiB
- path traversal entry、absolute path、NULを含むentry名を拒否

### D-07. TXT／CSV contract

TXT／CSVはmagic bytesを要求しない。次を満たすことを要求する。

- strict UTF-8。UTF-8 BOMは許可し、canonical contentからは除去して扱う。
- NULを拒否する。
- TAB、CR、LFを除くC0 control characterとDELを拒否する。
- 1行最大64 KiB UTF-8 bytes。
- CSVはdouble-quoteの対応、quoted field内改行、record終端の構造のみを検証する。
- CSV delimiterはcomma固定。encoding推測、Shift_JIS変換、Excel方言の自動補正を行わない。

### D-08. Processing と persistence

v1.3.0 productionでは添付本体を送信処理中のみ扱い、次を永続化しない。

- decoded attachment binary
- raw Base64
- Consumer request bodyの添付部分
- provider raw request／response

添付単位で保存するmetadata:

- NFC正規化済みfile name
- decoded byte size
- validated canonical Content-Type／file type
- attachment order

メール単位で保存するmetadata:

- send status
- fixed failure category
- delivery unknown state／flag
- relevant timestamps

既存 `payload_json` または同等のdurable payloadへ `content_base64` を含めてはならない。attachment metadataだけを持つsanitized persistence projectionを使用する。

#526／#532のtest-only probeが一時ファイルを使用した事実は、production spool方式の承認を意味しない。production temp-file spoolはv1.3.0非目標であり、将来#533で判断する。

### D-08a. Processing handoff はAccepted前に確定する

D-08の非永続化方針とADR 0012のdurable asynchronous workerを両立させるprocessing handoffは未決定である。

- attachment binaryを取得できない状態で既存Workerへ依頼を残してはならない。
- binaryが失われ得るのに、既存と同じdurable `202 accepted` を返してはならない。
- process-local handoffを採る場合も、response時点、crash semantics、backpressure、shutdown drainを一意にする。
- storage／spoolを採る場合は#524／#533を前倒しし、Kooの「v1.3.0では保存しない」決定を明示的に改訂する。

A〜Cの選択とHTTP結果／status state machineへの影響を、Koo決定コメントと本ADR更新で固定するまでproduction実装はHOLDとする。

### D-09. Runtime memory contract

運用文書とCompose例は次を区別して記載する。

| 項目 | 値 |
|---|---:|
| Minimum runnable memory | 256 MiB |
| Minimum memory for full attachment-limit support | 512 MiB |
| Recommended production memory | 512 MiB以上 |

256 MiB構成はMailer runtimeの起動と軽量・通常条件を対象とする。最大5 MiB添付＋8 MiB近傍provider envelopeはサポート保証外とする。

512 MiB以上では、2 MiB/file、5 MiB total、5 files、8 MiB provider envelope、concurrency 2をfull support候補とする。

本ADRのメモリ判断はtest-only ACS probeのEvidenceである。production実装後、実Mailer runtimeを対象に256／512 MiB、concurrency 2、ACS／SMTPの両provider pathをrelease qualificationで再実行する。

### D-10. Provider mapping と provider envelope gate

#### SMTP／MIME

- attachment `Content-Transfer-Encoding` はBase64へ明示的に固定する。
- provider libraryの自動encoding選択へ委ねない。
- filename、Content-Type、orderをvalidated canonical metadataから設定する。
- MIME header、boundary、Base64 line wrappingを含むserialized messageを8 MiB policyに対してprovider呼出前に判定する。

#### ACS

- `EmailAttachment`／REST JSON Base64 transportを使用する。
- SMTP／MIMEの`Content-Transfer-Encoding`概念は適用しない。
- PDF／PNG／TXT、複数attachment、NFC日本語filenameのrequest-side mappingは#525 Evidenceで承認済み。
- delivered-side decoded digest、length、filename、Content-Type、orderの受信箱検証はKoo決定によりdescope済み。将来必要になった場合は別Issueで再評価する。

provider invocation前にprovider固有の保守的estimatorを適用する。

- estimateが8 MiBを超える場合はproviderを呼ばず拒否する。
- estimatorはactual serialized envelopeを過小評価してはならない。
- SDK／MimeKit version、serialization、mappingが変わるPRではexact offline captureとの比較testを更新する。
- estimatorが安全に維持できない場合は、exact pre-serializationまたはより保守的な固定capへ切り替える。

### D-11. Failure と `DELIVERY_UNKNOWN`

validation、size、integrity、type、provider envelope超過はnon-retryable failureであり、providerを呼ばない。

provider呼び出し後に次の状態となり、外部providerへ送信された可能性を否定できない場合は、通常の失敗と分けて `DELIVERY_UNKNOWN` 相当として記録する。

- request body送信後のconnection loss
- provider受理応答のlocal parse／persistence failure
- timeout／cancellation時点でprovider受理の有無を証明できない

添付メールでは通常failure、`DELIVERY_UNKNOWN`のどちらもautomatic retry対象にしない。利用者は必要に応じてファイルを再添付し、新しい送信操作を行う。automatic retryは将来#534で判断する。

### D-12. Fixed failure categories

Contracts／OpenAPI実装では、少なくとも次の固定categoryを定義する。文字列はSCREAMING_SNAKE_CASEとし、provider raw messageを含めない。

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
DELIVERY_UNKNOWN
```

HTTP statusの基本方針:

- validation／size／type: 422
- 同一ID・異identity: 409 `IDEMPOTENCY_CONFLICT`
- auth／allowlist: ADR 0012の401／403を維持
- Mailer一時障害: 503

DBへは固定categoryのみを保存し、attachment content、filename以外のPIIを含むexception、provider raw responseを保存しない。

### D-13. Security、privacy、malware responsibility

Mailerが担当するもの:

- count／size cap
- strict Base64／UTF-8
- digest／length照合
- filename validation
- allowed typeとsignature／structure validation
- encrypted PDF、macro、archive、script拒否
- provider envelope事前判定

Consumerが担当するもの:

- 添付元の信頼性
- malware scan／DLP等の組織ポリシー
- 利用者が添付してよい情報の判断

v1.3.0ではMailerにmalware scannerを導入しない。将来検討は#518で管理し、v1.3.0をblockしない。

画面、一般log、metrics、Issue／PR Evidenceへ次を出さない。

- attachment content
- raw Base64
- Consumer／provider request body
- internal storage path／private path
- credential／secret
- provider raw response
- PIIを含む内部exception
- digest全文

### D-14. User／Admin visibility

利用者と管理者に表示できるもの:

- file name
- file size
- attachment count
- validated file type
- send success／failure／delivery unknown
- size超過、許可形式違反等の理解可能な固定理由

管理者には、必要な場合にPIIを含まないfailure category、発生日時、mail request identityを表示できる。

Adminからattachment binaryをdownloadする機能はv1.3.0非目標とする。

### D-15. Implementation gate と分割

本ADRがDraftの間、許可するのは文書、hash vector案、test fixture案、offline Spikeのみとする。

本ADRは、D-08aのprocessing handoffがKoo決定で一意になり、独立レビューで修正必須の問題がなく、KooがAcceptedへ変更した後にのみproduction implementationを許可する。

Accepted後の推奨順:

1. Contracts／OpenAPI／SDK hash vectors
2. request cap、Base64、digest／length、filename、file type validation
3. D-08aで決定したprocessing handoffと必要なstatus／HTTP semantics
4. attachment metadata DB schema／migration
5. ACS／SMTP provider mapping、provider envelope gate、`DELIVERY_UNKNOWN`
6. Admin／SDK／docs
7. production Docker／Native AOT／provider release qualification

Acceptedだけでlive ACS、release、publish、tagを許可しない。各実装Issueとrelease qualificationの明示許可が必要である。

## Consequences

### Positive

- ConsumerとMailerでattachment identityを一意に共有できる。
- size超過、改ざん、type mismatchをprovider invocation前に拒否できる。
- binaryを保存しないため、v1.3.0でbinary retention／purge／backup責任を追加しない。
- 256 MiBの最低動作と512 MiBの完全サポートを誤解なく区別できる。
- SMTP／ACSのtransport差を隠さず、provider固有の正しいmappingを維持できる。
- durable handoffとの矛盾を実装へ持ち込まず、Accepted前に判断できる。

### Negative

- Base64によりConsumer requestがdecoded binaryより大きくなる。
- 512 MiB未満では公開最大条件を保証できない。
- binaryを保存しないため、既存のdurable asynchronous Workerをそのまま利用できない。
- processing handoffの選択により、HTTP semantics変更または#533前倒しが必要になる可能性がある。
- strict validationにより、構造的に曖昧なファイルや一部の寛容なviewerで開けるファイルを拒否する場合がある。
- DOCX／XLSXの構造validationとPDF parserはNative AOT、license、bounded resourceを満たす実装選定が必要になる。

## Alternatives considered

### A. 添付本体をDBへ保存する

現時点では不採用。DB materializationだけではprovider送信時のpeak memoryが必ず下がるとは限らず、retention、purge、backup、encryption、retry reuseの責任が増える。ただしD-08aでdurable handoffを選ぶ場合は#524で再評価する。

### B. v1.3.0からproduction temp-file spoolを採用する

現時点では不採用。512 MiBでACS現行上限が成立したためmemory理由だけではMVPをblockしない。ただしD-08aでdurable handoffが必要なら#533前倒し候補となる。

### C. 添付メールを通常メールと同じように自動再送する

不採用。binaryを保存しないため同じ内容を安全に再構成できず、ambiguous deliveryでは二重送信リスクがある。将来#534で再評価する。

### D. file typeをextensionまたはContent-Typeだけで判定する

不採用。偽装と誤設定を検出できない。extension、declared Content-Type、structureを照合する。

### E. Consumer HTTP capとprovider capを同じ8 MiBにする

不採用。Base64とJSON overheadにより、業務上許可した5 MiB decoded attachmentを安全に受け付けられない。

## Review gates before Accepted

独立レビューでは、少なくとも次を確認する。

- D-08aのprocessing handoffが既存202／Worker契約と矛盾せず、一意に決定されているか
- binaryを失い得るのにdurable acceptedを返す経路がないか
- 16 MiB Consumer HTTP envelope capが5 MiB decoded／8 MiB provider条件を安全に受け付けるか
- 8 MiB provider policyがACS／SMTPで測定可能か
- `byte_length`／`order`を数値としてJCSへ追加するADR 0012 D-05改訂がSDK間で一意か
- filename 255 UTF-8 bytes、case-insensitive duplicate拒否がWindows／Linux／providerで一貫するか
- PDF／JPEG／PNGのtrailing payload方針が実装可能か
- DOCX／XLSXの1,024 entries／32 MiB total／16 MiB per-entryが安全かつMVP利用を過度に阻害しないか
- fixed failure categoryが既存Contracts命名と衝突しないか
- attachmentメールのautomatic retry禁止と`DELIVERY_UNKNOWN`の状態遷移が一意か
- production実装がtest-only spoolを誤って正本にしないか

## References

- [Issue #523](https://github.com/kooiei-in4a/amane-mailer/issues/523)
- [Issue #525 / PR #529](https://github.com/kooiei-in4a/amane-mailer/pull/529)
- [Issue #526 / PR #531](https://github.com/kooiei-in4a/amane-mailer/pull/531)
- [Issue #532 / PR #535](https://github.com/kooiei-in4a/amane-mailer/pull/535)
- [Docker qualification evidence](../cd/reports/2026-08-04-issue-532-docker-memory-qualification.md)
