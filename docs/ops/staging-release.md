# Staging image release

Amane Mailer の **staging-only OCI image release** 手順です。

正式な public release（Git tag / GitHub Release / Contracts NuGet / OpenAPI / CHANGELOG /
`release/current-public.json` / `latest`）は
[`docs/agent-workflows/release.md`](../agent-workflows/release.md) を正本とします。
この手順は、それらを変更せず、`main` の exact revision を staging 用 GHCR image として
Build Once / publish し、VPS deployment へ引き渡す場合だけに使います。

2026-09-19 の初回実績は
[`docs/deployments/staging-2026-09-19-ghcr-2.1.0-staging.1.md`](../deployments/staging-2026-09-19-ghcr-2.1.0-staging.1.md)
に記録しています。VPS 側の手順は
[`staging-deployment.md`](staging-deployment.md) を参照してください。

## 原則

- release source は Fresh に取得した exact `origin/main` SHA に固定する。
- Development VM の clean detached worktree から実施する。
- build input は committed files のみに限定する。
- linux/amd64 image は **1回だけ build** し、version tag と immutable SHA tag は同じ artifact を指す。
- VPS では build しない。
- GHCR publish で rebuild しない。
- `latest` は使わない。
- staging release は formal public release へ暗黙に昇格させない。
- GHCR publish と VPS deploy は別の Human approval boundary とする。

## 現行 tooling の制約

2026-09-19 時点では次の既存 release tooling は `X.Y.Z` のみを受理し、
`X.Y.Z-staging.N` を受理しません。

- `scripts/release-image-build-smoke.sh`
- `scripts/check-release-image-reproducibility.sh`
- `scripts/publish-release-image.sh`
- `.github/workflows/publish-release-image.yml`

staging release のためにこれらを一時的に書き換えてはいけません。
将来 prerelease version を正式サポートした場合は、その canonical tooling を優先してください。

## 1. Release identity を固定する

Human が staging version と source revision を明示します。

```text
version=X.Y.Z-staging.N
revision=<40-hex origin/main SHA>
repository=ghcr.io/kooiei-in4a/amane-mailer
version_ref=ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z-staging.N
sha_ref=ghcr.io/kooiei-in4a/amane-mailer:sha-<revision>
```

staging versionは毎回新しい識別子を使います。既に公開済みの `X.Y.Z-staging.N` を
別revisionへ付け替えたり、同じversionでrebuild / republishしません。

release 開始時に `git fetch origin main` し、`origin/main == revision` を要求します。
異なる場合は最新へ読み替えず停止します。

## 2. Clean detached worktree

Development VM の bare repository から exact revision の detached worktree を作成します。

現在の operator convention:

```text
bare repository: /srv/dev/repos/amane-mailer.git
release worktree: /srv/dev/worktrees/amane-mailer-release-<version>
artifacts:        /srv/dev/artifacts/amane-mailer-release/<version>-<short-sha>
```

既存 path を自動削除して再利用しません。worktree は clean であることを確認します。

## 3. Local validation

少なくとも次を通します。

```bash
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

検証後も source worktree を clean に保ちます。

## 4. Build Once

build context は working directory そのものではなく、exact revision の committed files から
`git archive` で作成します。

Buildx では次を固定します。

- platform: `linux/amd64`
- `SOURCE_COMMIT=<revision>`
- `MAILER_VERSION=<staging-version>`
- source commit timestamp を `SOURCE_DATE_EPOCH` として使用
- provenance / SBOM の追加によって runtime digest が変わらないよう既存 release contract と同じ設定を使用
- Docker archive と OCI layout を同一 build から生成

OCI labels:

```text
org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer
org.opencontainers.image.revision=<revision>
org.opencontainers.image.version=<staging-version>
```

### Image identity の注意

**Development VM の Docker local image ID を VPS の local image ID と比較しません。**

Buildx の Docker exporter で得られる local image ID と、GHCR から pull した後の VPS local image ID は
同じである必要がありません。deployment authority は次です。

```text
GHCR manifest digest
+ platform linux/amd64
+ OCI source label
+ OCI revision label
+ OCI version label
```

2026-09-19 の初回 staging では、この違いを誤って一致条件にしたため VPS deploy preflight が一度
fail-fast しました。サービス停止前だったため影響はありませんでした。

## 5. Local image smoke

最低限:

- `container --help` が成功
- `/healthz` が HTTP 200 / `healthy=true`
- fresh managed-v2 state の `/readyz` が HTTP 503 /
  `ready=false` / `reason=uninitialized`

generic HTTP 503 を PASS にしてはいけません。

smoke は unique Compose project / throwaway state を使い、終了時にその project だけを cleanup します。

## 6. Freeze publication digest

OCI layout の top-level runtime manifest が exactly one `linux/amd64` であること、
manifest/config/layer blob digest、OCI labels を確認し、publication digest を固定します。

build evidence には少なくとも次を残します。

```text
source revision
staging version
platform
Build VM local image ID
OCI digest
version ref
immutable SHA ref
OCI labels
smoke result
```

Build VM local image ID は evidence であり、VPS deployment authority ではありません。

## 7. GHCR publish

GHCR publication は別 Human approval 後に実施します。

publish 前に version tag と SHA tag の collision を確認し、既存なら blind retry しません。
保存済み OCI layout を pinned `crane` で直接 push し、**rebuild しません**。

順序:

```text
collision check
→ version ref へ OCI layout push
→ registry digest read-back
→ expected digest と一致
→ exact digest を immutable SHA ref へ copy
→ version/SHA 両 tag の digest read-back
→ OCI labels read-back
```

両 tag は同一 digest を指す必要があります。

## 8. Credential handling

Development VM の GHCR write PAT は operator-owned credential として継続保管して構いません。

現在の運用方針:

- credential directory: mode `0700`
- PAT file: mode `0600`
- PAT は `write:packages` に必要な最小 scope
- `delete:packages` や不要な `repo` scope を付けない
- PAT 自体は release ごとに作成・削除しない
- release ごとに temporary `DOCKER_CONFIG` を作り、終了時に削除する
- token を repository、artifact、shell output、normal Docker config に残さない

## 9. Publication evidence

secret を含まない publication record を artifacts に保存します。

最低限:

```text
source revision
version
platform
expected digest
version ref + verified digest
SHA ref + verified digest
OCI labels
publication timestamp
rebuild=false
```

GHCR publication 成功は VPS deployment approval を意味しません。

## 10. Human approval boundaries

staging release の標準 boundary:

```text
develop → main integration
        ↓ Human merge approval
exact main SHA freeze
        ↓
Build Once / local smoke
        ↓ Human GHCR publish approval
GHCR exact digest publication
        ↓ Human VPS deploy approval
VPS deployment
        ↓ explicit live-send approval when required
post-deploy E2E smoke
```