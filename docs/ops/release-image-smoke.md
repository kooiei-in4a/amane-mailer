[English](release-image-smoke.en.md)

# 公開 release イメージの clean-state smoke

v1.2.0 publish 後に、GHCR ランタイムイメージ（既定 `ghcr.io/kooiei-in4a/amane-mailer:v1.2.0`）を
clean state から pull し、Mailer + Mailpit を起動して release runtime path を自動 smoke します。

ローカル開発の `infra/docker/docker-compose.local.yml`（ソースから build）とは異なり、
この smoke は **publish 後の release image そのもの** を検証します。tenant 設定はイメージに同梱された
安全な example（`/app/config/mailer/tenants.example.json`）を使い、host の tenant JSON は mount しません。
Mailer の状態は named volume に置き、終了時に `docker compose down -v` で削除します。

## 前提

- Docker（compose plugin 同梱）が起動していること。
- Linux / macOS / Git Bash では `bash`、`curl`、`sha256sum` が使えること。
- Windows では PowerShell 5.1+ と Docker Desktop（PowerShell と同じ Docker CLI context）を使うこと。
- GHCR イメージが pull できること（private の場合は事前に `docker login ghcr.io`。
  [GHCR image publish 手順](ghcr-image-publish.md) を参照）。
- v1.2.0 release では、既定 smoke tag `v1.2.0` の GHCR runtime image は
  **multi-arch**（`linux/amd64` と
  `linux/arm64`）です。smoke では release notes または Docker manifest の platform を確認し、
  `MAILER_IMAGE_PLATFORM=linux/amd64` または `MAILER_IMAGE_PLATFORM=linux/arm64` を指定してください。
- amd64 emulation のみ利用可能なホストでは `linux/amd64` を明示してください。
- 既定の host port `15280`（Mailer）と `18025`（Mailpit）が空いていること。

## 実行

リポジトリ root で実行します。

Linux / macOS / Git Bash:

```bash
bash scripts/release-smoke.sh
```

Windows（PowerShell、Docker Desktop）:

```powershell
.\scripts\release-smoke.ps1
```

WSL の `bash scripts/release-smoke.sh` は Docker Desktop の Windows 側 daemon と
context がずれることがあるため、Windows では上記 PowerShell 版を使ってください。

スクリプトは次を行います。

1. 残っていれば前回の smoke compose project を削除する。
2. 対象 release image と Mailpit を pull し、clean な project / named volume で起動する。
3. 以下の check を実行し、各行に `[PASS]` / `[FAIL]` を出力する。
4. 終了時（失敗時も）に compose project と volume を削除する。

確認する項目:

- `GET /healthz` が `200`
- `GET /readyz` が `200`
- 正常 `POST /internal/mail-requests` が `202 accepted`
- Mailpit にメッセージが到着する
- 同じ `mail_request_id` + 同じ payload が `202 already_accepted`
- 同じ `mail_request_id` + 異なる payload が `409 IDEMPOTENCY_CONFLICT`
- 不正 token が `401 UNAUTHORIZED_TENANT`
- 不正 `source_service` が `403 SOURCE_SERVICE_NOT_ALLOWED`

いずれかが落ちると終了コードは `1` になり、末尾に `Smoke result: N passed, M failed` を出力します。
起動自体に失敗した場合は `docker compose ps` と直近ログを出力します。

## 設定（環境変数、すべて任意）

| 変数 | 既定 | 用途 |
|------|------|------|
| `MAILER_IMAGE_REPOSITORY` | `ghcr.io/kooiei-in4a/amane-mailer` | イメージ repository |
| `MAILER_IMAGE_TAG` | `v1.2.0` | 検証するタグ |
| `MAILER_IMAGE_PLATFORM` | `linux/amd64` | smoke 対象の Mailer runtime image platform。multi-arch release では `linux/amd64` / `linux/arm64` など release notes の platform ごとに実行します。 |
| `MAILER_PULL_POLICY` | `always` | ローカルイメージを使う場合は `missing` |
| `MAILPIT_IMAGE` | `axllent/mailpit:latest` | Mailpit helper image。既定の `latest` は意図的です。tag / digest 固定が必要な場合に上書きします。 |
| `MAILER_HTTP_PORT` | `15280` | Mailer の host port |
| `MAILPIT_HTTP_PORT` | `18025` | Mailpit API/UI の host port |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` | example tenant の token |
| `RELEASE_SMOKE_PROJECT` | `amane-mailer-release-smoke` | compose project 名 |
| `RELEASE_SMOKE_KEEP` | （未設定） | `1` で終了時の cleanup を skip（デバッグ用） |

別タグを検証する例:

```bash
MAILER_IMAGE_TAG=sha-<git-sha> bash scripts/release-smoke.sh
```

```powershell
$env:MAILER_IMAGE_TAG = 'sha-<git-sha>'; .\scripts\release-smoke.ps1
```

Mailpit は release artifact に含まれない smoke helper です。`latest` の扱いと固定が必要な場合の
手順は [container image pinning policy](container-image-pinning.md) を参照してください。

## 記録済み smoke 結果

`v1.2.0` の value-free smoke 結果（digest、日付、環境、各 check の pass/fail）は
[docs/releases/v1.2.0.md](../releases/v1.2.0.md) に記録します。過去の `v1.1.0` 結果は
[docs/releases/v1.1.0.md](../releases/v1.1.0.md)、`v1.0.1` 結果は
[docs/releases/v1.0.1.md](../releases/v1.0.1.md)、`v1.0.0` 結果は
[docs/releases/v1.0.0.md](../releases/v1.0.0.md)、`v0.9.2` 結果は
[docs/releases/v0.9.2.md](../releases/v0.9.2.md)、`v0.9.1` 結果は
[docs/releases/v0.9.1.md](../releases/v0.9.1.md)、`v0.9.0` 結果は
[docs/releases/v0.9.0.md](../releases/v0.9.0.md)、`v0.4.0` 結果は
[docs/releases/v0.4.0.md](../releases/v0.4.0.md)、`v0.3.0` 結果は
[docs/releases/v0.3.0.md](../releases/v0.3.0.md)、`v0.2.0` 結果は
[docs/releases/v0.2.0.md](../releases/v0.2.0.md) を参照してください。

## deploy drill との使い分け

- `scripts/release-smoke.sh` / `scripts/release-smoke.ps1`: **対象 release image** の HTTP /
  冪等性 / Mailpit delivery を clean state から一括検証する release smoke。host 側の
  HTTP クライアント（bash 版は curl、PowerShell 版は `Invoke-WebRequest`）のみで完結します。
  Admin UI・webhook HTTPS tenant 起動・`db backup` CLI は対象外です。
- `scripts/native-aot-path-smoke.sh`: CI の `Native AOT publish smoke` が publish した
  **linux-x64 Native AOT binary** に対し、Admin login・HTTPS webhook tenant の `/readyz`・
  `db backup` など低頻度 path を black-box 検証します（issue #286）。release image や
  Mailpit 配送は対象外です。ACS live は secret 依存のため手動のままです。
- `infra/deploy/drills/mail-05a-*`: deploy host 上の稼働中 compose stack に対する
  no-send / ACS deploy drill。SQLite Mailer CLI（`healthcheck`、`db stats`、`db request-state`）と
  一時的な curl compose client を使い、worker 無効化や DB 状態確認まで踏み込みます。
  詳細は [docs/ops/drills/mail-05a-drill-guide.html](drills/mail-05a-drill-guide.html) を参照してください。
