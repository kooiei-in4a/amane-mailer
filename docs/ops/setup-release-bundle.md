[English](setup-release-bundle.en.md)

# Easy Setup release-candidate bundle（#455）

Windows x64 / Linux x64 / Linux arm64 向け Easy Setup **release-candidate** 配布
bundle の生成手順です。公開（tag / GHCR / GitHub Release）は
[#458](https://github.com/kooiei-in4a/amane-mailer/issues/458) の所有です。

詳細（構成、manifest schemaVersion 1 additive、OCI layout digest 固定、再現性、
checksum、secret scan、#456/#458 handoff、非目標、Agent B B1/M1–M6）は
[英語版](setup-release-bundle.en.md)を正本としてください。

```bash
export MAILER_VERSION=1.2.0-candidate
bash scripts/generate-setup-release-bundle.sh
```
