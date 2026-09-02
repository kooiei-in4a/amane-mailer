[English](README.en.md)

# Mailer 設定

スキーマ:

- `tenants.schema.json`

サンプル:

- `tenants.example.json`（単一 tenant・ローカル Mailpit 既定）
- `tenants.shared.example.json`（3 tenant 共有 deploy テンプレート）
- `tenants.local-acs.json.example`（単一 tenant・ACS 実送信）

tenant ファイルの選択:

```text
Mailer:TenantsPath または MAILER_TENANTS_PATH で指定した 1 つの JSON
未指定時は config/mailer/tenants.example.json
```

tenant JSON は階層 merge しません。環境別 JSON を使う場合は、使いたいファイルを
`Mailer:TenantsPath` または `MAILER_TENANTS_PATH` で明示してください。

tenant Bearer トークンなどの秘密情報は JSON に保存しません。JSON には `token_env` で環境変数名を記載し、実際の token 値はその環境変数に設定します。

optional `webhook` オブジェクトで配送結果の outbound webhook を有効にできます。
`url` と `secret_env`（秘密は環境変数。tenant JSON 平文禁止）を設定します。詳細は
[webhook verification](../../docs/consumer/webhook-verification.md) と
`tenants.schema.json` の `webhook` 定義を参照してください。

`provider` は通常 tenant JSON の値を使います。`MAILER_PROVIDER` または .NET 環境変数形式の
`Mailer__Provider`（config key は `Mailer:Provider`）を設定した場合は、全 tenant の provider をその値で上書きします。

deploy 固有の tenant ファイルは、デプロイ前にコンテナへ mount し、`tenants.schema.json` で検証してください。Docker イメージに含まれるのは安全な example と schema のみです。
ローカル検証用ファイルには、schema に新しい environment 値を意図的に追加しない限り `develop` を使ってください。

## Platform-owned sender（`platform-sender.json`）

`platform-sender.json` / `platform-sender.schema.json` は、System Admin確認メールなど tenant に
属さない platform-owned mail のsender情報（email・display name）専用の設定形式です。tenant JSON
とは完全に独立しており、`tenant_id` / `source_services` / `token_env` などtenant固有の概念は
一切持ちません。

- schema: `platform-sender.schema.json`（`environment` は現時点 `staging` のみ、`provider` は
  `acs` のみ、`live_sending` は常に `false` を要求する）
- サンプル: `platform-sender.example.json`
- 登録方法: `admin provider register-acs` CLI（対話入力のみ。command argument や環境変数では
  秘密値を受け取らない）。詳細は [register-acs CLI runbook](../../docs/ops/register-acs-cli-runbook.md)
  を参照

このファイルは書き込み時点では runtime のどの送信経路からも読み込まれません（MAILER-ACS-INPUT-01
のscope）。実際にSystem Admin確認メールの送信判断へ組み込むのは、正式なplatform-owned mail
request契約を確立する別taskの責務です。既存tenantへの割当てや偽tenantの作成は行いません。

## Preflight

起動前に、tenant JSON と現在の shell 環境変数を preflight できます。secret 値そのものは
stdout / stderr に出しません。

```bash
MAIL_SERVICE_TOKEN=local-mail-service-token \
  scripts/validate-tenant-config.sh config/mailer/tenants.example.json
```

deploy 用 `infra/deploy/tenants.json` を確認する場合は、deploy `.env` の値を読み込んだ
bash session で実行してください。

```bash
set -a
. infra/deploy/.env
set +a
scripts/validate-tenant-config.sh infra/deploy/tenants.json
```

