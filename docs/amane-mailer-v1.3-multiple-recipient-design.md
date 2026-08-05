# amane-mailer v1.3.0 multiple To／CC／BCC 設計修正版

DESIGN_STATUS: ACCEPTED_DESIGN

- Repository: kooiei-in4a/amane-mailer
- Reviewed develop: df118647d18a578ff44afdd8c60a17931cb488e3
- Baseline tag: v1.2.0 / c173db1d03725e754c4432d02b7c43ceed98c3c0
- Target: v1.3.0 multiple To／CC／BCC
- Scope: accepted design authority and ADR amendments; implementation not authorized

> 本書はAgent BのMajor finding 5件を反映した設計文書であり、Kooが2026-08-05に設計判断を承認した。ACCEPTED_DESIGNはIMPLEMENTATION_READYを意味しない。production code、migration SQL、test code、Contracts、OpenAPI、SDK、releaseは本作業の対象外である。

## 1. 変更した設計判断

### D-01. Address

v1.3.0はASCII local-part + ASCII domainだけを受理する。IDN domain、Unicode local-part、IDNA／Punycodeはrejectする。

~~~text
user@example.com  Accept
user@例え.テスト  Reject
利用者名@example.com  Reject
~~~

address_keyは既存suppression互換の次を維持する。

~~~text
trim + full-address lowercase invariant
~~~

既存mail_suppressionsのre-key migrationは導入しない。invalid address、IDN、Unicode local-part、CR／LF、NUL、controlは既存public error code INVALID_REQUEST（422）で拒否し、error messageへraw addressを含めない。

### D-02. DeliveryUnknown

DeliveryUnknownをattachment-onlyではなく、providerへ提出された、または受理された可能性を否定できないrequest全般のpublic terminal stateとする。

automatic retry、whole-request manual retry、provider再呼出しを禁止する。attachment requestのStarted marker、request単位at-most-once、Started-only recovery、terminal後spool cleanup、全terminal stateからのmanual retry禁止は維持する。

### D-03. Provider disposition

internal dispositionを次の4値に固定する。

| Provider状況 | disposition | request state | recipient state | retry |
|---|---|---|---|---|
| 未提出を証明 | DefinitelyNotSubmitted | Queued／retry | NotSent | 可 |
| 受理を確認 | Accepted | Delivered相当 | Pending | 不可 |
| 未受理の明示拒否 | DefinitelyRejected | Failed | Failed／NotSent | 原則不可 |
| 受理可能性を否定不能 | UnknownAfterSubmission | DeliveryUnknown | Unknown | 不可 |

timeout、network loss、protocol errorをretryableだけで分類しない。stageを証明できない場合はUnknownAfterSubmissionとする。

### D-04. BCC capability

bcc_recipient_revealを独立capabilityとして定義する。default deny、general operator deny、通常PII capabilityからの流用禁止とする。許可tenant内の個別mail request detailだけでallowする。

audit-before-serveは認証、tenant scope、capability確認、audit保存、audit成功後serveの順序とし、audit失敗時はfail-closedとする。

### D-05. NotSent

recipient summary stateへNotSentを追加し、Pendingと区別する。

- NotSent: providerへ提出されていないrecipient
- Pending: providerがrequestを受理し、recipient feedbackを待つrecipient

request state machineへNotSent／Pendingを追加しない。

## 2. Public contract

既存to[]を維持し、cc[]とbcc[]をadditiveに追加する。

~~~text
to[] 0..10
cc[] 0..10
bcc[] 0..10
total 1..20
~~~

Toが0件でもCCまたはBCCが1件以上なら受理可能とする。全role合計が1未満はINVALID_REQUEST、roleまたは合計上限超過はTOO_MANY_RECIPIENTSとする。

role内／role間duplicateはaddress_keyでrejectする。Mailerはdedupe、role移動、recipient削除、recipient分割を行わない。配列順はrole内で保持し、providerへのglobal orderはTo、Cc、Bccとする。

