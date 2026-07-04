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

`provider` は通常 tenant JSON の値を使います。`MAILER_PROVIDER` または .NET 環境変数形式の
`Mailer__Provider`（config key は `Mailer:Provider`）を設定した場合は、全 tenant の provider をその値で上書きします。

deploy 固有の tenant ファイルは、デプロイ前にコンテナへ mount し、`tenants.schema.json` で検証してください。Docker イメージに含まれるのは安全な example と schema のみです。
ローカル検証用ファイルには、schema に新しい environment 値を意図的に追加しない限り `develop` を使ってください。

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
`live_sending=true` の場合の `ACS_CONNECTION_STRING`、Mailpit SMTP host / port の設定方針を確認します。
この preflight は現在の shell environment を対象にし、`appsettings*.json` は読み込みません。

共有 deploy テンプレート（`tenants.shared.example.json`）には 3 tenant — `example-develop`、`example-staging`、`example-production` — が含まれ、それぞれ別の `token_env` を持ちます。このファイルをコピーし、tenant 名をサービスに合わせて変更し、プレースホルダーを実値に置き換え、deploy ディレクトリで `tenants.json` として mount してください。

ローカル・テスト tenant では `live_sending=false` を使います。`provider=acs` でも `live_sending=false` の tenant は実送信しません。承認済み live sender の場合のみ、実効 provider を `acs` にし、`live_sending=true` と `ACS_CONNECTION_STRING` を設定してください。
