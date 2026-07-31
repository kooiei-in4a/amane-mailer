[English](setup-release-bundle.en.md)

# Easy Setup release-candidate bundle（#455）

Windows x64 / Linux x64 / Linux arm64 向け Easy Setup **release-candidate** 配布
bundle の生成手順です。公開（tag / GHCR / GitHub Release）は
[#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) の所有です。

詳細（構成、manifest schemaVersion 1 additive、`payloadTreeSha256` /
`archiveSha256`、OCI descriptor graph、Mailpit 必須、tools へ分離した packaging、
artifact smoke、#456/#458 handoff、非目標、Agent B B1/B2/B3/M1–M5）は
[英語版](setup-release-bundle.en.md)を正本としてください。

```bash
export MAILER_VERSION=1.2.0
export MAILPIT_IMAGE='axllent/mailpit@sha256:replace-with-64-lowercase-hex-digest-here-000000000000'
# Requires image-identity.json from build-candidate-oci-image.sh
bash scripts/generate-setup-release-bundle.sh linux-x64
```

製品 CLI に `setup stage-release-bundle` は含まれません。packaging は
`tools/Amane.Mailer.ReleaseBundle` のみです。