省略、null、空配列はrecipient数0として扱う。payload hashでは省略、null、空配列のcc／bcc propertyを省略する。

## 3. Address validation

受付順序はtrim、control reject、ASCII local/domain確認、ASCII email syntax、length確認、address_key生成とする。

- local-partは64 octets以下
- full addressは254 bytes以下
- display_nameはCR／LF／control reject
- display_nameの空白だけはnull
- raw addressはerror、通常log、metrics、trace、generic auditへ出さない

provider eventのrecipientにも同じASCII-only canonicalizerを適用する。非ASCII eventはunknown／unmatchedとして扱い、suppression登録しない。

## 4. Canonical recipient model

### 4.1 唯一のcanonical source

mail_request_recipientsをrecipient dataの唯一のcanonical read/write sourceとする。

mail_requests.recipient_emailとmail_requests.recipient_display_nameはphysical compatibility shadowに限定する。delivery、feedback、suppression precheck、Consumer GET、Admin、exportはlegacy列を読まない。CIでlegacy read inventoryを検査する。

### 4.2 Recipient row

~~~text
request_id
recipient_type: To / Cc / Bcc
ordinal: 0..9 within role
address: trimmed ASCII address
address_key: full lowercase invariant
display_name: nullable
delivery_state: NotSent / Pending / Delivered / Bounced /
                Suppressed / Failed / Unknown
provider_status
provider_message_id
last_event_id
last_event_at
created_at
updated_at
~~~

primary keyはrequest_id、recipient_type、ordinal。unique keyはrequest_id、address_key。recipient IDを先行導入しない。

Quarantinedはraw provider status／event historyへ保持し、public summaryではFailedに分類する。suppression登録はしない。

### 4.3 Existing row backfill

既存single-To rowはTo ordinal 0へbackfillする。address_keyは現行のtrim + full lowercase invariantを使用し、既存request、attempt、delivery event、bounce、suppression、attachment、submission evidenceを変更・削除しない。

### 4.4 BCC-only legacy shadow

BCC-only requestではlegacy列へraw BCCを保存しない。NOT NULL制約を満たすため固定sentinelを使用する。

~~~text
recipient_email: __bcc_only_redacted__
recipient_display_name: BCC recipients redacted
~~~

sentinelはreal addressではない。primary fenceはschema readinessによるv1.2 binary起動拒否であり、sentinelは二次防御に過ぎない。

canonical tableとinstance backupにはraw BCCが存在し得る。backupはinstance-wide high-privilege operationとし、通常Admin、list、search、export、metrics、trace、logs、generic auditへ返さない。

### 4.5 Plain request submission evidence

添付なしrequestにもrequest単位のdurable submission evidenceを持たせる。attachment requestの既存submission evidenceとは共用せず、plain request専用の境界とする。

~~~text
mail_request_submission_evidence
  request_id: unique primary key
  evidence_state: Started / DefinitelyNotSubmitted / Accepted /
                  DefinitelyRejected / Unknown
  attempt_number
  provider
  provider_message_id: nullable
  provider_operation_id: nullable
  recovery_attempt_count
  last_recovery_at: nullable
  started_at
  terminal_at: nullable
  updated_at
~~~

NoEvidenceはrowが存在しない状態であり、enum値として保存しない。provider呼出し前のStarted commit後は、recoveryが証拠なしにproviderを再呼出ししてはならない。request_id uniqueにより、stale claim、startup recovery、periodic sweep、manual retryが同じ証拠rowを参照する。

## 5. Source of truth／payload snapshot

payload snapshotはsanitized audit snapshotとし、top-level raw BCCとattachment content_base64を含めない。hashはsanitization前のaccepted requestから計算する。BCC canonical dataはrecipient tableだけに保存する。

plain requestのprovider submission evidenceはpayload snapshotやrecipient summaryとは別のdurable operational factである。provider response、provider_message_id、provider_operation_idを通常log、metrics、trace、generic auditへ出さない。

