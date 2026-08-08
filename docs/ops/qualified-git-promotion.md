# Qualification 済み Git source の main promotion

Issue [#504](https://github.com/kooiei-in4a/amane-mailer/issues/504) の正規手順です。
qualification 済み `releaseCommitSha` を変更せず、PR の merge commit と annotated
tag によって main へ昇格します。main ruleset を release の都度編集してはいけません。

この手順は Git / PR / merge parent / tag target の integrity だけを扱います。OCI の
digest-preserving promotion は #505 と
[`promote-qualified-oci.yml`](../../.github/workflows/promote-qualified-oci.yml)
の責務です。本手順は OCI、NuGet、GitHub Release、deployment を実行しません。

## 採用方式

採用方式は、repository 専用 GitHub App
`amane-mailer-release-promotion[bot]` と
[`promote-qualified-git.yml`](../../.github/workflows/promote-qualified-git.yml)
の組合せだけです。

- App installation は `kooiei-in4a/amane-mailer` だけを対象にする。
- App repository permission は `Contents: Read and write` だけにする。merge API と
  annotated tag API に必要な最小 permission であり、Administration、Actions、
  Workflows、Packages、Secrets は付与しない。
- main ruleset の bypass actor はこの App 1件だけとし、mode は
  **For pull requests only** (`pull_request`) とする。
- 通常 user、repository role、GitHub Actions App、PAT、deploy key には bypass を付けない。
- App private key は既存 `release` environment の secret とし、required reviewer の承認後、
  対象 repository / `contents:write` に縮小した1時間未満の installation tokenへ変換する。
- workflow は required checks をすべて `success` と再検証してから App tokenをmerge / tagの
  mutation APIに使用する。App の bypass 能力を required-check skip の代用にしない。
- App tokenはimmutable handoff artifactの存在とidentityを通常`GITHUB_TOKEN`で検証した後にだけ
  発行する。ruleset APIの`bypass_actors`はwrite accessを持つcallerにだけ返るため、以降の
  ruleset取得にも同じrepository-scoped App tokenを使う。checkoutには永続化しない。
- merge API には exact PR head SHA と `merge_method=merge` を同時指定する。
- annotated tag object は必ず `releaseCommitSha` を指す。merge commit を tag target にしない。

単なる admin bypass、通常 user の bypass、GitHub Actions App 全体の bypass、required
signatures / required checks の一時 OFF、ruleset の disable / restore は禁止です。

## 一回限りの恒久設定

設定変更自体は #504 導入時の一回だけです。設定完了後の fingerprint を baseline とし、
rehearsal と release 中は main / rehearsal ruleset の値を一切変更しません。

1. personal account 所有の private GitHub App を上記名称で作成する。Webhook は無効、
   install scope は `Only on this account`、repository は `amane-mailer` だけにする。
2. App private keyを生成し、PEM bytesを表示・log出力せず
   `release` environment secret `RELEASE_PROMOTION_APP_PRIVATE_KEY` に保存する。
3. `release` environment に次の variables を保存する。

   - `RELEASE_PROMOTION_APP_ID`
   - `RELEASE_PROMOTION_APP_SLUG`
   - `RELEASE_PROMOTION_MAIN_RULESET_ID` (`18124512`)
   - `RELEASE_PROMOTION_REHEARSAL_RULESET_ID`
   - `RELEASE_PROMOTION_MAIN_RULESET_FINGERPRINT`
   - `RELEASE_PROMOTION_REHEARSAL_RULESET_FINGERPRINT`
   - `RELEASE_PROMOTION_POLICY_FINGERPRINT`

4. `main protection` の既存 rules / conditions / enforcement を保持したまま、bypass listへ
   Appを `pull_request` modeで1件追加する。required signatures、8 required checks、PR rule、
   non-fast-forward、deletion ruleを削除・緩和しない。
5. `release-rehearsal/**` を対象とする active branch ruleset を作る。rules、enforcement、
   bypass actorは main と同一にし、ref conditionだけを rehearsal namespaceへ変える。
6. validation branch `release-rehearsal/504-main-equivalent` を main baselineから作る。
   default CodeQL setupはprotected branch向けPRを解析し、CI workflowはこのnamespaceを
   main向けrelease-gateと同じ条件で処理する。
7. 両rulesetをAPIから再取得し、次のscriptで設定fingerprintとpolicy fingerprintを計算する。
   `policyFingerprint`がmainとrehearsalで一致しない場合はrehearsalを開始しない。

```bash
python3 scripts/ruleset-fingerprint.py \
  --ruleset ruleset.json \
  --effective-rules effective-rules.json \
  --output fingerprint.json
```

fingerprint対象はruleset ID / name / target / source / enforcement / ref conditions / rules /
bypass actors / effective rulesです。timestamp、node ID、URLは除外します。App actor ID、
`pull_request` bypass mode、required signatures、required checksは意味のある入力として含まれます。

## Promotion preflight

workflow は次を operator input と immutable handoff / live GitHub state の間で照合します。

- `releaseVersion`
- `releaseCommitSha`
- `releaseBranch`
- `candidateRunId`
- `candidateAttempt`
- `ociIndexDigest`
- `candidateId`
- `bindingId`
- `qualificationRunId`
- `qualification_workflow_run_attempt` (producer Actions run attempt)
- `sealedEventId`
- `machineVerdict`
- `humanDecision`
- `promotionPrNumber`
- `promotionBaseSha`
- `expectedRulesetFingerprint`
- `tagName`
- `mergeFreezeConfirmation`

production入力はartifactを一つに混ぜません。既存の #455 `setup-release-candidate-handoff`
（`candidate-provenance.json`、`image-identity.json`、archives）を
`candidate_run_id` / `candidate_artifact_id` / `candidate_artifact_name`でそのまま読み取り、
別入力のsealed qualification artifactからのみ`binding.json`、`decision/go-no-go.json`、
`run-status-events/*.json`を読み取ります。候補artifactにsealed JSONを後付けしたり、candidateを
再生成して互換性を作ることは禁止です。現行v1.3.0 artifact `9004008439`は候補handoffであり、
sealed qualification artifactの代替ではありません。

`humanDecision=APPROVE` は exact-candidate qualification の判定であり、release execution
approvalではありません。workflowの`release` environment承認が別のexecution gateです。

[`validate-qualified-git-promotion.py`](../../scripts/validate-qualified-git-promotion.py)
は次をfail-closedで要求します。

- handoffのbinding / decision / sealed eventが同じcandidate run / attempt / candidate / binding /
  qualification run / source SHA / release versionを持つ。
- candidate provenanceのproducer workflow path/ref、run ID/attempt、release SHA、release version、
  OCI digestが固定値と一致し、image identityも同じSHA/version/digestを持つ。
- qualification artifactのActions runはrelease environmentで設定したtrusted repository、workflow ID/path、
  event、head branch/SHA、run attemptとAPI上で一致する。設定値がない、run/artifactが別producer、
  または期待SHA/branch/eventと違う場合はFAILとする。
- `machineVerdict=GO_ELIGIBLE`、`humanDecision=APPROVE`、`runSealed=true`、terminal eventが
  `sealed`かつ入力`sealedEventId`と一致する。
- live RC tipとpromotion PR headがどちらも`releaseCommitSha`と完全一致する。
- PR head refが`releaseBranch`、PR baseとlive base tipが固定`promotionBaseSha`と一致する。
- rulesetがactive、required signatures有効、required checksがすべてexpected GitHub Actions
  integrationから`success`である。
- bypass listが専用App 1件 / `Integration` / `pull_request`だけである。
  これにより通常user、repository role、GitHub Actions Appはbypass actorにならない。
- main / rehearsalのpolicy fingerprintが一致し、target config fingerprintが事前承認値と一致する。
- repositoryでmerge commitが許可され、選択methodが`merge`である。
- release modeではbase=`main`、tag=`v<releaseVersion>`。rehearsal modeではbranch / tagが
  専用namespaceに限定される。

unapproved、missing、duplicate、drift、mismatchはすべて非ゼロ終了です。validatorはfield名と
分類だけをerrorへ出し、入力JSON全体、token、private keyを出力しません。

## Main-equivalent rehearsal

production `main`、`release/v*`、`v*` tagは変更しません。

1. `release-rehearsal/504-main-equivalent`のtipと、main/rehearsal両rulesetのbaseline
   fingerprintを記録する。
2. immutable RC branchからvalidation branchへのPRを作る。PR headがexact
   `releaseCommitSha`であることを確認する。source branchへcommit、merge、rebase、
   force-pushしない。
3. required CI / CodeQL checksがすべてgreenになるまで待つ。
4. 下記negative fixtureを先に実行する。期待FAILが1件でもPASSした場合はSTOPする。
5. main上のworkflowを`mode=rehearsal`でdispatchする。synthetic qualification handoffは
   workflow内でartifact化され、production evidence / branchを変更しない。
6. `release` environmentで、対象がrehearsal PR / branch / tag namespaceであることを確認して
   承認する。
7. workflow evidenceで次をmachine-checkする。

   - merge parent 0 = rehearsal直前base SHA
   - merge parent 1 = exact `releaseCommitSha`
   - RC tip = exact `releaseCommitSha`（不変）
   - synthetic annotated tag target = exact `releaseCommitSha`
   - ruleset fingerprint before = pre-merge = after
   - normal actor bypass = `never`

rehearsal tagは検証後workflowが削除します。validation branch、ruleset、PRは将来rehearsalと
監査のため保持します。削除する場合もrehearsal専用resourceだけを対象にします。

## Negative fixtures

local / CI self-test:

```bash
python3 scripts/validate-qualified-git-promotion-self-test.py
```

必須fixture:

- N1: `machineVerdict=NO_GO` または `humanDecision!=APPROVE` -> FAIL
- N2: promotion PR head != `releaseCommitSha` -> FAIL
- N3a: rulesetでrequired signaturesが無効 -> validator FAIL
- N3b: validation branchに対するunsigned fixture PRを通常actorでmerge -> GitHub ruleset FAIL

追加fixture:

- N4: RC tip drift -> FAIL
- N5: `qualificationRunId` mismatch -> FAIL
- N6: ruleset fingerprint mismatch -> FAIL
- N7: `candidateId` / `sealedEventId` mismatch -> FAIL
- N8: qualification producer workflow ID/path/run identity mismatch -> FAIL
- N9: candidate-provenance workflow run/attempt/ref mismatch -> FAIL

N3bはproduction RCを使いません。validation branchからsynthetic source branchを作り、署名なし
fixture commitを1件追加します。required checksをgreenにした後、通常actor tokenでmerge APIを
呼び、非ゼロ終了を要求します。ruleset responseで通常actorの
`current_user_can_bypass=never`を再確認します。失敗がsignature条件以外（red check、base drift、
draft等）でも起きていないことをruleset insight / API evidenceで確認します。fixture PRはcloseし、
source branchだけを削除できます。validation branchやrulesetは変更しません。

## Actual release（別セッションの明示承認が必要）

実releaseではsynthetic fixtureを使いません。既存候補artifactと、binding / decision / sealed eventを含む
別のimmutable qualification handoff artifactのrun ID、attempt、artifact ID、nameが必須です。
qualification artifactのproducer identity（repository、workflow ID/path、event、head branch/SHA）は
release environmentの保護変数で固定し、Actions APIのrun metadataと照合します。artifactが存在しない、
expired、ambiguous、producer identityが未設定/不一致、またはsealed identityを再現できない場合はSTOPします。
既存candidateの再生成・qualification再実行・seal変更・候補artifactへのsealed JSON後付けで回避してはいけません。
保護変数は`RELEASE_PROMOTION_QUALIFICATION_REPOSITORY`、`..._WORKFLOW_ID`、
`..._WORKFLOW_PATH`、`..._EVENT`、`..._HEAD_BRANCH`、`..._HEAD_SHA`です。

明示的release approval後だけ、exact RC -> main PRを作り、checks green、main tip固定、
fingerprint一致を確認して`mode=release`をdispatchします。workflowは1回の承認境界内で
merge commitを作成し、親順を検証してからannotated `v<version>` tagをexact RC SHAへ作成します。
OCI / NuGet / GitHub Release / deployはこのworkflowの後でも自動実行されません。

### Maintainer merge-freeze（必須）

GitHubのPR merge APIはPR head SHAだけをatomicに比較し、base SHAのcompare-and-swapを提供しません。
したがって、`merge_freeze_confirmation=CONFIRM_TARGET_MERGE_FREEZE`を必須入力とし、承認記録に
次を明記します。merge直前からparent検証完了まで、対象branchへの他PR merge、branch update、
force-push、ruleset変更を行わないこと。workflowは直前にbase/head/RC/checksを再取得し、merge後に
parent順を検証します。parent不一致なら即STOPし、tag作成・自動rollback・別SHA補完をしません。
このfreezeは競合リスクを低減する運用制約であり、GitHub APIのatomic base保証ではありません。
atomic保証が必要な場合は、merge queue（organization所有repositoryが必要）または専用lock rulesetの
採用を別issueで決定するまでproduction releaseを実行しません。

merge成功後にtag作成だけが失敗した場合、mainをrewindしません。merge SHA、親順、RC tip、
tag absence、ruleset fingerprintを記録してSTOPし、同じApp / validator / exact tag targetを用いた
recoveryをmaintainerが明示承認します。別SHAのtag、lightweight tag、merge commitへのtagで
補完してはいけません。

## Evidence

workflow artifact `qualified-git-promotion-evidence-<runId>` は秘密情報を含めず、次を記録します。

- adopted method / actor / actor scope / PR number
- `releaseCommitSha` / `qualificationRunId`
- merge commit SHA / parent check
- annotated tag object / target check
- normal actor bypass
- ruleset fingerprint before / after / unchanged
- result

production token、private key、input handoff JSON全文、provider / recipient情報は記録しません。

## STOP条件

RC / candidate / binding / qualification / sealの変更、rulesetの一時緩和、normal actor bypass、
exact head不一致、merge parent不一致、tag target不一致、required negative fixtureの予期しないPASS、
positive rehearsal失敗、fingerprint drift、GitHub仕様上の安全な実現不能が1件でもあればSTOPします。
main promotion、production tag、publishを続行しません。
