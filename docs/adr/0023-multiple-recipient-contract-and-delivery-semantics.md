# ADR 0023: multiple To／CC／BCCの公開契約と配送semantics

- Status: Draft
- Date: 2026-08-05
- Tracks: Issue #519
- Planning: Issue #517
- Design: ../amane-mailer-v1.3-multiple-recipient-design.md
- Amends by proposal: ADR 0012、0013、0014、0015、0020、0022

> 本ADRはDraftであり、Accepted ADRのstatusを変更しない。Accepted化はKoo承認後の別操作とする。

## Context

v1.2.0はto[]最大1件、legacy single recipient、recipient feedbackとrequest statusの分離なし、DeliveryUnknown attachment-onlyという境界である。v1.3.0ではmultiple To／CC／BCC、canonical recipient persistence、recipient feedback、BCC privacyを追加する。

## Draft decisions

### D-01 Recipient contract

to、cc、bccは各0〜10件、全role合計1〜20件。Toが空でもCCまたはBCCがあれば受理可能とする。role内／role間duplicateはaddress_keyでrejectし、Mailerはdedupeやrole変更をしない。

### D-02 Address

ASCII local-part + ASCII domainだけを受理する。IDN、Unicode local-part、IDNA、Punycodeはv1.3で扱わない。address_keyはtrim + full-address lowercase invariantであり、既存suppression keyを変更しない。invalid addressとduplicateはINVALID_REQUEST、limit超過はTOO_MANY_RECIPIENTSとし、raw addressをerrorへ含めない。

### D-03 Canonical persistence

mail_request_recipientsをrecipient dataの唯一のcanonical sourceとする。legacy columnsはphysical shadowだけにする。existing rowはTo ordinal 0へbackfillする。BCC-only requestのshadowは固定sentinelとし、raw BCCを保存しない。

### D-04 Request statusとrecipient feedback

request statusはprovider submission／worker outcome、recipient summaryはrecipient-level feedbackとする。PartialFailureをpublic request statusへ追加せず、late feedbackでrequest statusを変更しない。

Recipient summary stateはNotSent、Pending、Delivered、Bounced、Suppressed、Failed、Unknown。Pendingはprovider受理後のfeedback待ちだけに使用し、pre-provider rejectionではNotSentとする。

### D-05 DeliveryUnknown

providerへ提出または受理された可能性を否定できないrequestをDeliveryUnknownへ収束させる。automatic retry、whole-request manual retry、provider再呼出しを禁止する。

attachment固有のStarted marker、request単位at-most-once、Started-only recovery、terminal後spool cleanup、全terminalからのmanual retry禁止は維持する。

### D-05a Manual retry boundary

ADR 0015のFailed／DeadLettered manual retry境界は維持する。ただしDeliveryUnknownはprovider acceptance uncertaintyを表すため、plain requestでもmanual retry対象外とする。attachment requestの全terminal manual retry禁止はADR 0022の例外を維持する。

### D-06 Provider disposition

internal dispositionはDefinitelyNotSubmitted、Accepted、DefinitelyRejected、UnknownAfterSubmission。SMTPではDATA後またはstage不明のtimeout／network lossをUnknownAfterSubmissionとする。ACSはresponse loss後に最大1回のbounded re-queryを行い、5秒後開始、10秒timeout、確認不能はUnknownAfterSubmissionとする。

### D-06a Plain request submission evidence

添付なしrequestにもrequest単位のdurable submission evidenceを持たせる。NoEvidenceはrowなし、provider呼出し前にStartedをunique request_idでcommitする。Started commit後はstale claim、startup recovery、periodic sweep、manual retryが証拠なしにproviderを再呼出ししてはならない。

evidence stateはStarted、DefinitelyNotSubmitted、Accepted、DefinitelyRejected、Unknownとする。通常応答時はevidence terminal stateとrequest／attempt finalizeを同一transactionで保存する。SMTP DATA後のcrashはStartedからDeliveryUnknownへ収束し、ACSは保存済みoperation IDでbounded re-queryする。

DefinitelyNotSubmittedからの再送だけは、同じunique evidence rowを明示的なretry transitionでStartedへ戻した場合に限り許可する。Started、Accepted、DefinitelyRejected、Unknownからのautomatic／manual retryとprovider再呼出しは許可しない。

018のupgradeでは、新しいevidence rowの不存在をprovider未呼出しの証明として扱わない。旧Worker停止、in-flight provider invocation=0、Processing request=0を満たしたatomic migrationで、provider attempt履歴がないrequestだけをNoEvidence、Deliveredでacceptanceが確定するrequestをAccepted、履歴があるがacceptance／definite rejectionを証明できないrequestをUnknownへbackfillする。Unknownへ分類したrequestはDeliveryUnknownとし、自動／whole-request manual retryを禁止する。分類不能時はmigrationとWorker readinessをfail-closedにする。

Started insertは、`BEGIN IMMEDIATE`相当のtransaction内で、`request.status=Processing`、current claim token、`lock_expires_at > actual now`、plain request、evidence row不存在を条件とするconditional insertとする。affected rowsが0ならrollbackしproviderを呼び出さない。DefinitelyNotSubmitted→Startedも、同じrequestのevidence state、Processing、current claim token、lease未期限切れを条件としたconditional updateに限定する。

terminal finalizeは、expected `evidence_state=Started`、Processing、current claim token、lease未期限切れを条件に、evidence、request、mail_attempt、canonical recipientのPending／NotSentを同じtransactionで更新する。fenced updateが0 rows、または部分更新が失敗した場合は全体をrollbackし、Startedをrecoveryへ残す。lease expiry後のstale WorkerはStarted commit／finalizeを成功できず、reclaim後のWorkerは同じevidenceを読み、Started以上ならproviderを再呼出ししない。

### D-07 Bounce／suppression

BouncedとSuppressedはbounce eventを記録し、canonical address keyをsuppressionへ登録する。Failed、Quarantined、unknown statusは記録のみでsuppressionしない。相関はprovider message ID exact matchからcanonical recipient row照合までtenant scope内で行う。

### D-08 BCC privacy

bcc_recipient_revealを独立capabilityとし、default deny、general operator deny、通常PII capabilityからの流用禁止とする。認証、tenant scope、capability、audit保存、audit成功後serveの順を強制し、audit失敗時はfail-closedとする。

### D-09 Hash

既存single-To hash vectorは不変。cc／bccが省略、null、空配列ならhash documentからroleを省略し、1件以上の場合だけrole名と配列順を含める。

## Consequences

- provider受理とrecipient feedbackをConsumer／Adminが区別できる。
- recipient table migrationが必要になる。
- canonical tableと高権限backupにはraw BCCが残り得るため、通常Admin／export／auditから厳格に分離する。
- provider stage不明時はretryを諦め、duplicate送信防止を優先する。
- plain requestにもsubmission evidenceとcrash／lease recoveryが必要になる。
- v1.2 binaryとのrolling upgradeとsimple rollbackは提供しない。

## Acceptance gate

Koo承認、ADR amendment patchの承認、Contracts／OpenAPI／SDK inventory、migration 016／017／018 designと既存履歴分類、plain requestのcrash／stale-lease recovery、claim／lease fence、SMTP／ACS disposition、BCC capability、NotSent／Pending summary、attachment compatibility、Agent B M-03再レビューを完了してからAccepted化を判断する。