この preflight は `tenants.schema.json` に沿った shape、`tenant_id` 重複、
`source_services` の空・重複、`token_env` の env 存在、placeholder らしい token 値、
実効 provider（`MAILER_PROVIDER` / `Mailer__Provider` override 含む）が `acs` かつ
`live_sending=true` の場合の ACS secret（`ACS_CONNECTION_STRING_FILE` または
`ACS_CONNECTION_STRING`）、Mailpit SMTP host / port の設定方針を確認します。
この preflight は現在の shell environment を対象にし、`appsettings*.json` は読み込みません。
`Development` / `Testing` 以外では、tenant authentication token が既知 placeholder に
解決される場合、Mailer runtime 起動も fail-closed します（判定 predicate は `setup doctor` と
この preflight script と同一。runtime enforcement は `Development` / `Testing` のみ緩和され、
`setup doctor` はそれらの environment でも placeholder を報告し得ます）。
`Testing` は test host 向けの特権的 environment 名であり、production deployment では使用しません。
preflight validator の追加背景は [#150](https://github.com/kooiei-in4a/amane-mailer/issues/150) を参照してください。

## Tenant / env troubleshooting

tenant JSON と環境変数のずれが疑われる場合は、まず同じ shell で `scripts/validate-tenant-config.sh <tenants.json>` を実行し、Mailer 起動時と同じ `MAILER_TENANTS_PATH`、`MAILER_PROVIDER` / `Mailer__Provider`、`MAIL_SERVICE_TOKEN_*`、ACS secret（Staging/Production deploy は `ACS_CONNECTION_STRING_FILE`、local/drill は `ACS_CONNECTION_STRING`）、Mailpit SMTP 設定を見ます。secret 値そのものは docs や issue に貼らず、`replace-with-*` や `local-mail-service-token` のような placeholder だけを使ってください。

| 症状 | 見るべき設定 | 安全な修正方針 |
|------|--------------|----------------|
| `401 UNAUTHORIZED_TENANT` | リクエストの `tenant_id`、Bearer token、tenant JSON の `tenant_id`、`token_env`、その環境変数の存在 | 正しい tenant 用 token を環境変数に設定し、リクエストの `tenant_id` と token の組み合わせを合わせます。token 実値は JSON やログに書かないでください。 |
| `403 SOURCE_SERVICE_NOT_ALLOWED` | リクエストの `source_service`、tenant JSON の `source_services` allowlist | 呼び出し元の正式な `source_service` 名を allowlist に追加するか、リクエスト側を登録済み名に直します。大文字小文字や `-` / `_` の違いも確認します。 |
| `LIVE_SENDING_DISABLED` | tenant JSON の `provider`、`live_sending`、実効 provider（`MAILER_PROVIDER` / `Mailer__Provider` override 後） | local / staging では通常 `live_sending=false` のままにします。承認済み production sender だけ `provider=acs`、`live_sending=true`、登録済み ACS secret をそろえます。 |
| provider 設定不足 | 実効 provider、ACS secret（`ACS_CONNECTION_STRING_FILE` または `ACS_CONNECTION_STRING`）、Mailpit SMTP host / port | Staging/Production deploy で `provider=acs` かつ `live_sending=true` なら `admin provider register-acs` で file secret を登録します（register-acs runbook 参照）。local ACS drill は bare `ACS_CONNECTION_STRING` を使えます。Mailpit なら Mailpit SMTP の host / port がコンテナから到達できる値か確認します。 |
| Mailpit に届かない | `MAILER_PROVIDER` / `Mailer__Provider`、tenant JSON の `provider`、`live_sending`、Mailpit SMTP host / port、Worker が起動しているか | local smoke では `MAILER_PROVIDER=mailpit` を明示し、Mailpit UI / API の port と SMTP port を取り違えていないか確認します。Worker 配送に数秒かかるため、少し待ってから再確認します。 |
| tenant JSON path の指定ミス | `MAILER_TENANTS_PATH` / `Mailer:TenantsPath`、Docker mount path、起動ログ、preflight に渡した file path | 起動時に使う JSON と preflight した JSON を同じ path にします。deploy では host 側ファイルを read-only mount し、コンテナ内 path を `MAILER_TENANTS_PATH` に指定します。 |
| `MAILER_PROVIDER` override による想定外 provider | `MAILER_PROVIDER`、`Mailer__Provider`、tenant JSON の `provider`、preflight の実効 provider 表示 | override は全 tenant の provider を上書きします。local smoke 後や ACS drill 後は不要な override を解除し、tenant JSON の provider と意図が一致するか確認します。 |

`live_sending` は fail-closed の実送信ゲートです。環境ごとの基本方針は次の通りです。

| 環境 | 推奨方針 |
|------|----------|
| local / test | `provider=mailpit` または `MAILER_PROVIDER=mailpit`、`live_sending=false`。実メールを送らず Mailpit で確認します。 |
| staging | 原則 `live_sending=false`。ACS 接続や sender 検証を短時間だけ確認する場合は、専用 tenant / 宛先 / 手順に限定して一時的に有効化します。 |
| production | 承認済み ACS sender だけ `provider=acs`、`live_sending=true`、`admin provider register-acs` で登録した file-based ACS secret を設定します。production tenant と non-production tenant の token / `tenant_id` は分けます。 |

共有 deploy テンプレート（`tenants.shared.example.json`）には 3 tenant — `example-develop`、`example-staging`、`example-production` — が含まれ、それぞれ別の `token_env` を持ちます。このファイルをコピーし、tenant 名をサービスに合わせて変更し、プレースホルダーを実値に置き換え、deploy ディレクトリで `tenants.json` として mount してください。

ローカル・テスト tenant では `live_sending=false` を使います。`provider=acs` でも `live_sending=false` の tenant は実送信しません。承認済み live sender の場合のみ、実効 provider を `acs` にし、`live_sending=true` と登録済み ACS secret を設定してください。
