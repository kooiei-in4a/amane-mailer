[English](ghcr-image-publish.en.md)

# GHCR イメージ publish

公式 Amane Mailer image の publish には、用途の異なる二つの経路があります。

- #649 の早期経路: `.github/workflows/publish-release-image.yml`。指定した
  source SHA から `linux/amd64` を一度だけ build し、smoke test と no-cache
  digest 再現性 gate を通した同じ OCI layout を publish します。
- 資格済みリリース経路: 資格済み OCI layout を digest-preserving に昇格する
  **P-OCI-PROMOTE**。canonical workflow は
  `.github/workflows/promote-qualified-oci.yml` です。

早期経路は amd64 の最小公開対象であり、multi-arch qualification の代替では
ありません。どちらの経路も build 後に別の bytes を再 build して publish しません。
資格済み経路の canonical workflow は `refs/heads/main` から dispatch し、candidate の ref は
pre-login validatorで別途固定します。
product refはcandidate bytesとqualificationを所有し、`main`はrelease-infrastructure
のpromotion wrapperとproof生成だけを所有します。

> **v1.2.0 note:** 公開 OCI は **P-OCI-PROMOTE**（資格済み layout の promote）で、
> `EXTERNAL_PROVENANCE` のため registry attestation manifest は添付しません。
> 証跡は [docs/releases/v1.2.0.md](../releases/v1.2.0.md) と GitHub Release 添付、
> 検証手順は [release-artifact-verification](release-artifact-verification.md) を参照。
> 以下の「必須 handoff」以降は資格済み P-OCI-PROMOTE の手順です。
> `publish-image.yml` は廃止済みの fail-closed tombstone です。

## #649 早期経路

`.github/workflows/publish-release-image.yml` は `refs/heads/main` から
maintainer が dispatch します。`source_sha` は 40 桁の commit SHA、
`release_version` は `major.minor.patch` を指定します。既存の `release`
environment 承認後、次を順に行います:

1. 指定 SHA の `linux/amd64` image を build し、`--help`、`/healthz`、`/readyz` を確認。
2. cache を使わずに同じ build を行い、manifest digest が一致することを確認。
3. smoke 済み OCI layout を `vX.Y.Z` と `sha-<sourceSha>` にだけ push し、両方の digest を再確認。
4. `verify-public-image` job が、publish job の成功後に同じ digest を read-only で
   GHCR から確認する。検証 job は build、login、push を行わず、`packages: write`
   も持たない。

`latest` は作成せず、registry attestation manifest も追加しません。失敗時は
registry login 前なら publish されません。multi-arch の資格・handoff が必要な
リリースは、以下の P-OCI-PROMOTE 経路を使います。

## 公開証跡

publish job は build smoke、no-cache reproducibility、publish 入力を value-free
artifact（retention 14 日）として保存します。続く検証 job は、次の read-only
確認を行い、最終 artifact（retention 30 日）を保存します。

- `vX.Y.Z` tag と `sha-<sourceSha>` tag が期待 digest を指すこと
- 両 tag の digest が一致すること
- 公開済み digest を `linux/amd64` として pull できること
- digest 指定 image の OCI `source` / `revision` / `version` label が一致すること
- digest 指定 image の `--help` が成功すること

最終証跡の保存先は artifact 内の
`artifacts/publish-release-image/release-publication-evidence.json` です。
`schemaVersion: 1` の `release-image-publication` schema は、
`workflowRunId` / `workflowRunAttempt` / `workflowName` /
`workflowRef` / `gitRef`、source SHA、release version、platform、published digest、
両 tag、tag ごとの確認済み digest、OCI labels、三つの gate 結果、
`recordedAtUtc` を含みます。公開 consumer の詳細は同じ artifact の
`public-consumer-verification.json` に分離します。token、認証情報、PII、
秘密 URL、raw registry error は証跡に保存しません。

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
