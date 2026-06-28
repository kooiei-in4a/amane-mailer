[English](release-artifact-verification.en.md)

# Release artifact verification

この runbook は、OSS consumer が公開済み Amane Mailer release artifact を検証するための手順です。
maintainer 向け publish 手順は [GHCR image publish 手順](ghcr-image-publish.md) と
`.github/workflows/publish-contracts.yml` を参照してください。

## 検証対象

release ごとに次を確認します。

- GitHub Release tag と release record（`docs/releases/vX.Y.Z.md`）が同じ commit を指すこと
- GHCR image の release tag と immutable `sha-<git-sha>` tag が同じ digest を指すこと
- GHCR image の OCI `source` / `revision` label と、現行 workflow release の `version` label が
  release record と一致すること
- GHCR image index に SBOM / provenance の attestation manifest が存在すること
- NuGet package の version、repository metadata、署名、SourceLink / symbol package 方針が release record と一致すること

公開 package だけを読む場合、GHCR / NuGet の認証は不要です。GHCR package が private の場合は
`read:packages` 権限の read-only token で `docker login ghcr.io` してください。

## GHCR image digest

`docs/releases/vX.Y.Z.md` または GitHub Release notes から次の値を控えます。

- Image: `ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z`
- Digest / index digest
- Platform 一覧
- Platform ごとの runtime manifest digest
- Platform ごとの attestation manifest digest
- Immutable tag: `ghcr.io/kooiei-in4a/amane-mailer:sha-<git-sha>`
- OCI `org.opencontainers.image.revision`
- OCI `org.opencontainers.image.version`（release record に記録がある場合）

`vX.Y.Z` は対象 release に置き換えてください。

```bash
IMAGE_REPO=ghcr.io/kooiei-in4a/amane-mailer
IMAGE_TAG=vX.Y.Z
EXPECTED_INDEX_DIGEST=sha256:replace-with-release-image-digest
EXPECTED_REVISION=replace-with-release-commit-sha

IMAGE_REF="${IMAGE_REPO}:${IMAGE_TAG}"
SHA_REF="${IMAGE_REPO}:sha-${EXPECTED_REVISION}"
```

release tag と immutable tag の digest を確認します。

```bash
docker buildx imagetools inspect "$IMAGE_REF"
docker buildx imagetools inspect "$SHA_REF"
```

どちらの `Digest:` も `EXPECTED_INDEX_DIGEST` と一致する必要があります。deploy では tag より
digest pin を優先してください。

```bash
TARGET_PLATFORM=linux/amd64
docker pull --platform "$TARGET_PLATFORM" "${IMAGE_REPO}@${EXPECTED_INDEX_DIGEST}"
```

## Runtime manifest と OCI labels

image index 内の runtime manifest digest を platform ごとに確認します。`TARGET_PLATFORM` と
`EXPECTED_RUNTIME_MANIFEST_DIGEST` は release record の platform ごとの値に置き換えてください。

```bash
TARGET_PLATFORM=linux/amd64
TARGET_OS="${TARGET_PLATFORM%%/*}"
TARGET_ARCH="${TARGET_PLATFORM#*/}"
EXPECTED_RUNTIME_MANIFEST_DIGEST=sha256:replace-with-runtime-manifest-digest

docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | jq -r --arg os "$TARGET_OS" --arg arch "$TARGET_ARCH" '.manifests[]
    | select(.platform.os == $os and .platform.architecture == $arch)
    | .digest'
```

出力は `EXPECTED_RUNTIME_MANIFEST_DIGEST` と一致する必要があります。multi-arch release では
`linux/amd64` と `linux/arm64` など、release record の全 platform で繰り返してください。`jq` がない環境では
`docker buildx imagetools inspect "$IMAGE_REF"` の対象 platform manifest 行を確認してください。

OCI labels は pulled image から確認します。

```bash
docker pull --platform "$TARGET_PLATFORM" "$IMAGE_REF"

docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.source" }}'
docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}'
docker image inspect "$IMAGE_REF" --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
```

期待値は次です。

- `org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer`
- `org.opencontainers.image.revision=<release commit sha>`
- `org.opencontainers.image.version=vX.Y.Z`（現行 workflow release。古い release で release record が
  absent と明記している場合は、その release record を優先します）

`org.opencontainers.image.description` などの説明用 label は参考情報です。release record が明示しない限り、
consumer verification の判定対象には含めません。