v1.2 binaryとv1.3 schema、v1.3 binaryとv1.2 schemaの混在を禁止する。simple rollbackは禁止する。readinessはbundled migration set、applied set、checksum、required schemaを一致検証し、不一致時はAPI／Workerを処理開始させない。

restoreはmatching binary、schema manifest、SQLite、attachment storage、backup manifestを同一世代として行う。

## 6. Persistence／migration方針

SQLは本作業では実装しない。設計上はmigration 016でrecipient table作成と既存To backfill、017でbounce eventのrecipient correlation metadata追加、018でplain request submission evidence table作成を行う。

016〜018は次を変更・削除しない。

- existing request
- mail_attempts
- delivery events
- bounce event ID／provider event uniqueness
- mail_suppressions
- attachment metadata／spool reference
- submission evidence

018のupgrade preconditionとして、migration開始前に旧Workerを停止し、in-flight provider invocationとProcessing requestを0件にする。Processing requestを安全にdrainできない場合はmigrationを適用せずfail-closedとする。migrationは既存plain requestを、`mail_attempts`を含む履歴と既存request stateから一括分類する。新しいevidence rowがないことだけでは、旧Workerがproviderを呼び出していない証明にならない。

- NoEvidenceとして扱えるのは、attachmentを持たないplain requestで、`request.status=Queued`、`attempt_count=0`、`mail_attempts=0`、`provider_message_id`等のprovider結果記録なし、attachment submission evidenceなし、plain submission evidenceなしをすべて満たすrequestだけである。この条件を満たすrequestだけがv1.3 Workerの初回provider invocation対象になり得る。plain submission evidence rowの不存在だけではNoEvidenceと判定しない。
- Deliveredでprovider acceptanceが既存request stateと履歴から確定できるrequestは、存在するprovider、attempt、provider message ID等だけを保持してAccepted evidenceをbackfillする。存在しない値を推測・生成しない。
- v1.2の既存情報だけでprovider未受理の明示拒否を一意に証明できるrequestに限りDefinitelyRejectedへbackfillできる。provider別の新しいlegacy判定は設けず、証明できないrequestはUnknown evidenceをbackfillする。
- 上記のNoEvidence、Accepted、DefinitelyRejectedのいずれにも一意に分類できないrequestはUnknown evidenceをbackfillし、requestをDeliveryUnknownへ収束させ、自動retryとwhole-request manual retryを禁止する。これには`attempt_count>0`（`mail_attempts=0`でも該当）、`mail_attempts`が1件以上、Failed、DeadLettered、Cancelled、manual retry後に`attempt_count=0`へ戻されたが履歴があるrequestを含む。
- 既存rowを一意に分類できない、backfillが一部しか成功しない、request stateとevidence stateが不整合になる、または分類transactionが完了しない場合は、018とv1.3 Worker readinessを成立させずfail-closedとする。分類済みの既存`mail_attempts`、request、attempt、delivery event、attachment、bounce、suppressionは削除しない。

fresh DBとmigration 015適用済みDBのschema一致、attempt／attachment／evidence保持、plain request evidenceのunique constraint、old binary readiness拒否、matching restoreをqualificationする。Down migrationは提供せず、migration前backupを復旧境界とする。

## 7. Provider mapping

### 7.1 SMTP／Mailpit

To rowsをMime To、Cc rowsをMime Cc、Bcc rowsをSMTP envelopeへ対応付ける。literal Bcc headerをDATAへ出力しない。display nameとrole内順を保持し、provider dedupeへ依存しない。

### 7.2 ACS

EmailRecipients.To、CC、BCCへ対応付け、request単位で一回のSendAsyncを行う。message-level synchronous resultとrecipient-level Event Gridを分離する。deterministic operation IDの再利用拒否は補助安全策であり、Mailerのretry判断を代替しない。

## 8. Provider submission disposition

### 8.1 SMTP／Mailpit table

