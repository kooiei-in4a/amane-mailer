[English](release-image-smoke.en.md)

# 公開 release イメージの clean-state smoke

v1.3.7 publish 後の GHCR ランタイムイメージ（現在公開中の例: `ghcr.io/kooiei-in4a/amane-mailer:v1.3.7`）を
clean state から pull し、Mailer + Mailpit を起動して release runtime path を自動 smoke します。

ローカル開発の `infra/docker/docker-compose.local.yml`（ソースから build）とは異なり、
この smoke は **publish 後の release image そのもの** を検証します。tenant 設定はイメージに同梱された
安全な example（`/app/config/mailer/tenants.example.json`）を使い、host の tenant JSON は mount しません。
Mailer の状態は named volume に置き、終了時に `docker compose down -v` で削除します。

## サポート対象プラットフォーム

| 区分 | 対象 |
|------|------|
| **Release smoke gate（サポート）** | Linux local Docker 上の `scripts/release-smoke.sh` のみ |
| **Contract parity（非 gate）** | `scripts/release-smoke.ps1` — shell 版と同一 contract を保つ PowerShell 実装。Linux 上の fixture / self-test で contract を検証する |
| **サポート対象外** | Windows Docker Desktop 上での release smoke live 実行 |

Windows Docker Desktop を release / acceptance gate としてはサポートしません。
公開 release image の clean-state smoke は **Linux local Docker のみ** を公式 gate とします。
`release-smoke.ps1` は削除せず contract 同等性の検証用として維持しますが、Windows 上での live smoke PASS を要求しません。

## 前提

- Docker（compose plugin 同梱）が Linux 上で起動していること。
- `bash`、`curl`、`sha256sum` が使えること。
- GHCR イメージが pull できること（private の場合は事前に `docker login ghcr.io`。
  [GHCR image publish 手順](ghcr-image-publish.md) を参照）。
- v1.3.6 release の runtime image platform は **`linux/amd64` only** です。release notes または Docker manifest を確認し、
  必要に応じて `MAILER_IMAGE_PLATFORM=linux/amd64` を明示してください。
- 既定の host port `15280`（Mailer）と `18025`（Mailpit）が空いていること。
- **検証対象 Mailer イメージは `MAILER_IMAGE_TAG` または `MAILER_IMAGE_DIGEST` のどちらか一方を必ず明示**すること（暗黙 default はありません）。

## 実行

リポジトリ root で実行します（**サポート対象の canonical operational entrypoint**）:

```bash
MAILER_IMAGE_TAG=v1.3.6 bash scripts/release-smoke.sh
```

immutable digest で検証する例:

```bash
MAILER_IMAGE_DIGEST=sha256:<digest> bash scripts/release-smoke.sh
```

### PowerShell 版（contract parity / 非 gate）

`scripts/release-smoke.ps1` は shell 版と同一 contract を保つ実装です。
release gate としては **Linux local Docker の shell 版のみ** をサポートします。
PowerShell 版の contract 検証は Linux 上の以下を使用してください。

- `scripts/release-smoke-preflight-self-test.ps1`
- `scripts/release-client-self-test.ps1`

Windows Docker Desktop 上での live smoke は **サポート対象外** です。
WSL 経由の `bash scripts/release-smoke.sh` も、Docker context の不一致リスクがあるため gate には使用しません。

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

## 設定（環境変数）

| 変数 | 既定 | 用途 |
|------|------|------|
| `MAILER_IMAGE_TAG` | （必須・`MAILER_IMAGE_DIGEST` と排他） | 検証する Mailer イメージ tag（`latest` 不可） |
| `MAILER_IMAGE_DIGEST` | （必須・`MAILER_IMAGE_TAG` と排他） | 検証する Mailer イメージ digest（`sha256:<64-lowercase-hex>`） |
| `MAILER_IMAGE_REPOSITORY` | `ghcr.io/kooiei-in4a/amane-mailer` | イメージ repository |
| `MAILER_IMAGE_PLATFORM` | `linux/amd64` | smoke 対象の Mailer runtime image platform |
| `MAILER_PULL_POLICY` | `always` | ローカルイメージを使う場合は `missing` |
| `MAILPIT_IMAGE` | `axllent/mailpit:latest` | Mailpit helper image。既定の `latest` は意図的です。 |
| `MAILER_HTTP_PORT` | `15280` | Mailer の host port |
| `MAILPIT_HTTP_PORT` | `18025` | Mailpit API/UI の host port |
| `MAIL_SERVICE_TOKEN` | `local-mail-service-token` | example tenant の token |
| `RELEASE_SMOKE_PROJECT` | `amane-mailer-release-smoke` | compose project 名 |
| `RELEASE_SMOKE_KEEP` | （未設定） | `1` で終了時の cleanup を skip（デバッグ用） |

別 tag を検証する例:

```bash
MAILER_IMAGE_TAG=sha-<git-sha> bash scripts/release-smoke.sh
```

Mailpit は release artifact に含まれない smoke helper です。`latest` の扱いと固定が必要な場合の
手順は [container image pinning policy](container-image-pinning.md) を参照してください。

## 記録済み smoke 結果

`v1.3.7` の value-free smoke 結果（digest、日付、環境、各 check の pass/fail）は
[docs/releases/v1.3.7.md](../releases/v1.3.7.md) に記録します。過去の `v1.2.0` 結果は
[docs/releases/v1.2.0.md](../releases/v1.2.0.md)、`v1.1.0` 結果は
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

- `scripts/release-smoke.sh`: **対象 release image** の HTTP / 冪等性 / Mailpit delivery を clean state から一括検証する **サポート対象** release smoke gate。host 側 HTTP クライアント（curl）のみで完結します。
- `scripts/release-smoke.ps1`: shell 版と同一 contract を保つ PowerShell 実装。**release gate としてはサポート対象外**（contract parity は Linux 上の self-test で検証）。Windows Docker Desktop live smoke は要求しません。
- `scripts/native-aot-path-smoke.sh`: CI の `Native AOT publish smoke` が publish した
  **linux-x64 Native AOT binary** に対し、Admin login・HTTPS webhook tenant の `/readyz`・
  `db backup` など低頻度 path を black-box 検証します（issue #286）。release image や
  Mailpit 配送は対象外です。ACS live は secret 依存のため手動のままです。
- `infra/deploy/drills/mail-05a-*`: deploy host 上の稼働中 compose stack に対する
  no-send / ACS deploy drill。SQLite Mailer CLI（`healthcheck`、`db stats`、`db request-state`）と
  一時的な curl compose client を使い、worker 無効化や DB 状態確認まで踏み込みます。
  詳細は [docs/ops/drills/mail-05a-drill-guide.html](drills/mail-05a-drill-guide.html) を参照してください。