## Provenance / attestation / SBOM

現行の image publish workflow は `docker/build-push-action` を次の設定で実行します。

- `provenance: true`
- `sbom: true`
- `platforms: linux/amd64,linux/arm64`

SBOM と provenance は standalone release asset ではなく、GHCR image index に紐づく OCI
attestation manifest として publish されます。attestation manifest の存在と digest は runtime
manifest digest ごとに確認します。

```bash
EXPECTED_ATTESTATION_MANIFEST_DIGEST=sha256:replace-with-attestation-manifest-digest

docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | jq -r --arg runtime "$EXPECTED_RUNTIME_MANIFEST_DIGEST" '.manifests[]
    | select(.annotations["vnd.docker.reference.type"] == "attestation-manifest")
    | select(.annotations["vnd.docker.reference.digest"] == $runtime)
    | .digest'
```

出力の 1 つが対象 platform の `EXPECTED_ATTESTATION_MANIFEST_DIGEST` と一致する必要があります。
`jq` がない環境では次で attestation manifest の存在を確認できますが、platform ごとの digest 照合には
`docker buildx imagetools inspect --raw` の JSON を確認してください。

```bash
docker buildx imagetools inspect --raw "$IMAGE_REF" \
  | grep -E '"vnd\.docker\.reference\.type"[[:space:]]*:[[:space:]]*"attestation-manifest"'
```

現時点では GitHub Release asset として個別の `.spdx` / `.cdx` SBOM file は添付していません。
release record には image index digest、platform ごとの runtime manifest digest、attestation manifest digest を記録します。

## NuGet package

`Amane.Mailer.Contracts` は nuget.org Trusted Publishing for GitHub Actions OIDC で publish します。
release tag、package version、project `<Version>` は publish workflow で一致検証されます。

package を取得して repository metadata と署名を確認します。

```bash
PACKAGE_ID=Amane.Mailer.Contracts
PACKAGE_ID_LOWER=amane.mailer.contracts
PACKAGE_VERSION=X.Y.Z
NUPKG="${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"

curl -L -o "$NUPKG" \
  "https://api.nuget.org/v3-flatcontainer/${PACKAGE_ID_LOWER}/${PACKAGE_VERSION}/${PACKAGE_ID_LOWER}.${PACKAGE_VERSION}.nupkg"

dotnet nuget verify "$NUPKG" --all

unzip -p "$NUPKG" "${PACKAGE_ID}.nuspec" | grep -E '<repository '
```

`dotnet nuget verify --all` が失敗する package は使用しないでください。この project は現時点では
project 固有の author signing certificate を運用していません。nuget.org から取得した package は
nuget.org repository signature を検証対象とします。

`<repository>` metadata の URL は `https://github.com/kooiei-in4a/amane-mailer`、commit は
release record の commit と一致する必要があります。

## SourceLink / symbols

Contracts package は次の package 設定で SourceLink と symbol package を生成します。

- `Microsoft.SourceLink.GitHub`
- `PublishRepositoryUrl=true`
- `EmbedUntrackedSources=true`
- `IncludeSymbols=true`
- `SymbolPackageFormat=snupkg`
- CI pack 時の `ContinuousIntegrationBuild=true`

release record と publish workflow summary で `.snupkg` の生成 / push 結果を確認してください。
NuGet indexing 後は NuGet Package Explorer または Visual Studio / Rider の debugger で SourceLink が
release commit の GitHub source を解決できることを確認します。

現時点では NuGet package 用の standalone SBOM file、SLSA provenance attestation file、project 固有の
author signature は publish していません。NuGet 側の supply-chain evidence は Trusted Publishing、
repository metadata、repository signature、SourceLink / `.snupkg` の組み合わせで確認します。

## 失敗時の扱い

次の不一致は release evidence の問題として扱います。

- release tag と immutable `sha-<git-sha>` tag の digest が違う
- OCI `revision` label が release record の commit と違う
- release record が `version` label を記録している release で、OCI `version` label が違う
- attestation manifest が存在しない、または release record の digest と違う
- NuGet package version、repository commit、SourceLink commit が release record と違う
- `dotnet nuget verify --all` が失敗する

不一致を見つけた場合は、該当 release artifact を deploy に使わず、GitHub issue で release tag、
artifact URL、観測した digest / metadata を報告してください。secret、token、recipient email、
message body、private deploy path は報告に含めないでください。