| 条件 | disposition |
|---|---|
| local validation／construction失敗、session未開始 | DefinitelyNotSubmitted |
| session未確立を証明したconnection failure | DefinitelyNotSubmitted |
| MAIL FROM／RCPT TOの明示拒否、DATA未送信を証明 | DefinitelyRejected |
| DATA受領前のfailureを証明 | DefinitelyNotSubmitted |
| DATA前後を証明できないtimeout／network／protocol error | UnknownAfterSubmission |
| DATA acceptedを2xxで確認 | Accepted |
| DATA accepted後disconnect、2xx受領済み | Accepted |
| DATA送信後response loss、2xx未確認 | UnknownAfterSubmission |

timeoutを一律retryableとしない。

### 8.2 ACS table

| 条件 | disposition |
|---|---|
| local validation／construction失敗 | DefinitelyNotSubmitted |
| ACS明示拒否、未受理を確認 | DefinitelyRejected |
| SendAsync success | Accepted |
| response loss | bounded re-query |
| re-queryでSucceeded | Accepted |
| re-queryで明示失敗 | DefinitelyRejected |
| re-queryで確認不能 | UnknownAfterSubmission |

re-queryはresponse lossごとに最大1回、5秒後に開始、provider API timeout 10秒で停止する。確認不能ならDeliveryUnknownへ収束し、provider再呼出しをしない。

### 8.3 Durable evidence boundary

provider呼出し前に、request単位でevidence_state=Startedとprovider-specific operation identityをdurably commitする。Started commitに成功しなければproviderを呼出してはならない。

通常応答が戻った場合は、evidence terminal stateとrequest／attempt finalizeを同一DB transactionで保存する。processがそのtransaction前にcrashした場合、DBに残るStartedをrecoveryが処理する。

この境界はclaim／lease fencingを含む。実装はDBの書込み時刻を評価する`BEGIN IMMEDIATE`相当のtransactionで、現在のclaim tokenを持つWorkerだけが次を実行できるようにする。

- 初回のNoEvidence→Startedは、`request.status=Processing`、`lock_token=current claim token`、`lock_expires_at IS NOT NULL`、`lock_expires_at > actual now`、plain request、evidence row不存在を同一transaction内で条件にしたinsertとする。条件付きinsertのaffected rowsが0ならtransactionをrollbackし、providerを呼び出さない。cancelがStarted insertより先に確定した場合も同じ境界でprovider invocationは0回となる。
- DefinitelyNotSubmitted→Startedは、同じunique evidence rowに対し、`evidence_state=DefinitelyNotSubmitted`、`request.status=Processing`、current claim token、lease未期限切れを条件にしたconditional updateとする。affected rowsが0ならproviderを呼び出さない。
- terminal finalizeは、evidence state、request state、mail_attempt、canonical recipientのPending／NotSentを同一transactionで更新し、`request.status=Processing`、current claim token、`lock_expires_at > actual now`、expected `evidence_state=Started`を必須条件とする。いずれかのfenced updateが0 rows、または同一transaction内の書込みが失敗した場合は全体をrollbackし、Started evidenceを後続recoveryへ残す。
- lease expiry後のstale WorkerはStarted commitもterminal finalizeも成功できない。reclaim後のWorkerは新しいclaim tokenでevidenceを先に読み、Started以上ならproviderを再呼び出ししない。

したがって、provider呼出し開始前の証拠作成、provider呼出し、finalize、stale claim／startup／periodic recovery、manual retryは、同じrequest単位evidenceとclaim／lease fenceを参照する。provider呼出し後にfinalizeが失敗しても、証拠なしの再送には遷移しない。

| evidence state | SMTP recovery | ACS recovery | provider再呼出し |
|---|---|---|---|
| NoEvidence（rowなし） | 初回処理可能 | 初回処理可能 | 可 |
| Started | DeliveryUnknownへ収束 | deterministic operation IDでbounded re-query | 不可 |
| DefinitelyNotSubmitted | controlled retryだけ可 | controlled retryだけ可 | 状態遷移後だけ可 |
| Accepted | request terminalへ収束 | request terminalへ収束 | 不可 |
| DefinitelyRejected | Failedへ収束 | Failedへ収束 | 不可 |
| Unknown | DeliveryUnknownへ収束 | DeliveryUnknownへ収束 | 不可 |

