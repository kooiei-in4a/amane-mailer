# Non-live qualification lane adapter

`qualification-lane-fixture-producer.py` と `qualification-lane-adapter.py` は、Issue #583 v1.3.0 scopeに含まれるPhase A〜CのHard laneについて、manifestで固定されたcanonical procedureを実行し、実測したvalue-free predicate observationを `qualification-runner.py evidence --observations` へ渡せる完全envelopeへ変換する。

対象は次の32 variantだけである。G456-03/04/05/06、Production HTTPS、G456-35、Conditional、G583-MIG-01〜03はこのadapterの対象外である。

## 責任分離と固定procedure

manifestの32 laneはすべて `producerId` と `procedureId` を持ち、producer registryが全laneのcanonical producer availabilityをmachine-checkする。producerはlane以外のcommand、report、predicate result、observed valueを受け取らない。固定されたplatform probeとdedicated product-test procedureが完了し、skip/fail/errorがない場合だけ、procedureの固定value-free observationをmachine-readable reportへ出力する。test stdout/stderr、private host path、secret、provider raw responseは保存も出力もしない。

adapterはproducerを自ら起動し、producerのexit code/result、producerId、procedureId、procedure digest、candidate/binding/run identity、platform identityを再検証する。任意のfixture reportをCLIから渡す経路はない。

全validator fieldについて、次のcheckを1つずつ要求する。

```text
checkId = <scenarioId>/<variantId>/<fieldName>
result = PASS
proofKind = qualification-integration-observation
observedFields = { <fieldName>: <observed value> }
```

`test command exit code == 0`、`predicateResult`、`--pass`、operator指定resultだけではreportを作成できない。missing、unknown、tampered、wrong platform、wrong identity、owner不一致はfail-closedになる。

## Producer report

fixture reportはvalue-free JSONで、次のtop-level fieldだけを持つ。

```json
{
  "schemaVersion": 2,
  "kind": "qualification-lane-fixture-observations",
  "scenarioId": "G456-15",
  "variantId": "ci-auto",
  "candidateId": "<bound candidate id>",
  "releaseCommitSha": "<bound release sha>",
  "bindingId": "<bound binding id>",
  "qualificationRunId": "<bound qualification run id>",
  "executedByRole": "<authorized owner role>",
  "executedByIdentity": "<authorized value-free owner identity>",
  "startedAtUtc": "2026-08-09T00:00:00Z",
  "finishedAtUtc": "2026-08-09T00:00:01Z",
  "attestedAtUtc": "2026-08-09T00:00:01Z",
  "execution": {
    "platform": "ci-auto",
    "osFamily": "ci",
    "runtimeKind": "qualification-fixture",
    "fixtureCommandId": "g456-15-ci-auto"
  },
  "producer": {
    "producerId": "g456-15-ci-auto",
    "producerRevision": "1",
    "procedureId": "g456-15-ci-auto-canonical",
    "procedureRevision": "1",
    "procedureDigestSha256": "<digest of fixed procedure>",
    "exitCode": 0,
    "result": "PASS",
    "passedTestCount": 1,
    "totalTestCount": 1,
    "skippedTestCount": 0
  },
  "checks": [
    {
      "checkId": "G456-15/ci-auto/credentialChanged",
      "result": "PASS",
      "proofKind": "qualification-integration-observation",
      "sourceTestId": "<value-free test id>",
      "observedFields": {"credentialChanged": false}
    }
  ]
}
```

実際のproducerではvalidatorが要求する全fieldのcheckを含める。recipient、sender、subject/body、provider raw error、secret、token、connection string、private key、URL、private host pathはproducer reportへ出力しない。

## 実行

adapterが固定producerを起動した後、evidence envelopeを生成する。

```powershell
python scripts/qualification-lane-adapter.py run `
  --run-root <maintainer-run>/runs/<qualification-run-id> `
  --scenario-id G456-15 `
  --variant-id ci-auto `
  --output <value-free-observations.json>

python scripts/qualification-runner.py evidence `
  --run-root <maintainer-run>/runs/<qualification-run-id> `
  --evidence-id <adapter-output-evidence-id> `
  --scenario-id G456-15 `
  --variant-id ci-auto `
  --result PASS `
  --executed-by-role <authorized-owner-role> `
  --executed-by-identity <authorized-owner-identity> `
  --observations <value-free-observations.json>
```

adapter自身でもrunnerのenvelope validatorを呼び、runnerが受理できることを事前確認する。evidence commandが作る禁止内容scan、disposition、replayは既存runnerのappend-only contractに従う。

producer availabilityは次で確認する。

```powershell
python scripts/qualification-lane-fixture-producer.py manifest
```

`canonicalProducerAvailable=true` かつ `laneCount=32` でない場合はfail-closedとし、Hard PASSへ変換しない。

## Self-test

```powershell
python -m py_compile scripts/qualification-lane-adapter.py scripts/qualification-lane-adapter-self-test.py
python scripts/qualification-lane-adapter.py manifest
python scripts/qualification-lane-adapter-self-test.py
```

self-testはsynthetic contract negative casesに加え、実際の固定product-test procedureを `linux-docker`、`admin-local-dev`、`admin-integrated` の代表laneで起動する。その実測reportをadapterで検証し、runnerのevidence、prohibited-content scan、accept disposition、replayのactive evidenceまで実行する。RC7のqualification runやevidenceは読み書き・再利用しない。`ci-auto` procedureは実CI (`CI=true` または GitHub Actions) でのみ実行可能であり、ローカル環境による偽装は拒否する。
