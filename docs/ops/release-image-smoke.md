[English](release-image-smoke.en.md)

# 公開 release イメージの clean-state smoke

公開済みの GHCR ランタイムイメージ（既定 `ghcr.io/kooiei-in4a/amane-mailer:v0.1.1`）を
clean state から pull し、Mailer + Mailpit を起動して公開 release runtime path を自動 smoke します。

ローカル開発の `infra/docker/docker-compose.local.yml`（ソースから build）とは異なり、
この smoke は **公開済みイメージそのもの** を検証します。tenant 設定はイメージに同梱された
安全な example（`/app/config/mailer/tenants.example.json`）を使い、host の tenant JSON は mount しません。
Mailer の状態は named volume に置き、終了時に `docker compose down -v` で削除します。

## 前提

- Docker（compose plugin 同梱）が起動していること。
- `bash`、`curl`、`sha256sum` が使えること。
- GHCR イメージが pull できること（private の場合は事前に `docker login ghcr.io`。
  [GHCR image publish 手順](ghcr-image-publish.md) を参照）。
- 公開 Mailer runtime image は現時点では `linux/amd64` only です。ARM host では
  Docker Desktop などの amd64 emulation が使える場合のみ検証できます。multi-arch 対応は
  [#4](https://github.com/kooiei-in4a/amane-mailer/issues/4) で追跡しています。
- 既定の host port `15280`（Mailer）と `18025`（Mailpit）が空いていること。

## 実行

リポジトリ root で実行します。

```bash
bash scripts/release-smoke.sh
```

スクリプトは次を行います。

1. 残っていれば前回の smoke compose project を削除する。
2. 公開イメージと Mailpit を pull し、clean な project / named volume で起動する。
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
| `MAILER_IMAGE_TAG` | `v0.1.1` | 検証するタグ |
| `MAILER_IMAGE_PLATFORM` | `linux/amd64` | 公開 Mailer runtime image の platform |
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

Mailpit は release artifact に含まれない smoke helper です。`latest` の扱いと固定が必要な場合の
手順は [container image pinning policy](container-image-pinning.md) を参照してください。

## 記録済み smoke 結果

`v0.1.1` の value-free smoke 結果（digest、日付、環境、各 check の pass/fail）は
[docs/releases/v0.1.1.md](../releases/v0.1.1.md) に記録します。過去の `v0.1.0` 結果は
[docs/releases/v0.1.0.md](../releases/v0.1.0.md) を参照してください。

## deploy drill との使い分け

- `scripts/release-smoke.sh`: **公開イメージ** の HTTP / 冪等性 / Mailpit delivery を
  clean state から一括検証する release smoke。host 側 curl のみで完結します。
- `infra/deploy/drills/mail-05a-*`: deploy host 上の稼働中 compose stack に対する
  no-send / ACS deploy drill。SQLite Mailer CLI（`healthcheck`、`db stats`、`db request-state`）と
  一時的な curl compose client を使い、worker 無効化や DB 状態確認まで踏み込みます。
  詳細は [docs/ops/drills/mail-05a-drill-guide.html](drills/mail-05a-drill-guide.html) を参照してください。