SMTP DATA受理後、request finalize前にcrashした場合はAcceptedを推測せずStartedからDeliveryUnknownへ収束する。ACS受理後のcrashはStartedに保存済みのdeterministic operation IDでbounded re-queryし、SucceededならAccepted、明示失敗ならDefinitelyRejected、確認不能ならUnknownとする。

## 9. Request／recipient state

request stateは既存のQueued、Processing、Delivered、Failed、DeadLettered、Cancelled、DeliveryUnknownを維持する。PartialFailureを追加しない。

request DeliveredはMailerがprovider acceptanceを確認した意味、recipient Deliveredはrecipient-level delivery feedbackを確認した意味と明示する。

### 9.1 Recovery state machine

~~~text
NoEvidence（rowなし）
  → provider呼出し前にStarted commit
  → provider call

Started
  → normal responseをterminal evidenceへtransactional finalize
  → stale claim／startup／periodic sweepでは再呼出しせずrecovery

DefinitelyNotSubmitted
  → retry policyが許可する場合だけ同じevidence rowをStartedへ遷移

Accepted／DefinitelyRejected／Unknown
  → terminal evidenceとしてrequest stateへ収束
  → recovery、manual retry、sweepからprovider再呼出しなし
~~~

stale ProcessingをreclaimするWorker、startup recovery、periodic sweep、manual retryはすべてこのevidence stateを先に読む。evidence rowがない場合だけprovider未呼出しと判断できる。Started以上ならproviderを呼出さず、SMTPはDeliveryUnknown、ACSはre-queryへ進む。

| provider状況 | disposition | request state | recipient state | retry |
|---|---|---|---|---|
| 未提出と証明 | DefinitelyNotSubmitted | Queued／retry | NotSent | 可 |
| 明示拒否 | DefinitelyRejected | Failed | Failed／NotSent | 不可 |
| provider受理 | Accepted | Delivered相当 | Pending | 不可 |
| submission後不明 | UnknownAfterSubmission | DeliveryUnknown | Unknown | 不可 |
| 事前suppression | provider未呼出し | Failed | Suppressed／NotSent | 不可 |
| late mixed feedback | request変更なし | request変更なし | Delivered／Bounced等 | 不可 |

recipient summaryはpublic additive DTOとし、stateはNotSent、Pending、Delivered、Bounced、Suppressed、Failed、Unknownとする。

~~~json
{
  "recipient_delivery_summary": {
    "total": 3,
    "not_sent": 2,
    "pending": 0,
    "delivered": 0,
    "failed": 0,
    "bounced": 0,
    "suppressed": 1,
    "unknown": 0,
    "classification": "all_failed"
  }
}
~~~

事前suppression例:

~~~text
A: Suppressed
B: NotSent
C: NotSent
request: Failed
provider invocation: 0
~~~

Pendingとして表示してはならない。Consumer GET、Admin detail、summary countで同じ意味を返す。

## 10. DeliveryUnknown／retry／cancel

provider submission後に受理可否を確定できないrequestはDeliveryUnknownへ収束する。automatic retry、whole-request manual retry、cancel、rescheduleによるprovider再呼出しを禁止する。

attachment requestはStarted marker commit後、provider invocation最大1回、Started-only recovery DeliveryUnknown、terminal commit後だけspool cleanup、全terminal stateからmanual retry禁止を維持する。

添付なしrequestはprovider未提出を明確に証明できる場合だけDefinitelyNotSubmittedとしてretryできる。Failed／DeadLetteredのmanual retryも、evidenceがNoEvidenceまたはDefinitelyNotSubmittedの場合だけ許可する。Started、Accepted、DefinitelyRejected、UnknownからQueuedへ戻してはならない。

