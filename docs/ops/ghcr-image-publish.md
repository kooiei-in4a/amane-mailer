[English](ghcr-image-publish.en.md)

# GHCR イメージ publish 手順

GitHub Actions で Mailer ランタイムイメージを GitHub Container Registry へ publish します。

ワークフロー:

- `.github/workflows/publish-image.yml`
- トリガー: 手動 `workflow_dispatch`

## イメージ

- `ghcr.io/<github-org>/amane-mailer`

イメージ名は publish 時に `${{ github.repository_owner }}` から決まります。

## タグ

- 手動 `workflow_dispatch` では不変タグ `sha-<git-sha>` を付与します。
- CI が安定したら、`develop` への push で `sha-<git-sha>` と可変タグ `develop` を publish できます。
- 保護されたリリースブランチでは、environment 承認設定後に不変タグ `sha-<git-sha>` を publish できます。
- `staging` と `main` は不変タグ `sha-<git-sha>` でデプロイしてください。

## GitHub Actions 権限

publish ジョブは次を使います:

- `contents: read`
- `packages: write`

イメージ push に repository secret は不要です。ワークフローは `GITHUB_TOKEN` を使います。

ワークフローは `infra/docker/Dockerfile` から Mailer イメージをビルドします。

## 初回 publish

1. `main` ブランチから `workflow_dispatch` で `Publish Amane Mailer Image` を実行します。
2. 生成された不変タグ `sha-<git-sha>` を使います。
3. ワークフローの image run と config-content チェックが通ることを確認します。

ランタイムイメージに含まれる `config/mailer` のファイルは安全なものだけです:

- `tenants.example.json`
- `tenants.local-acs.json.example`
- `tenants.schema.json`

deploy 固有の tenant JSON はイメージに焼き込みません。共有 deploy では `infra/deploy/compose.yml` の `MAILER_TENANTS_HOST_PATH` と `MAILER_TENANTS_CONTAINER_PATH` でホスト所有の tenant ファイルを mount します。tenant JSON の変更はイメージ再ビルドではなく config deploy です。

## GitHub Environments

以下の environment はデプロイゲートとして計画されています:

- `development`: `develop` への push で使用
- `staging`: `staging` への push で使用
- `production`: `main` への push で使用

本番イメージ publish を許可する前に、GitHub で `production` environment に必須レビュアーを設定してください。

## Deploy host pull 認証

GHCR パッケージが private の場合、deploy host は `docker compose pull` の前に認証が必要です。

`read:packages` スコープの read-only personal access token を使います:

```bash
printf '%s' '<ghcr-read-token>' | docker login ghcr.io -u '<github-user>' --password-stdin
```
