# Non-live qualification lane adapter

`qualification-lane-fixture-producer.py` と `qualification-lane-adapter.py` は、Issue #583 v1.3.0 scopeに含まれるPhase A〜CのHard laneについて、manifestで固定されたcanonical procedureを実行し、fixtureが実測したvalue-free predicate observationをevidence envelopeへ変換する。

対象manifestは32 variantである。G456-03/04/05/06、Production HTTPS、G456-35、Conditional、G583-MIG-01〜03はこのadapterの対象外である。

## 責任分離と固定procedure

manifestの各laneは `producerId` と `procedureId` を持つ。実際に実行可能なlaneにはさらに `canonicalFixture` として、fixture ID、revision、完全なsource test ID、固定test selectorを登録する。producer registryは32 laneの件数・重複・validator対応・fixture identityをmachine-checkし、`availableLaneCount` と `canonicalProducerAvailable` を出力する。

structured result producerまで実装済みなのはmanifestの32 laneすべてである。各laneは `QualificationFixtureTests` の完全修飾test IDに結合され、manifestにfixtureがあるのにproducerが欠落・不一致の場合はfail-closedになる。

canonical fixture自身が実操作を行い、validatorが要求する全fieldのobservationsをvalue-freeなstructured resultとして出力する。producerはPASS値を生成・補完せず、fixture resultのschema、fixture identity、exact test case、終了結果、skip/fail/errorのない単一実行、value-free性、登録predicateを検証する。stdout/stderr、private host path、secret、provider raw responseは保存も出力もしない。

adapterはproducerを自ら起動し、producerのexit code/result、producerId、procedureId、procedure digest、fixture result digest、exact source test ID、candidate/binding/run identity、platform identityを再検証する。任意のfixture reportやoperator指定resultをCLIから渡す経路はない。

全validator fieldについて、fixture resultから次のcheckを1つずつ導出する。

```text
checkId = <scenarioId>/<variantId>/<fieldName>
result = PASS
proofKind = qualification-integration-observation
sourceTestId = <exact canonical fixture test id>
observedFields = { <fieldName>: <fixture-measured value> }
```

`test command exit code == 0`、`passed > 0`、`predicateResult`、`--pass`、operator指定resultだけではreportを作成できない。fixture resultのfield欠落・skip・失敗・改ざん・unknown field、wrong platform、wrong identity、owner不一致はfail-closedになる。

## Structured fixture result と producer report

fixtureは次のvalue-free resultを生成する。observationsの値は実操作の応答・設定・状態からfixture自身が観測した値であり、producerのPASS定数表からは供給しない。

```json
{
  "schemaVersion": 1,
  "kind": "qualification-fixture-result",
  "fixtureId": "g456-07-admin-local-dev",
  "fixtureRevision": "1",
  "scenarioId": "G456-07",
  "variantId": "admin-local-dev",
  "sourceTestId": "<exact test id>",
  "result": "PASS",
  "operationExitCode": 0,
  "observations": {
    "accessProfile": "development-loopback",
    "transportProfile": "http-loopback",
    "loopbackOnly": true,
    "loginResult": "success",
    "setupStatusResult": "visible",
    "adminRouteResult": "available",
    "sensitiveOutput": "absent"
  }
}
```

adapterへ渡すproducer reportはschema version 3で、`fixtureResult`本体とそのdigestを含む。全fieldのcheckはこの同じfixture resultとexact source test IDへ機械的に結合される。recipient、sender、subject/body、provider raw error、secret、token、connection string、private key、URL、private host pathはproducer reportへ出力しない。

## 実行

adapterが固定producerを起動した後、value-free evidence envelopeを生成する。実装済みfixtureの例は次のとおりである。

```powershell
python scripts/qualification-lane-adapter.py run `
  --run-root <maintainer-run>/runs/<qualification-run-id> `
  --scenario-id G456-07 `
  --variant-id admin-local-dev `
  --output <value-free-evidence.json>
```

manifestにないlaneやcanonical fixture identityが不一致のlaneを実行するとcanonical structured fixture unavailableとして終了し、Hard PASSは作成されない。adapter自身でもrunnerのenvelope validatorを呼び、runnerが受理できることを事前確認する。evidence commandが作る禁止内容scan、disposition、replayは既存runnerのappend-only contractに従う。

producer availabilityは次で確認する。

```powershell
python scripts/qualification-lane-fixture-producer.py manifest
python scripts/qualification-lane-adapter.py manifest
```

出力の `laneCount` と `availableLaneCount` は32、`canonicalProducerAvailable` はtrueである。ただし、実行時はmanifestが要求するDocker OS、CI、Windows hostなどの環境境界を満たすlaneだけを実行する。

## Self-test

```powershell
python -m py_compile scripts/qualification-lane-adapter.py scripts/qualification-lane-fixture-producer.py scripts/qualification-lane-adapter-self-test.py
python scripts/qualification-lane-adapter.py manifest
python scripts/qualification-lane-adapter-self-test.py
```

self-testはsynthetic contractのnegative casesでfixture result差し替え、digest不一致、exact source test ID不一致、field欠落、fixture FAIL、check改ざんを拒否する。さらに `G456-07/admin-local-dev` の実fixtureを起動し、実測structured result → producer → adapter → evidence → prohibited-content scan → accept disposition → replayのactive PASSまで実行する。RC7のqualification runやevidenceは読み書き・再利用しない。