provider未提出を証明できないままstale Processing、startup recovery、periodic sweepへ入った場合は、plain requestでもDeliveryUnknownへ収束する。evidenceなしの再処理は、Started commitより前にprovider invocationがなかったことがtransaction境界で保証される場合だけ許可する。

## 11. Bounce／suppression

| status | bounce event | suppression | recipient |
|---|---|---|---|
| Bounced | 記録 | 登録 | Bounced |
| Suppressed | 記録 | 登録 | Suppressed |
| Failed | 記録 | 登録しない | Failed |
| Quarantined | 記録 | 登録しない | Failed（raw status保持） |
| unknown | 記録またはUnknown | 登録しない | Unknown |

相関順はprovider_message_id exact match、request特定、event recipient canonicalize、request配下のunique address_key照合、recipient／event／suppression保存とする。

duplicate eventはevent ID unique conflictとしてfinalizeする。out-of-order eventはhistoryを保持し、occurred_atが新しいものだけcurrent stateを更新する。unknown recipient、cross-tenant、非ASCII eventはsuppressionせず、raw addressをgeneric logへ出さない。

既存suppression normalizerと既存keyは変更しない。

## 12. BCC capability／Admin

capability registryへbcc_recipient_revealを追加する。view_unmasked_list_piiだけではBCCをrevealできない。

list、search、generic API、CSV／generic export、metrics、trace、logs、screenshot data、support diagnostics、generic auditではraw BCCを返さず、件数だけを表示する。

audit-before-serve:

1. Admin authentication
2. tenant scope
3. bcc_recipient_reveal capability
4. audit event durable save
5. audit成功後だけraw BCC serve

audit失敗、scope不明、capability不明はfail-closed。auditにはadmin ID、tenant ID、request ID、日時、actionだけを記録し、raw address、display name、BCC一覧、countを記録しない。responseはno-storeとする。

## 13. Contracts／OpenAPI／SDK／AOT inventory

本作業ではContracts、OpenAPI、SDK、production codeを変更しない。実装時に同一drift gateで次を更新・比較する。

- MailRequestCreateRequest、MailRecipientDto、to／cc／bcc nullable／omitted semantics
- limits、total minimum、duplicate、ASCII-only validation
- recipient summary DTO、NotSent、Pending
- generalized DeliveryUnknown
- internal-only provider disposition
- INVALID_REQUEST、TOO_MANY_RECIPIENTSとsanitized examples
- JSON source generation context
- OpenAPI info.version 1.3.0、schema、examples、Consumer GET
- Python SDK、TypeScript SDK、.NET Contracts、Go example
- single-To hash vector、multiple-role hash vector
- IDN／Unicode local reject、duplicate、BCC redaction vector

cc／bccが省略、null、空配列の場合はhash documentからroleを省略する。1件以上の場合だけrole名と配列順を含める。source-generated JSON contextでreflectionなしにAOT serialize／deserializeする。

## 14. Testing／qualification計画

実装は行わない。実装時に次をqualificationする。

### Address／contract

ASCII accept、IDN reject、Unicode local reject、raw address非表示、既存suppression lookup、0〜10／total 1〜20、role内外duplicate、To omitted／CC-only／BCC-only。

### Provider disposition

SMTP connect前、RCPT拒否、DATA前、DATA accepted、DATA後response loss、accepted後disconnect、stage不明timeout、ACS拒否、success、re-query success／failure、plain requestのprocess crash、stale Processing reclaim、startup recovery、periodic sweep、unknown後再呼出しなしに加え、Started insert前のcancel、lease expiry中のStarted insert、stale／reclaimed Workerの競合、二重Started遷移、claim lost時のfinalize、partial finalize failure、fence failure時のprovider invocation 0回をqualificationする。

### Recipient summary

pre-provider suppression、Suppressed + NotSent、all NotSent、Accepted後Pending、Delivered／Bounced混在、Delivered／Suppressed混在、duplicate／out-of-order／unknown event、cross-tenant拒否。

### BCC

general operator deny、normal PII deny、dedicated capability allow、tenant scope、audit success／failure、raw BCCのaudit／log／export非漏洩、BCC-only shadow／backup非露出。

