[English](ghcr-image-publish.en.md)

# GHCR イメージ publish 手順

GitHub Actions で Mailer ランタイムイメージを GitHub Container Registry へ publish します。

ワークフロー:

- `.github/workflows/publish-image.yml`
- トリガー: 手動 `workflow_dispatch`（release tag ref からのみ成功）

## イメージ

- `ghcr.io/<github-org>/amane-mailer`

イメージ名は publish 時に `${{ github.repository_owner }}` から決まります。

## タグ

- `workflow_dispatch` は GitHub UI/API で release tag `vX.Y.Z` を選んで実行します。
- pre-release は `vX.Y.Z-rc.1` のような `-` 付き識別子を許可します。
- workflow input はありません。image version tag は選択された `GITHUB_REF_NAME` から決まります。
- publish されるタグは `sha-<git-sha>` と release tag（例: `v0.1.1`）だけです。
- `sha-<git-sha>` または release tag が GHCR に既に存在する場合、workflow は上書きせず失敗します。
- branch ref、形式不正な tag、tag が指す commit と checked-out commit / workflow event commit が一致しない実行は失敗します。
- deploy では可能な限り不変タグ `sha-<git-sha>` または digest を使います。

## GitHub Actions 権限

publish ジョブは次を使います:

- `contents: read`
- `packages: write`

イメージ push に repository secret は不要です。ワークフローは `GITHUB_TOKEN` を使います。

ワークフローは `infra/docker/Dockerfile` から Mailer イメージをビルドします。release build の
base image は digest pin し、更新時は [container image pinning policy](container-image-pinning.md)
に従って review / verification します。

## Release publish

1. release commit に `vX.Y.Z` tag を作成します。tag commit はこの hardened workflow を含む必要があるため、この変更を merge した後の commit に tag を切ってください。Contracts package を同じ release で publish する場合は、`src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj` の `<Version>` が `X.Y.Z` と一致している必要があります。
2. GitHub Actions の `Publish Amane Mailer Image` を release tag ref から実行します。
3. `release` environment の承認後、workflow が `sha-<git-sha>` と `vX.Y.Z` を publish します。
4. ワークフローの image run、config-content チェック、digest / platform / OCI label / attestation チェックが通ることを確認します。
5. workflow summary の digest と `sha-<git-sha>` を GitHub Release notes または release evidence に転記します。Release notes の artifact / 運用制約は [release notes checklist](release-notes-checklist.md) で確認します。

既存の `v0.1.0` image は手動 evidence で digest / provenance を確認済みです。workflow 変更だけで既存 artifact を再発行しません。

ランタイムイメージに含まれる `config/mailer` のファイルは安全なものだけです:

- `tenants.example.json`
- `tenants.local-acs.json.example`
- `tenants.schema.json`

deploy 固有の tenant JSON はイメージに焼き込みません。共有 deploy では `infra/deploy/compose.yml` の `MAILER_TENANTS_HOST_PATH` と `MAILER_TENANTS_CONTAINER_PATH` でホスト所有の tenant ファイルを mount します。tenant JSON の変更はイメージ再ビルドではなく config deploy です。

## GitHub Environment

image publish と NuGet package publish はどちらも GitHub Environment `release` を使います。

- `release` environment に必須 reviewer を設定します。
- environment の deployment branch/tag policy は release tag（例: `v*`）を許可します。
- branch ref から実行した publish は workflow 内の tag 検証で失敗します。

## SBOM / provenance / digest

`docker/build-push-action` は次を明示して実行します:

- `provenance: true`
- `sbom: true`
- `platforms: linux/amd64`

workflow は publish 前に `sha-<git-sha>` tag と release tag が GHCR に存在しないことを確認します。publish 後は build action の digest output が空でないこと、`sha-<git-sha>` tag と release tag の digest が build digest と一致すること、`docker buildx imagetools inspect --raw` に attestation manifest があることを gate します。さらに pulled image の OCI labels を検証します:

- `org.opencontainers.image.source`
- `org.opencontainers.image.revision`
- `org.opencontainers.image.version`

digest、platform、OCI labels、inspect 結果は workflow summary に出力されます。Release notes を自動更新しないため、publish 後は summary の digest と `sha-<git-sha>` を release record に転記し、[release notes checklist](release-notes-checklist.md) の artifact / 運用制約も GitHub Release notes に反映してください。

Consumer が公開済み artifact を検証する手順は
[release artifact verification](release-artifact-verification.md) にまとめます。

## Deploy host pull 認証

GHCR パッケージが private の場合、deploy host は `docker compose pull` の前に認証が必要です。

`read:packages` スコープの read-only personal access token を使います:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```
