[English](container-image-pinning.en.md)

# Container image pinning policy

この文書は、Mailer release image の build input と、local / smoke helper image の pinning 方針を定義します。

## 方針

- `infra/docker/Dockerfile` の .NET SDK / runtime-deps base image は `tag@sha256:<digest>` で固定します。tag は可読性と Dependabot の更新検出のために残し、digest が実際の build input を固定します。
- publish workflow は現時点で `linux/amd64` のみを build します。Dockerfile の digest は registry の manifest list digest を使い、workflow の `platforms: linux/amd64` で対象 platform を選択します。
- `infra/docker/docker-compose.local.yml` と `infra/docker/docker-compose.release-smoke.yml` の Mailpit は production / release artifact に含まれない local-only helper です。既定は `axllent/mailpit:latest` のままにし、日常のローカル検証では Mailpit の修正を自然に取り込みます。
- Mailpit の特定 build で再現したい場合、または supply-chain review のために固定が必要な場合は、`MAILPIT_IMAGE=axllent/mailpit:<tag>` または `MAILPIT_IMAGE=axllent/mailpit@sha256:<digest>` で上書きします。

`latest` を使う箇所は Mailpit helper のみです。Mailer release image の base image、published GHCR image、deploy compose の Mailer image には `latest` を使いません。

## Digest 更新

Dependabot は `.github/dependabot.yml` の `docker` ecosystem で `/infra/docker` を週次確認します。Dockerfile の digest 更新 PR は自動 merge せず、通常の dependency update として review します。

更新 PR では次を確認します。

1. tag 名が意図した lineage のままか確認します（例: `10.0-noble-aot`, `10.0-noble-chiseled`）。tag 変更を含む場合は .NET / Ubuntu base の変更として扱います。
2. old / new digest を `docker buildx imagetools inspect` で確認し、workflow が使う `linux/amd64` manifest が存在することを確認します。
3. upstream の .NET container image / Ubuntu / chiseled image notes、security update、breaking change を確認します。
4. `infra/docker/Dockerfile` から Mailer image を build し、`/app/Amane.Mailer --help` または workflow 相当の image run が通ることを確認します。
5. release 前は publish workflow の digest / platform / OCI label / attestation gate と、release image smoke を通します。

例:

```bash
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0-noble-aot
docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
docker build --file infra/docker/Dockerfile --tag amane-mailer:base-image-review .
docker run --rm amane-mailer:base-image-review --help
```

Mailpit の `latest` は local helper 方針により Dependabot の digest update 対象にしません。Mailpit 起因が疑われる local / release-smoke failure では、`docker buildx imagetools inspect axllent/mailpit:latest` で digest を記録し、必要に応じて `MAILPIT_IMAGE` を固定して再実行します。

## Release evidence

release evidence には published GHCR image の digest を記録します。base image digest を更新した release では、PR または release notes に次を残します。

- Dockerfile の .NET SDK / runtime-deps tag と digest
- digest update PR の review 結果
- Docker build / image run / release smoke の結果
- publish workflow summary の published image digest、platform、attestation 状態