### Migration／recovery

v1.2 DB、migration 015 DB、single row backfill、attempt／attachment／plain submission evidence／bounce／suppression保持、migration 018 precondition、既存request分類をqualificationする。最低限、Queued + `attempt_count=0` + `mail_attempts`なしはNoEvidence、Queued + `attempt_count>0` + `mail_attempts`なしはUnknown、Failed + `attempt_count>0` + `mail_attempts`なしはUnknown、DeadLettered + `attempt_count>0` + `mail_attempts`なしはUnknown、Cancelled + `attempt_count>0` + `mail_attempts`なしはUnknown、`attempt_count=0` + `mail_attempts`ありはUnknown、DeliveredはAccepted、分類不能rowはmigration／readiness FAILとする。あわせてmigration後の自動／manual retry禁止、fresh／upgrade一致、old binary拒否、rollback禁止、matching restoreを確認する。

### Platform／RC

Windows、Linux、Docker、Native AOT、512 MiB、OpenAPI drift、Contracts drift、SDK parity、Mailpit wire、ACS To／CC／BCC matrixをGが所有する。

## 15. Issue split

~~~text
D0. ADR／authority整合
  ↓
D1. #530 Suppressed production対応
  ↓
A. Contracts／OpenAPI／hash／validation
  ↓
B. recipient persistence／migration
  ↓
C. provider disposition／provider mapping
  ↓
D. recipient feedback／bounce correlation
  ↓
E. BCC capability／Admin／audit
  ↓
F. SDK／examples
  ↓
G. integration／platform／RC qualification
~~~

D1はmultiple-recipient本体へ混ぜない。実装順はD0完了後にD1、D1完了後にAとする。D1の実装準備とAの設計inventory作成は並列可能だが、Aのproduction implementationはD1完了後に開始する。FはA確定後に一部並列可能。A前にB、B前にC／D、B／D前にE、全項目前にGを開始してはならない。

Bはmigration 016／017に加えてplain request submission evidence用の018、unique request_id、既存履歴のatomic分類、NoEvidence／Started／terminal evidence、upgrade precondition、claim／lease fenceを所有する。Cはprovider adapterのnormal response分類とfenced evidence transaction finalizeを所有し、Dはstartup／periodic sweepのrecipient／request収束を同じevidenceとfenceで所有する。

## 16. Dependency graph

~~~text
#513
  ↓
D0: #519 Accepted ADR + formal amendments
  ├─→ D1: #530
  └─→ A: Contracts／hash／validation
          ↓
        B: persistence／migration
          ↓
        C: provider disposition／mapping
          ↓
        D: feedback／bounce correlation
          └─→ E: BCC capability／Admin／audit
A ───────────────→ F: SDK／examples
B〜F + attachment baseline
  ↓
G: integration／platform／RC
~~~

## 17. Major finding対応

1. IDN／suppression mismatch: ASCII-only reject、既存key維持、re-keyなしで解消。
2. DeliveryUnknown authority conflict: ADR 0012／0022／ADR 0023／Contracts／OpenAPI／Consumer GET／Adminの実装時に一致させるauthorityを固定。
3. Provider retry ambiguity: SMTP／ACSのstage table、4 disposition、request単位durable evidence、既存履歴のmigration分類、claim／lease fence、crash／lease recovery、bounded re-query、unknown時no reinvokeを固定。
4. BCC capability未定義: registry、explicit grant、scope、audit-before-serve、fail-closedをAccepted ADRへ反映。
5. Pending／NotSent ambiguity: NotSentをpublic summary stateとして追加し、事前suppressionではSuppressed／NotSentを返す。

## 18. Residual risks

- provider adapterがstage evidenceを誤分類しないため、実装時fault injectionが必要。
- canonical tableとinstance backupにはraw BCCが存在し得る。通常operatorへの返却禁止とhigh-privilege auditを維持する。
- v1.2 binary readiness fenceをstartup、Worker、restore経路すべてで検証する。
- provider未知statusはUnknownへ寄せ、自動suppressionしない。
- IDN／SMTPUTF8はv1.3非対応であり、将来導入には別ADRが必要。

