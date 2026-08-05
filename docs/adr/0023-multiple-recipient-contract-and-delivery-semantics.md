# ADR 0023: multiple To／CC／BCCの公開契約と配送semantics

- **Status:** Accepted
- **Date:** 2026-08-05
- **Accepted:** 2026-08-05（Koo承認。Agent Bの設計レビューは `APPROVE_DESIGN_WITH_GATES`）
- **Decision owner:** Koo
- **Tracks:** [Issue #519](https://github.com/kooiei-in4a/amane-mailer/issues/519)
- **Planning:** [Issue #517](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- **Related PR:** [#539](https://github.com/kooiei-in4a/amane-mailer/pull/539)
- **Related issues:** [#525](https://github.com/kooiei-in4a/amane-mailer/issues/525)、[#530](https://github.com/kooiei-in4a/amane-mailer/issues/530)
- **Amends:** [ADR 0012](0012-mail-via-mailer-microservice.md)、[ADR 0013](0013-admin-threat-model-and-pii-policy.md)、[ADR 0014](0014-admin-session-tenant-throttle-audit-design.md)、[ADR 0015](0015-manual-retry-cancel-state-transitions.md)、[ADR 0020](0020-bounce-ingestion-and-suppression.md)、[ADR 0022](0022-attachment-contract-validation-and-delivery-boundaries.md)
- **Implementation status:** Design decision approved. Production implementation is **NOT YET APPROVED** and was not performed by PR #539.
- **Migration status:** Migration SQL, including migration 018, is **NOT IMPLEMENTED / NOT REVIEWED**.
- **Release status:** Release, publish, and version change are **NOT AUTHORIZED**.

> 本ADRはKooの2026-08-05設計承認を正式な正本へ記録する。Acceptedは設計判断の承認を表すだけであり、production implementation、migration SQL、PR Ready、merge、release、publishを許可しない。

## Context

v1.2.0の公開契約は単一recipientを前提とし、recipient feedbackとrequest-level lifecycleを同じ意味として扱わない設計境界が不足していた。v1.3.0では複数To、CC、BCC、recipient単位の永続化・配送結果・BCC privacyを導入する。ただし、provider submission後の再送でduplicate deliveryを起こさない境界と、添付requestの既存at-most-once契約は維持する。

Kooは2026-08-05に、v1.3.0のCommitted scopeを複数To、CC、BCC、attachmentとして承認した。設計承認後のContracts、runtime、migration、Admin、SDK、qualification実装は分割Issueで進める。

## Decision

### D-01. Public recipient contract

既存の `to[]` 形状を維持し、`cc[]` と `bcc[]` を追加する。roleごとの上限と全体上限は次で固定する。

| role | 件数 |
|---|---:|
| `to` | 0〜10 |
| `cc` | 0〜10 |
| `bcc` | 0〜10 |
| 全role合計 | 1〜20 |

Toが1件以上であることは必須条件ではない。`cc` または `bcc` だけでも、全role合計が1件以上なら受理可能とする。`to`／`cc`／`bcc` の未指定、`null`、空配列は、そのroleの件数0として扱う。全role合計0は `INVALID_REQUEST`、role別または合計上限超過は `TOO_MANY_RECIPIENTS` とする。

role内およびrole間のduplicateは、既存suppression keyと互換の `trim + full-address lowercase invariant` による `address_key` で判定し、受理エラーとする。Mailerはdedupe、role変更、recipient削除、recipient分割を行わない。display nameの違いはduplicate判定を変えない。

addressはv1.3.0ではASCII local-part + ASCII domainだけを受理する。IDN、IDNA、Punycode、Unicode local-partは受理しない。local-partの上限は64 octets、full addressの上限は254 bytesとし、CR/LF、NUL、controlも拒否する。display nameはnullableであり、CR/LF/controlを拒否し、空白だけの値はnullとして扱う。raw addressをerror、一般log、metrics、trace、generic auditへ出してはならない。

各roleの配列順を保持し、`ordinal` はrole内の0-based ordinal（0〜9）とする。providerへのglobal orderはTo、Cc、Bccとする。single-recipient requestは既存 `to[]` の意味、既存suppression key、既存single-To payload hash vectorを維持する。

### D-02. Payload hash

payload hashは受理したrecipient objectのrole、address、display name、配列順を、既存のcanonical JSON規則と実装時の共通vectorに従って反映する。既存single-To vectorを変更しない。

`cc`／`bcc` が未指定、`null`、空配列の場合はhash documentからそのrole propertyを省略する。1件以上の場合だけrole名と配列順を含める。入力の省略と空配列をMailerが別のrecipient意味へ変換してはならない。hash計算の詳細vectorはContracts／OpenAPI／SDK実装時に同一drift gateで比較するが、本ADRの省略・順序・single-recipient互換境界を変更してはならない。

### D-03. Recipient canonical persistence

`mail_request_recipients` をaccepted recipient canonical dataの唯一の正本とする。recipient rowは少なくとも次の意味を持つ。

```text
request_id
recipient_role: To / Cc / Bcc
ordinal: 0-based within role
address: trimmed accepted address
address_key: full lowercase invariant
display_name: nullable
delivery_state: NotSent / Pending / Delivered / Bounced / Suppressed / Failed / Unknown
provider status and recipient correlation identifiers
created_at / updated_at
```

primary identityは `(request_id, recipient_role, ordinal)`、duplicate防止は `(request_id, address_key)` とする。recipient IDを先行導入しない。既存single-To rowはTo ordinal 0へbackfillする。

`mail_requests.recipient_email` と `mail_requests.recipient_display_name` はlegacy binary互換のphysical shadowに限定する。delivery、feedback、suppression precheck、Consumer GET、Admin、exportはlegacy列をcanonical sourceとして読まない。BCC-only requestでshadowが必要な場合もraw BCCをshadowへ保存せず、既存設計の固定redacted sentinelを使う。schema readinessでmatching binaryだけを許可することが主境界であり、sentinelは二次防御である。

### D-04. Provider submission evidence and disposition

plain requestにもrequest単位のdurable provider submission evidenceを導入する。evidence rowは `request_id` で一意とし、provider invocation前に `Started` をdurably commitする。evidenceの意味は次で固定する。

| evidence state / disposition | 意味 | request state | provider再呼び出し |
|---|---|---|---|
| NoEvidence（rowなし） | provider call前であることを7条件から証明できる | Queued / Processingへの初回claim | 許可（初回のみ） |
| Started | provider call開始境界をcommit済み | recovery対象 | 禁止 |
| DefinitelyNotSubmitted | provider未提出を明示的に証明 | Queuedへ戻す制御遷移のみ | 遷移後だけ許可 |
| Accepted | provider acceptanceを確認可能 | Delivered | 禁止 |
| DefinitelyRejected | provider未受理を明示的かつ一意に証明 | Failed | 禁止 |
| Unknown | acceptanceも明示拒否も証明不能 | DeliveryUnknown | 禁止 |

内部provider mappingでは、submission後不明を `UnknownAfterSubmission` と分類できるが、public request stateは `DeliveryUnknown`、recipient stateは `Unknown`、durable evidenceは `Unknown`へ収束させる。timeout、network loss、protocol error、履歴不足をretryableという理由だけでDefinitelyNotSubmittedまたはDefinitelyRejectedにしてはならない。存在しないprovider message ID、operation ID、acceptance resultを推測・生成してはならない。

provider call前のStarted insert、DefinitelyNotSubmittedからStartedへの遷移、terminal finalizeは、current claim tokenと未期限切れleaseを条件とするfenced transactionで行う。affected rowsが0ならproviderを呼ばない。evidence、request、mail_attempt、recipientのPending／NotSent更新は同一transactionで行い、部分更新やfence failureはrollbackする。lease expiry後のstale WorkerはStarted commit／finalizeを成功できず、reclaim後のWorkerもStarted以上のevidenceからproviderを再呼び出ししない。

添付なしrequestと添付requestのevidenceは責任境界を混同しない。添付requestについては[ADR 0022](0022-attachment-contract-validation-and-delivery-boundaries.md)のrequest単位Started markerとat-most-once invocationを維持する。

### D-05. Request aggregate and recipient delivery state

request stateは既存の `Queued`、`Processing`、`Delivered`、`Failed`、`DeadLettered`、`Cancelled`、`DeliveryUnknown` を維持し、`PartialFailure` などの新しいpublic request stateは追加しない。request-level aggregateはprovider submission／worker outcomeの正本であり、recipient stateはrecipient-level feedbackの正本である。request `Delivered` はMailerがprovider acceptanceを確認した意味であり、recipient `Delivered` はrecipient-level delivery feedbackを確認した意味である。

| recipient結果・証拠 | request aggregate | retry | provider再呼び出し |
|---|---|---|---|
| 全recipientがDelivered、evidenceがAccepted | Delivered | 禁止 | 禁止 |
| 一部Delivered・一部明示Failed/Bounced | Delivered（PartialFailure stateは作らない） | whole-request禁止 | 禁止 |
| 全recipientが明示Failed、evidenceがDefinitelyRejected | Failed | whole-request禁止 | 禁止 |
| 一部Unknown | DeliveryUnknown（provider acceptance不明の場合） | 禁止 | 禁止 |
| 全recipientUnknown | DeliveryUnknown | 禁止 | 禁止 |
| 一部Suppressedの事前suppression | Failed、provider invocation 0回 | 禁止 | 禁止 |
| BCCだけがprovider feedbackでFailed/Suppressed | provider acceptance済みならDelivered、BCC rowはそのrecipient state | whole-request禁止 | 禁止 |
| late eventでDelivered/Bounced/Suppressedが混在 | request aggregateは既存provider outcomeを維持 | 禁止 | 禁止 |

provider Accepted後のrecipient feedbackはcanonical recipient rowとevent historyへ保存する。late feedbackでrequest aggregateを別stateへ変更しない。provider acceptanceそのものが不明な場合は、recipient全体または未分類recipientをUnknownとしてrequestをDeliveryUnknownへ収束させる。

legacy classification後の収束も同じ関係に従う。

| legacy classification | evidence | request aggregate | retry／provider再呼び出し |
|---|---|---|---|
| legacy `Accepted` | `Accepted` | `Delivered` | 禁止 |
| legacy `DefinitelyRejected` | `DefinitelyRejected` | `Failed` | 禁止 |
| legacy `NoEvidence` | rowなし。厳格な7条件を満たす場合だけ初回処理可能 | 初回claim前のQueued | 初回provider callだけ許可 |
| legacy `Unknown` | `Unknown` | `DeliveryUnknown` | 禁止 |

### D-06. Migration 018 legacy classification

Migration 018は旧Workerを停止し、in-flight provider invocationとProcessing requestが0件であることをpreconditionとする。安全にdrainできない場合、migrationとWorker readinessを成功扱いにせずfail-closedで停止する。

#### NoEvidenceの厳格な7条件

既存requestをNoEvidenceとして扱えるのは、次の7条件を**すべて**満たす場合だけである。

1. attachmentなしのplain request
2. `request.status = Queued`
3. `attempt_count = 0`
4. `mail_attempts = 0件`
5. provider結果記録なし
6. attachment submission evidenceなし
7. plain submission evidenceなし

plain submission evidence rowが存在しないことだけではNoEvidenceと判定しない。NoEvidenceは保存enum値ではなく、上記条件から初回provider callが未実行と証明できるrowの分類である。

#### Accepted / DefinitelyRejected / Unknown

- `Delivered`であり、provider acceptanceを確認可能な既存証拠があるrequestだけをAccepted evidenceへbackfillする。存在しないprovider情報を推測・生成しない。
- providerがrequestを受理していないことを明示的かつ一意に証明できる場合だけDefinitelyRejectedへbackfillする。単なる `Failed`、例外、timeout、履歴不足をDefinitelyRejectedの根拠にしない。
- 次のいずれかに該当するrequestは原則Unknownへbackfillする: `attempt_count > 0`、`mail_attempts`が1件以上、`Failed`、`DeadLettered`、`Cancelled`、manual retry後に`attempt_count=0`へ戻されたが履歴がある、provider acceptanceまたは明示拒否を証明できない。
- Unknownへ分類したrequestはrequest stateを `DeliveryUnknown` へ収束させ、automatic retry、whole-request manual retry、provider再呼び出しを禁止する。

既存rowを一意に分類できない、backfillが一部しか成功しない、request stateとevidence stateが不整合、classification transactionが完了しない、または分類件数が期待値と一致しない場合、migration 018とWorker readinessをfail-closedにする。既存request、mail_attempts、attempt、delivery event、attachment、bounce、suppressionを分類のために削除・捏造してはならない。

### D-07. Retry、cancel、reschedule

一部recipientへ送信された可能性があるrequest、provider acceptanceが不明なrequest、Unknown／DeliveryUnknown requestはwhole-request manual retry、automatic retry、rescheduleによるprovider再呼び出しを禁止する。legacy Unknownもmanual retry対象にしない。成功済みrecipientへの二重送信を防ぐため、再送が必要な場合は新しい `mail_request_id` による新規requestを基本とする。

ADR 0015のsingle-recipient plain requestとの互換境界として、添付なしrequestの既存manual retryは、provider invocationがなかったことをNoEvidenceとして証明できる場合、または同じevidence rowをDefinitelyNotSubmittedからStartedへfenced transitionできる場合だけ許可する。Started、Accepted、DefinitelyRejected、UnknownからQueuedへ戻してはならない。

添付requestでは、Started marker commit後のprovider invocation最大1回、Started-only recoveryのDeliveryUnknown、terminal durable commit後だけのspool cleanup、全terminal stateからのmanual retry禁止を維持する。cancelまたはrescheduleがprovider再呼び出しを生む経路を作らない。

### D-08. Recipient feedback、event、suppression

delivery eventはrecipient rowに関連付け可能にする。provider message IDだけでcross-tenant correlationをしてはならず、tenant、request、canonical recipientの順にscopeを限定する。recipient-level stateは `Delivered`、`Bounced`、`Suppressed`、`Complaint`、`Unknown` を含む外部結果を必要な範囲で記録し、public summaryのrecipient stateは [D-03](#d-03-recipient-canonical-persistence) の値へ分類する。

duplicate eventはevent identityで冪等に処理し、out-of-order eventはhistoryを保持してcurrent stateの更新条件を固定する。unknown recipient、cross-tenant、cross-source-serviceのeventは他tenantのrecipientへ相関してはならない。provider eventは外部観測結果であり、canonical recipient dataの正本を置き換えない。

ACS `Suppressed` は `Delivered` として扱わない。recipient単位のSuppressed結果として記録し、suppression対象として保存し、将来requestの事前拒否に使う。request全体のstatusは他recipientの結果とのaggregateで決定する。事前suppressionはall-or-nothingとし、一人でも抑制対象ならprovider invocationを行わず、対象recipientをSuppressed、他のrecipientをNotSentとしてrequestをFailedへ収束させる。Issue #530がSuppressedのproduction実装を独立して所有する。ADR 0023とADR 0020は責任境界を記録するだけで、PR #539は実装完了を意味しない。

### D-09. BCC privacy、capability、audit

recipient addressとrecipient display nameはPIIであり、BCCは高機密recipient情報として扱う。通常表示、list、search、generic API、export、metrics、trace、log、screenshot data、support diagnostics、generic auditではBCCをmaskし、raw BCCを返さない。

raw BCC revealは専用 `bcc_recipient_reveal` capabilityを必要とし、default deny、general operator deny、通常のunmasked PII capabilityからの流用禁止とする。Admin authentication、tenant scope、source_service scope、専用capability、durable audit save、audit成功後serveの順序を強制する。scope不明、capability不明、audit保存失敗はfail-closedとする。

auditへraw BCC、raw address、display name、BCC本文、recipient一覧を保存してはならない。auditにはadmin identity、tenant、request、日時、actionなどの最小メタデータだけを保存する。cross-tenant取得とcross-source-service取得を禁止し、responseはno-storeとする。

### D-10. Source of truth

二重正本を作らず、責任を次のとおり固定する。

| データ | 正本 |
|---|---|
| accepted recipient canonical data | `mail_request_recipients` |
| request-level aggregate lifecycle | `mail_requests` |
| provider invocation／acceptance disposition | provider submission evidence |
| dispatch history | `mail_attempts` |
| safe redacted request snapshot | `payload_json` |
| external observation | provider event |
| future request refusal | suppression data |

`payload_json` は安全にredactしたsnapshotであり、raw BCCやattachment contentを一般観測面へ出さない。provider submission evidenceはpayload snapshot、recipient summary、dispatch historyとは別のdurable operational factとする。

### D-11. v1.3.0 scope and issue split

Koo承認済みのv1.3.0 Committed scopeは次である。

- multiple To
- CC
- BCC
- attachment

次はv1.3.0のLater / Non-goalとする。

- recipient単位automatic retry
- attachment automatic retry
- long-term attachment retention
- malware scanning
- Admin attachment download
- external object storage
- tenant CRUD
- tenant別DB

実装IssueはD0（ADR／authority整合）、D1（[#530](https://github.com/kooiei-in4a/amane-mailer/issues/530) Suppressed独立実装）、A（Contracts／OpenAPI／hash／validation）、B（recipient persistence／migration 018）、C（provider disposition）、D（recipient feedback／bounce correlation）、E（BCC capability／Admin／audit）、F（SDK／examples）、G（integration／platform／RC qualification）へ分割する。#530のproduction責任をmultiple-recipient本体へ混ぜず、Issue mutationは本ADR反映では行わない。

## Consequences

- provider acceptance、request aggregate、recipient feedback、retry可否を別々に判定できる。
- 一部送信済みまたは結果不明のwhole-request retryを防ぎ、duplicate deliveryより安全側のUnknownを優先する。
- recipient canonical table、plain submission evidence、migration 018、claim／lease fencingが必要になる。
- BCC raw dataは高権限canonical storageまたはmatching backupに存在し得るため、通常Admin、log、metrics、trace、auditから分離する。
- v1.2 binaryとv1.3 schema／binaryの混在およびsimple rollbackは許可しない。実装時にmatching schema、binary、backupをreadinessで検証する。

## Governance gate

本ADRのAcceptedは設計判断の承認であり、production implementationの承認ではない。production code、Contracts、OpenAPI、SDK、test code、migration SQLは本PRで変更しない。migration 018は設計上の分類条件だけを固定し、SQL実装とreviewは後続Issueで行う。Release／publish／tag、PR Ready化、merge、Issue mutationは本ADRから許可されない。

## References

- [Issue #517: v1.3 planning](https://github.com/kooiei-in4a/amane-mailer/issues/517)
- [Issue #519: multiple To / CC / BCC ADR](https://github.com/kooiei-in4a/amane-mailer/issues/519)
- [Issue #530: ACS Suppressed recipient suppression](https://github.com/kooiei-in4a/amane-mailer/issues/530)
- [PR #539: v1.3 multiple-recipient design](https://github.com/kooiei-in4a/amane-mailer/pull/539)
- [ADR 0012: メール送信マイクロサービス](0012-mail-via-mailer-microservice.md)
- [ADR 0013: 管理画面の脅威モデル・PII取り扱い](0013-admin-threat-model-and-pii-policy.md)
- [ADR 0014: Admin session / tenant scope / audit](0014-admin-session-tenant-throttle-audit-design.md)
- [ADR 0015: manual retry / cancel / state transitions](0015-manual-retry-cancel-state-transitions.md)
- [ADR 0020: bounce ingestion and suppression](0020-bounce-ingestion-and-suppression.md)
- [ADR 0022: attachment contract and delivery boundaries](0022-attachment-contract-validation-and-delivery-boundaries.md)
