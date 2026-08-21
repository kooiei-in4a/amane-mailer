[English](setup-release-bundle.en.md)

# Easy Setup release-candidate bundle（#455）

オペレーター向けの判断・開始手順の正本は [setup-guide](setup-guide.md) です。

Windows x64 / Linux x64 / Linux arm64 向け Easy Setup **release-candidate** 配布
bundle の生成手順です。公開（tag / GHCR / GitHub Release）は
[#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) が所有して完了しました。
公開済み v1.3.0 の消費は [setup-guide](setup-guide.md)、
[release record](../releases/v1.3.0.md)、
[GitHub Release](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.0)
（Windows / Linux x64 / Linux arm64 archive + checksum / provenance 添付）を使います。

詳細（構成、manifest schemaVersion 1 additive、`payloadTreeSha256` /
`archiveSha256`、OCI descriptor graph + Buildx digest↔`index.json` `manifests[]`
束縛（`sha256(index.json)` ではない）、
Mailpit 必須 / tag 拒否、`supportedReleaseManifestSchemaMin`/`Max`、
tools へ分離した packaging、artifact smoke、workflow artifact
`setup-release-candidate-oci`、#456 OCI import（containerd / skopeo）、
#458 promote（再 attest で digest 変化の可能性）、非目標、Agent B 指摘対応）は
[英語版](setup-release-bundle.en.md)を正本としてください。

なお、OCI candidate の生成は #557 で native platform build + assemble 方式に
なりました。`linux/amd64` は `ubuntu-24.04`、`linux/arm64` は
`ubuntu-24.04-arm` で個別にBuildxを実行し、QEMU・registry login・push・cacheは
使いません。既存の論理job `build-oci` が両方の検証済みgraphを決定的にassembleし、
最終の `buildx-metadata.json`、`oci-index.digest`、`image-identity.json` を従来どおり
`setup-release-candidate-oci` に生成します。`oci-index.digest` は `index.json` が参照する
最終image-index blobのdigestであり、`sha256(index.json)`ではありません。

```bash
export MAILER_VERSION=1.3.0
export MAILPIT_IMAGE='axllent/mailpit@sha256:replace-with-64-lowercase-hex-digest-here-000000000000'
# Requires image-identity.json from build-candidate-oci-image.sh
bash scripts/generate-setup-release-bundle.sh linux-x64
```

製品 CLI に `setup stage-release-bundle` は含まれません。packaging は
`tools/Amane.Mailer.ReleaseBundle` のみです。