設計状態は、Kooの2026-08-05承認とADR 0023／関連ADR amendmentの正式反映を含むACCEPTED_DESIGNとする。これはIMPLEMENTATION_READY、production implementation、migration SQL、release承認を意味しない。

## 19. Implementation hold conditions

1. production implementation開始前に、#519のAccepted ADRと関連amendmentを正本として参照すること。
2. ADR 0012／0013／0014／0015／0020／0022のamendmentとADR 0023が整合すること。
3. ASCII-only、既存suppression key、INVALID_REQUESTがContracts／OpenAPI／SDK／vectorsへ反映されること。
4. DeliveryUnknown一般化とattachment固有制約が全public surfaceで一致すること。
5. provider disposition、request-level durable evidence、既存provider履歴のmigration分類、claim／lease fence、stage recovery、ACS bounded re-queryが確定し、M-03限定再レビューで承認されること。
6. bcc_recipient_revealのgrant、tenant scope、audit-before-serve、fail-closedが確定すること。
7. NotSent／Pendingの意味がConsumer GET、Admin、summaryで一致すること。
8. fresh／upgrade DB、old binary拒否、restore、attachment／bounce／suppression保持のqualification計画が承認されること。

## 20. Traceability

| Decision | Authority | Design section |
|---|---|---|
| ASCII-only、既存suppression key | Koo D-01 | 1、3、11 |
| DeliveryUnknown一般化 | Koo D-02 | 1、9、10 |
| provider disposition | Koo D-03 | 1、8 |
| BCC capability | Koo D-04 | 1、12 |
| NotSent | Koo D-05 | 1、9 |
| limits／duplicate／role order | #519 | 2、4 |
| canonical recipient table | #519／ADR 0023 | 4、5、6 |
| Bounced／Suppressed | #519／#530 | 11 |
| attachment at-most-once | ADR 0022／PR #537／#538 | 1、10、14 |
| plain submission evidence／recovery | Agent B M-03／ADR 0012 D-07／ADR 0015 | 4.5、6、8.3、9.1、10 |

## 21. Agent B再レビュー依頼プロンプト

今回の再レビュー範囲は、前回のMajor finding 5件が解消されたかに限定してください。

1. IDN／Unicode local-partをrejectし、既存suppression keyを変更していないか。
2. DeliveryUnknownがattachment-onlyのまま残らず、通常requestへ一般化され、attachment固有Started／at-most-onceが維持されているか。
3. provider dispositionとSMTP／ACS stage判定がpost-submission再送を防げるか。
4. bcc_recipient_revealのdefault deny、explicit grant、tenant scope、audit-before-serve、fail-closed、raw BCC非記録がauthorityになっているか。
5. NotSentとPendingが定義され、事前suppression時にConsumer GET／Admin／summaryでPendingを表示しないか。
6. migration 018が既存`mail_attempts`履歴を分類し、NoEvidenceと証明できないrequestをUnknown／DeliveryUnknownへ収束させるか。
7. Started insert、DefinitelyNotSubmitted→Started、terminal finalizeがclaim／lease fenceとaffected rows検査を持ち、競合時にprovider再呼出しと部分更新を防ぐか。

設計、Accepted ADR、Contracts／OpenAPI inventoryだけをレビューし、production code、migration SQL、test codeの実装レビューは行わないこと。判定はAPPROVE_DESIGN_WITH_GATES、CHANGES_REQUIRED、HOLDのいずれかとする。

## 22. Operation result

Production code implementation: NOT PERFORMED

Migration implementation: NOT PERFORMED

Test implementation: NOT PERFORMED

Issue mutation: NOT PERFORMED

ADR acceptance: RECORDED (design approval only)

PR Ready／merge: NOT PERFORMED

Release／publish: NOT PERFORMED
