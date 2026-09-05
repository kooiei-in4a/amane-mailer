# Python Consumer smoke client

`send_mail.py` は、Amane Mailer v2 Consumer API を VPS または local 環境から確認する
公式の stdlib-only Python smoke client です。依頼を 1 件 POST し、`GET
/api/mail-requests/{id}` を bounded polling して、`delivered` のときだけ exit code
`0` を返します。Python package や production SDK ではありません。

## 前提

- Python 3.x（標準ライブラリだけを使用）
- 起動済みの Mailer v2 と、対象 Sender に紐づく managed API Key
- 承認済みの宛先（実送信を行う場合）

VPS の fresh deployment と Managed v2 初回 setup は、[VPS dogfood smoke checklist](../../docs/ops/vps-dogfood-smoke.md)
と [VPS dogfood deployment (PR1)](../../docs/ops/vps-dogfood-deployment.md) を順に参照してください。
local Mailpit の起動方法は、root README の [Mailpit で起動する](../../README.md#mailpit-で起動する)
を参照します。

## Install → setup → send

追加の install は不要です。次の順序で使います。

1. Mailer を起動し、既存の [Managed v2 初回ブラウザセットアップ](../../docs/ops/setup-guide.md#初回ブラウザセットアップmanaged-v2)
   （bootstrap 認証 → ACS provider secret → 最初の Admin → 最初の Sender → finalize）を完了する。
2. `/admin/senders` で対象 Sender を作成または確認し、その Sender の API Key を作成する。
   API Key の plaintext は作成直後に一度だけ表示されるため、secret manager または環境変数へ
   安全に渡し、shell history・ログ・Issue・チャットには貼らない。
3. 宛先を明示し、client を実行する。API Key は `MAILER_API_KEY` から読むか、TTY では非表示
   prompt に入力する。`--api-key` のような secret CLI 引数はありません。

```bash
export MAILER_BASE_URL='https://mailer.example.invalid/'
export MAILER_RECIPIENT_EMAIL='approved-recipient@example.invalid'

# MAILER_API_KEY を設定しない場合は、TTY の hidden prompt が表示されます。
python examples/consumer-python/send_mail.py \
  --subject 'Amane Mailer smoke' \
  --body 'Intentional operator smoke request.'
```

非対話実行では secret manager から `MAILER_API_KEY` を process environment に注入します。
client の process argv に API Key を置かないでください。上の `.invalid` の値は文書用のため、
実送信時は operator が承認済みの実値を安全な経路から指定します。

PowerShell の公式 client は [scripts/smoke/send-mail.ps1](../../scripts/smoke/send-mail.ps1) です。
Python と同じ v2 body、status polling、終了条件を持ち、`MAILER_API_KEY` または hidden prompt
を使います。

アプリへ組み込む場合は、この operator client をコピーせず、[Consumer SDK](../../sdk/README.md)
または [OpenAPI contract](../../docs/api/openapi.yaml) を使って同じ v2 契約を実装してください。
managed API Key は application の secret manager から注入し、この client は手動の VPS smoke に
限定します。

## 設定

CLI option は同名の環境変数より優先します。API Key に対応する設定は環境変数または prompt
だけで、CLI option ではありません。

| 設定 | 既定値 / 必須 | 用途 |
|---|---|---|
| `MAILER_BASE_URL` / `--base-url` | `http://127.0.0.1:5280/` | Mailer の HTTP(S) base URL |
| `MAILER_API_KEY` | 必須（TTY では hidden prompt） | 1 Sender を選択する managed API Key |
| `MAILER_RECIPIENT_EMAIL` / `--recipient` | 必須 | 宛先。`--to` も利用可能 |
| `MAILER_SUBJECT` / `--subject` | `Amane Mailer smoke` | 件名 |
| `MAILER_TEXT_BODY` / `--body` | `Amane Mailer smoke request.` | plain-text 本文 |
| `MAILER_PURPOSE` / `--purpose` | `SmokeTest` | v2 purpose |
| `--request-id` | 未指定時は random UUID | idempotency / conflict rehearsal 用 |
| `MAILER_TIMEOUT_SECONDS` / `--timeout-seconds` | `10` | 各 HTTP request の timeout |
| `MAILER_POLL_TIMEOUT_SECONDS` / `--poll-timeout-seconds` | `30` | status polling 全体の上限 |
| `MAILER_POLL_INTERVAL_SECONDS` / `--poll-interval-seconds` | `1` | poll 間隔。`0` も可 |

通常の実行では新しい UUID を自動生成します。同じ request を意図的に再送する場合だけ
`--request-id` を指定してください。既存 ID と payload が異なる場合は `409
IDEMPOTENCY_CONFLICT` で終了します。

## API と終了条件

送信する JSON は v2 の次のフィールドだけです。

```json
{
  "mail_request_id": "<random-uuid>",
  "purpose": "SmokeTest",
  "to": [{"email": "<approved-recipient>"}],
  "subject": "<subject>",
  "text_body": "<plain-text-body>"
}
```

`POST` の `202 Accepted` と `accepted` / `already_accepted` は、Mailer が依頼を受理した
ことを表すだけです。その後、同じ managed API Key で status を照会します。

- `queued` / `processing`: deadline まで polling を続ける
- `delivered`: 成功、exit `0`
- `failed` / `dead_lettered` / `cancelled` / `delivery_unknown`: 終端だが失敗、exit `1`
- timeout、401/403/404/409/429/503、redirect、未知の status: 安全な status/code だけを表示し、exit `1`

エラー response の raw body、recipient、subject、body、API Key、Authorization header は
表示しません。`delivery_unknown` は「未送信」とは限らず、同じ `mail_request_id` を再送して
よい状態でもありません。重複リスクを評価したうえで、新しい UUID の業務上の再送を判断します。

この client は `tenant_id`、`source_service`、`payload_hash`、`From`、
`MAIL_SERVICE_TOKEN*` を受け付けず、Sender は API Key によって選択されます。

## local/no-send proof

実 ACS や実 recipient を使わない fixture proof は、repository root から次で実行できます。

```bash
PYTHONDONTWRITEBYTECODE=1 python3 scripts/smoke/test_send_mail.py
```

この self-test は temporary local HTTP fixture に対して v2 POST、Authorization、random UUID、
`queued` → `processing` → `delivered` polling、bounded timeout、401/409/429 の safe diagnostics、
secret/PII redaction を確認します。PowerShell 側は
`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/smoke/send-mail-self-test.ps1`
で同様の no-send fixture を実行します。どちらも ACS、Mailpit、VPS、Docker を必要としません。

## Out of scope

Sender 作成、API Key 作成・revoke、Admin login、Docker/Compose 操作、VPS provisioning、
DNS/TLS、backup/restore、実 ACS send は client の責務ではありません。後者を含む再現可能な
手順は [VPS dogfood smoke checklist](../../docs/ops/vps-dogfood-smoke.md) に分離しています。
