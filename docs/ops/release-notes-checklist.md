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
- Platform 一覧（Docker manifest と同じ表記。例: `linux/amd64`, `linux/arm64`）
- Platform ごとの runtime manifest digest
- Platform ごとの attestation manifest digest
- Release image smoke 結果（`docs/releases/vX.Y.Z.md`、digest / 日付 / 環境 / pass-fail 要約）
- OCI source label と revision label
- NuGet package name / version / package URL
- NuGet symbol package:
  - 生成: publish workflow summary で `.snupkg` file 名と `Verify symbol package was produced` の成功を確認
  - push: `Push symbols to nuget.org` の結果、または `.nupkg` push による symbol package 作成と explicit symbol push の `--skip-duplicate` 結果を確認
  - availability: `https://www.nuget.org/api/v2/symbolpackage/Amane.Mailer.Contracts/X.Y.Z` から `.snupkg` を取得でき、`lib/net8.0/Amane.Mailer.Contracts.pdb` を含むことを確認
  - indexing / debugging: NuGet Package Explorer、Visual Studio、または Rider で SourceLink / symbol 解決を手動確認。未確認なら release record に `not verified` と記録
- SourceLink commit が release tag commit と一致することの確認
- .NET SDK version（`global.json`）と roll-forward policy

## Operational notes

- `202 Accepted` は「Mailer が依頼を永続化した」ことを表し、provider delivery
  完了ではない。
- `202 Accepted`、Mailer の provider invocation、provider 側の実配送結果を
  別々に説明する。`exactly-once delivery` を保証する表現は使わない。
- submission evidence を採用する release では、provider invocation 開始後の
  lease expiry / process restart を理由に同じ request を自動再 invoke しない。
  受理可否が曖昧な場合の公開状態、consumer の exhaustive enum 対応、同じ
  `mail_request_id` を再送してはいけない条件、業務上の新規 request による
  resend 手順を release notes に明記する（v1.3.0 では `delivery_unknown`）。
- SDK / HTTP client の 503 retry と Mailer 内部の provider 再 invoke は別概念で
  あることを確認する。HTTP acceptance の冪等再送を provider delivery の安全性と
  混同しない。
- SQLite deployment は single-node / single-replica 前提。単一 SQLite file を
  共有する複数 Worker の水平化は現在の運用対象外
  （着手条件と非目標は [ADR 0019](../adr/0019-sqlite-single-process-boundaries.md)）。
- Docker image の対応 platform を Docker manifest と同じ表記で明記する。single-platform release では
  `linux/amd64 only` のように制約を明記し、multi-arch release では platform ごとの digest / smoke 結果を記録する。
- Admin UI は disabled by default、内部ネットワーク向け、experimental。現時点の
  limitation（durable session/throttle/audit、tenant scope 運用境界、retention sweep など）を明記する。
- upgrade / migration 前に SQLite DB と tenant config の backup を取得し、
  production では restore 手順も確認する。
- GHCR image publish 時は `promote-qualified-oci.yml` の pre-login identity
  validation（candidate run/attempt、artifact IDs、candidateId、sealed
  qualification、releaseCommitSha、OCI index digest）が通過していることを
  確認する。旧 `publish-image.yml` はfail-closed tombstoneであり、再buildや
  image pushを行わない。
- ACS live sending は explicit config が必要。`MAILER_PROVIDER=acs`、
  Staging/Production では `admin provider register-acs` で登録した
  `ACS_CONNECTION_STRING_FILE`（local/drill のみ bare `ACS_CONNECTION_STRING`）、
  `live_sending=true` tenant、ACS で承認済み sender/domain が揃う場合だけ実送信する。

## References to verify

- `docs/releases/vX.Y.Z.md`
- `docs/ops/public-repository-p0-evidence.md`
- `CHANGELOG.md`
- `README.md` / `README.en.md`
- `docs/service-spec.md` / `docs/service-spec.en.md`
- `docs/adr/0012-mail-via-mailer-microservice.md`
- `docs/adr/0013-admin-threat-model-and-pii-policy.md`
- `docs/ops/ghcr-image-publish.md` / `.en.md`
- `docs/ops/release-artifact-verification.md` / `.en.md`
- `docs/ops/backup-operations.md` / `.en.md`
- `docs/ops/restore-procedure.md` / `.en.md`
- GitHub Release body (`gh release view vX.Y.Z --repo kooiei-in4a/amane-mailer`)
