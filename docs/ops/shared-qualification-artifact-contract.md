# Shared qualification artifact contract

Issue [#622](https://github.com/kooiei-in4a/amane-mailer/issues/622) で、Git / OCI
promotion が共有する qualification artifact の preparation boundary を定義します。
この契約はsealed qualification formatを変更せず、producer metadataを含むtransport artifactと、
既存strict sealed validatorへ渡すsealed-only viewを分離します。

## Production artifact shape

download後のartifact rootは次の5ファイルだけを含みます。wrapper directory、nested copy、
追加ファイル、欠落ファイル、symlinkは許可しません。

```text
artifact-root/
├── handoff-manifest.json
├── binding.json
├── qualification-producer.json
├── decision/
│   └── go-no-go.json
└── run-status-events/
    └── <exactly-one-event-id>.json
```

`qualification-producer.json` は次の8 fieldだけを持ちます。

- `repository`
- `workflowPath`
- `workflowId`
- `event`
- `headBranch`
- `headSha`
- `runId`
- `runAttempt`

`workflowId`、`runId`、`runAttempt`はpositive integer、`headSha`は40文字lowercase hexです。
期待値はpromotion workflowがtrusted configurationとGitHub Actions run APIから作成します。
artifact自身が期待producer identityを決めることはありません。

## Preparation boundary

[`prepare-qualification-handoff.py`](../../scripts/prepare-qualification-handoff.py) は次を
fail-closedで実行します。

1. artifact rootのexact file setと1件だけのrun-status eventを検証する。
2. 全entryのsymlinkを拒否する。
3. producer documentがexact 8-field contractであり、trusted expected identityと全field一致することを検証する。
4. immutable artifact rootの外側にある空のoutput directoryだけを受け入れる。
5. sealed 4文書だけをcopyし、source / destination SHA-256一致を各fileで検証する。

input artifactは変更しません。outputは次のsealed-only shapeです。

```text
sealed-root/
├── handoff-manifest.json
├── binding.json
├── decision/
│   └── go-no-go.json
└── run-status-events/
    └── <exactly-one-event-id>.json
```

このoutputを既存の
[`validate-qualification-handoff.sh`](../../scripts/validate-qualification-handoff.sh)へ渡します。
strict sealed validatorはproducer transport metadataを解釈せず、sealed object allowlist、digest、
candidate / binding / run / source / OCI identity、GO / APPROVE / sealed semanticsを検証します。

## Consumer sequence

Git / OCI promotionはどちらもproduction modeで次の順序を維持します。

```text
trusted Actions run and artifact identity
              ↓
downloaded immutable production artifact
              ↓
shared preparation helper
              ↓
byte-identical sealed-only view
              ↓
unchanged strict sealed validator
              ↓
Git-specific or OCI-specific promotion checks
```

producer mismatch、unexpected / missing / nested file、symlink、copy digest mismatch、strict sealed
validation failureのいずれでもpromotion mutation前にSTOPします。旧layoutへのsilent fallbackや
artifact mutationによる互換化は行いません。

## Regression fixture

共通production-shape fixtureは
[`scripts/fixtures/qualification-handoff/production-shape`](../../scripts/fixtures/qualification-handoff/production-shape)
です。shared preparation self-testとGit promotion self-testは同じfixtureを使用し、negative caseは
そのcopyへ限定mutationを加えて作成します。これによりGit / OCI consumerごとのlogical fixture
重複を避けます。

```bash
python3 scripts/prepare-qualification-handoff-self-test.py
python3 scripts/validate-qualified-git-promotion-self-test.py
```

fixtureはproduction shapeの契約テスト専用であり、release evidenceやrelease authorityではありません。
