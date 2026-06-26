# Release notes checklist

[English](release-notes-checklist.en.md)

GitHub Release notes は OSS consumer が release page だけで artifact と主要な
運用制約を判断できるように、公開前または公開直後に次の項目を確認して記載します。

## Artifact

- Release tag（例: `v0.1.0`）
- Annotated tag object（annotated tag の場合）
- Tag target commit SHA
- Docker image（例: `ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z`）
- Image digest / index digest
- 不変 Docker tag（`sha-<git-sha>`）と tag digest
- Runtime manifest digest
- Attestation manifest digest
- Platform（現状 `linux/amd64`）
- OCI source label と revision label
- NuGet package name / version / package URL
- .NET SDK version（`global.json`）と roll-forward policy

## Operational notes

- `202 Accepted` は「Mailer が依頼を永続化した」ことを表し、provider delivery
  完了ではない。
- Delivery は at-least-once。provider 送信成功後、Mailer DB の `delivered`
  更新前に停止した場合、同じメールが再送される可能性がある。
- SQLite deployment は single-node / single-replica 前提。単一 SQLite file を
  共有する複数 Worker の水平化は現在の運用対象外。
- Docker image の対応 platform を明記する。現状は `linux/amd64` only。
- Admin UI は disabled by default、内部ネットワーク向け、experimental。現時点の
  limitation（durable session/throttle/audit/tenant scope など）を明記する。
- upgrade / migration 前に SQLite DB と tenant config の backup を取得し、
  production では restore 手順も確認する。
- ACS live sending は explicit config が必要。`MAILER_PROVIDER=acs`、
  `ACS_CONNECTION_STRING`、`live_sending=true` tenant、ACS で承認済み sender/domain
  が揃う場合だけ実送信する。

## References to verify

- `docs/releases/vX.Y.Z.md`
- `docs/ops/public-repository-p0-evidence.md`
- `CHANGELOG.md`
- `README.md` / `README.en.md`
- `docs/service-spec.md` / `docs/service-spec.en.md`
- `docs/adr/0012-mail-via-mailer-microservice.md`
- `docs/adr/0013-admin-threat-model-and-pii-policy.md`
- `docs/ops/ghcr-image-publish.md` / `.en.md`
- `docs/ops/backup-operations.md` / `.en.md`
- `docs/ops/restore-procedure.md` / `.en.md`
- GitHub Release body (`gh release view vX.Y.Z --repo kooiei-in4a/amane-mailer`)
