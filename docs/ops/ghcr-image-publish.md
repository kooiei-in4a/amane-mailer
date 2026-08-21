[English](ghcr-image-publish.en.md)

# GHCR イメージ publish

公式 Amane Mailer image の publish は、資格済み OCI layout を digest-preserving
に昇格する **P-OCI-PROMOTE** のみを使います。canonical workflow は
`.github/workflows/promote-qualified-oci.yml` です。
canonical workflowは `refs/heads/main` からdispatchし、candidateのrefは
pre-login validatorで別途固定します。
product refはcandidate bytesとqualificationを所有し、`main`はrelease-infrastructure
のpromotion wrapperとproof生成だけを所有します。

> **v1.2.0 note:** 公開 OCI は **P-OCI-PROMOTE**（資格済み layout の promote）で、
> `EXTERNAL_PROVENANCE` のため registry attestation manifest は添付しません。
> 証跡は [docs/releases/v1.2.0.md](../releases/v1.2.0.md) と GitHub Release 添付、
> 検証手順は [release-artifact-verification](release-artifact-verification.md) を参照。
> 以下は従来の rebuild-as-publish workflow（`publish-image.yml`）の手順です。

## 必須 handoff

同一の `Generate Setup Release Candidate` 成功runからOCI artifactとhandoff
artifactを取得し、sealed qualification handoffも取得します。registry login前に
再buildせず、次を検証します:

- workflow name/path、event、head branch/SHA、run ID、run attempt;
- OCI/handoff artifactの名前・IDと期限切れでないこと;
- `candidateId`、`qualificationRunId`、`releaseCommitSha`、OCI index digest、
  `image-identity.json`、`candidate-provenance.json`、`buildx-metadata.json`;
- immutableなbinding 1件、`GO_ELIGIBLE` + `APPROVE` decision、sealed run-status
  event 1件。

不一致や欠落はregistry login前にfail-closedします。source digestはlayoutの
`index.json`が参照する最終image-index blobのdigestです。

## publish結果

同一OCI layoutを次の2つのtagにだけpushします:

- `vX.Y.Z`
- `sha-<releaseCommitSha>`

両方のdigestがsource image-index digestと一致することを確認します。`latest`は
作成しません。`EXTERNAL_PROVENANCE`を維持し、registry attestation manifestは
追加しません。runtime値から `promote-proof.json` を生成してworkflow artifactに
保存します。

## legacy route（廃止済み）

`.github/workflows/publish-image.yml` はfail-closed tombstoneとして当面残します。
製品build、registry login、push、tag作成を行わず、
`promote-qualified-oci.yml`を明示して停止します。参照docsと運用手順の整理後、
削除は別変更で判断します。

## 権限とenvironment

canonical promotionは`contents: read`、`actions: read`、`packages: write`と既存の
`release` environment承認を使います。repository publish secretは不要で、job単位の
`GITHUB_TOKEN`を使います。

## deploy host pull認証

GHCRがprivateの場合、deploy hostは`docker compose pull`前に`read:packages`のみの
read-only tokenで認証します:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```

検証手順は [Digest-preserving OCI promotion](oci-promote.md) と
[release artifact verification](release-artifact-verification.md) を参照してください。
