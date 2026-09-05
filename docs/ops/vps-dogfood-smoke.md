[English](vps-dogfood-smoke.en.md)

# VPS dogfood smoke checklist（Issue #733 / PR2）

この checklist は、PR1 の VPS managed-v2 deployment 上で v2 Consumer API を確認するための
再現可能な運用手順です。公式 client は [Python smoke client](../../examples/consumer-python/README.md)
と [PowerShell smoke client](../../scripts/smoke/send-mail.ps1) の 2 つです。

この PR と CI は **実 ACS 送信を実行しません**。以下の A1/A2/B1 の send 手順は、承認された
operator が後日、目的・宛先・時間帯を明示して実行するための手順です。client の起動だけで
実送信を始める仕組み、Sender/API Key の自動作成、Admin login の自動化、Docker restart の
自動化はありません。

## 0. Go / no-go

実行前に次を確認します。

- [ ] Issue #733 の対象が PR2 smoke / dogfood であり、PR3 backup/restore の作業を混ぜない。
- [ ] real ACS send の目的、承認済み recipient、承認済み sender、実行時間、停止担当者を決めた。
- [ ] production では Mailer の公開 URL、管理 URL、operator CIDR、ACS environment が一致している。
- [ ] recipient、API Key、bootstrap token、ACS connection string、password をこの文書、shell history、
      Issue、chat、CI log に記録しない。
- [ ] 実行を取りやめる場合は client を起動せず、`live_sending` を有効にしない。

`A1 real send` などの表記は、この PR の実行結果を意味しません。実行済みの証跡を残す場合も、
記録するのは image digest、時刻、HTTP status/code、Mailer が表示する `mail_request_id`、
delivery status などの value-free な情報に限定します。宛先や message body は記録しません。

## 1. 前提と fresh VPS

- [ ] Docker Engine と Compose plugin（`!override` / `!reset` 対応）がある。
- [ ] DNS / TLS が設定され、host firewall は public API と operator-only management path を意図どおりに分離している。
- [ ] 検証済み immutable image tag または digest を決めた。未検証の `latest` は使わない。
- [ ] `MAILER_DATA_PATH` は永続 directory、ACS / bounce secret directory は mode `0700` である。
- [ ] fresh state で `tenants.json`、`MAIL_SERVICE_TOKEN*`、legacy `MAILER_PROVIDER` を作成していない。

PR1 の security boundary、固定 proxy network、Mailer port 非公開、management CIDR の設定は
[VPS dogfood deployment (PR1)](vps-dogfood-deployment.md) を正本とします。`down -v` は Mailer DB と
Caddy certificate state を削除し得るため、この手順でも使いません。

## 2. Deploy / migration / bootstrap setup

1. PR1 runbook の `infra/deploy/.env.vps-dogfood.example` と Caddyfile から deploy host 用の
   未コミット設定を作り、image、hostname、operator CIDR、data path、protected secret path を確認する。
2. rendered Compose に Mailer の host `8080` publish、legacy tenant mount、`MAIL_SERVICE_TOKEN*`、
   `MAILER_PROVIDER` がないことを、secret の値を表示せず確認する。
3. profile を明示して config、migration、起動を実行する。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d
```

4. `/healthz` と `/readyz` を確認する。fresh state で setup 前の `/readyz` `503` は期待値です。
5. bootstrap token は container 内から一度だけ表示し、TTY で browser に入力する。値をコピーして
   shell history、log、Issue、chat、CI artifact に残さない。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

6. operator-only HTTPS の `/setup` で、次の順序を完了する。

   `bootstrap 認証 → ACS provider secret → 最初の Admin → 最初の Sender → finalize`

   ACS connection string はフォームの password 欄に入力し、環境変数・URL・CLI 引数へ置かない。
   finalize は永続 managed state を確定する。Mailer を再起動し、`/readyz` が ready になることを確認する。

7. setup 後、instance owner として `/admin/ops` の `Provider preflight` が
   `configured / safe` であること、`live_sending` が `disabled` であることを確認する。
   `/readyz` とこの Admin 表示が、VPS managed-v2 の tenant runtime に対する canonical preflight です。
   `setup doctor --mode production-acs` は tenants.json を使う legacy / setup bundle mode の
   検査であり、tenant JSON を持たない managed-v2 fresh VPS の送信証跡には使いません。

`admin provider register-acs` は別の platform-owned sender registration 経路です。tenant Sender の
runtime ACS secret を `/setup/provider` で登録する手順の代替ではありません。利用する場合は
[register-acs CLI runbook](register-acs-cli-runbook.md) の exact `Production` 確認と preflight を守り、
secret を CLI 引数に渡しません。この command の成功だけで tenant の real send が検証済みになったとは
扱いません。

## 3. Sender / API Key の用意

管理画面の `/admin/senders` で、first-run setup が作った最初の Sender を **Sender A** とし、別の
**Sender B** を作成します。Sender の email は ACS で承認済みのものを使います。

| 対象 | Admin 操作 | 目的 |
|---|---|---|
| Sender A | 作成済みの first Sender を確認 | A1/A2 の owner |
| Key A1 | A の detail で API Key 名 `A1` を作成 | revoke 対象 |
| Key A2 | A の detail で API Key 名 `A2` を作成 | revoke 後の継続確認 |
| Sender B | `/admin/senders` で別 Sender を作成 | B1 の owner |
| Key B1 | B の detail で API Key 名 `B1` を作成 | Sender identity 分離確認 |

- [ ] Sender A/B が enabled である。
- [ ] API Key はそれぞれ作成直後に表示される plaintext を一度だけ安全に保存した。
- [ ] plaintext を Admin list、log、ticket、chat、shell history に貼っていない。Admin は後から key を再表示しない。
- [ ] `live_sending` は provider preflight と承認内容を確認するまで disabled のままである。

## 4. 明示的な live sending と公式 client

実 ACS send を行う場合だけ、Admin instance owner が `/admin/ops` の `Live sending` を確認し、
provider preflight が safe であることを確認したうえで、明示確認付きで enable します。これは setup
完了の証明ではなく、実送信を許可する gate です。実行終了後または中止時には、組織の手順に従って
disabled に戻します。

### Python

Python は追加 package 不要です。API Key を `MAILER_API_KEY` に置かない場合は、TTY の hidden prompt
が表示されます。非対話 runner では secret manager が environment に注入します。以下の値は文書用
placeholder であり、real run では承認済みの値を安全な経路から指定します。

```bash
export MAILER_BASE_URL='https://mailer.example.invalid/'
export MAILER_RECIPIENT_EMAIL='approved-recipient@example.invalid'
export MAILER_SUBJECT='Amane Mailer intentional smoke'
export MAILER_TEXT_BODY='Intentional operator smoke request.'

# MAILER_API_KEY は hidden prompt または secret-manager injection を使う。
python3 examples/consumer-python/send_mail.py
```

### PowerShell

PowerShell 5.1+ または 7+ で実行します。API Key を parameter に渡すことはできません。
`MAILER_API_KEY` が未設定なら `Read-Host -AsSecureString` の hidden prompt になります。

```powershell
$env:MAILER_BASE_URL = 'https://mailer.example.invalid/'
$env:MAILER_RECIPIENT_EMAIL = 'approved-recipient@example.invalid'
$env:MAILER_SUBJECT = 'Amane Mailer intentional smoke'
$env:MAILER_TEXT_BODY = 'Intentional operator smoke request.'

# MAILER_API_KEY は hidden prompt または secret-manager injection を使う。
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke\send-mail.ps1
```

各 invocation は、`--request-id` / `-RequestId` を指定しない限り新しい UUID を生成します。same
request の意図的な idempotent retry / conflict rehearsal 以外で ID を固定しません。

client の判定は次の通りです。

- `POST 202` の `accepted` / `already_accepted` は受理だけを示す。成功判定は行わない。
- `queued` / `processing` は bounded deadline まで GET polling する。
- `delivered` のみ exit `0`。`failed`、`dead_lettered`、`cancelled`、`delivery_unknown` は終端だが exit `1`。
- 401/403/404/409/429/503、timeout、redirect、未知の status は exit `1`。表示は HTTP status と安全な error code だけ。
- `delivery_unknown` は未送信の証拠ではない。同じ ID を再送せず、重複リスクを評価して新しい ID の業務再送を判断する。

### A1 / A2 / B1 の実行順

1. `MAILER_API_KEY` を A1 にし、Python または PowerShell で 1 回実行する。`202` の後、status が
   `delivered` になることを確認する。
2. A1 を process environment から安全に差し替え、A2 で新しい UUID の send を 1 回実行し、`delivered` を確認する。
3. B1 で新しい UUID の send を 1 回実行し、`delivered` を確認する。
4. `/admin/mail-requests` で Admin が instance-wide に A1/A2/B1 の request と delivery status を
   確認できることを、recipient/body を証跡へコピーせず確認する。管理画面の表示は sender ownership
   と operational visibility の確認に使い、API Key の代わりにはしない。

## 5. Revoke / isolation

1. `/admin/senders` → Sender A → Key A1 で、revoke の不可逆確認を行う。
2. A1 を使って **新しい UUID** で client を実行する。期待結果は `HTTP 401` / `UNAUTHORIZED`、
   exit `1`。同じ ID を使って「revoke の確認」をしない。
3. A2 で新しい UUID の send を実行し、`delivered` になることを確認する。
4. B1 で新しい UUID の send を実行し、`delivered` になることを確認する。
5. A1 の 401 が A2/B1 を無効化しないこと、Sender A/B の request ownership が混ざらないことを Admin
   の instance-wide view で確認する。

## 6. Restart / persistence

実送信が完了した後、または中止して `live_sending` を disabled に戻した後に、データ削除を伴わない
Mailer restart を行います。

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood restart mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps
```

- [ ] `/readyz` が ready へ戻るまで待つ。
- [ ] Admin の Sender A/B、A2/B1 の active state、revoke 済み A1、`live_sending`、provider 設定が SQLite managed state として残っている。
- [ ] A2 で新しい UUID の send を行い、継続して `delivered` になることを確認する。
- [ ] B1 でも同じ確認を行う。
- [ ] `docker compose down -v`、volume 削除、DB 初期化を行わない。backup/restore の検証は PR3 へ残す。

## 7. Runtime rate-limit proof

新しい limiter は作りません。既存の runtime limiter の deterministic proof を、local/test で次の
既存テストから取得します。実 ACS は不要です。

```bash
dotnet test tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~SenderApiKeyIdentityTests.Authentication_attempt_limiter_rejects_after_fixed_window_budget'

dotnet test tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~FirstRunSetupTests.Setup_auth_rate_limits_repeated_invalid_tokens_at_http_endpoint'
```

期待結果は次の通りです。

- `/api/*`: 同一 remote IP の invalid/unknown API key を 20 回まで `401`、21 回目を `429` / `AUTHENTICATION_RATE_LIMITED` とする。
- `/setup` bootstrap auth: invalid bootstrap token を 20 回まで `401`、21 回目と正しい token の直後の試行を `429` とする。
- `/admin` の password login は別の SQLite-backed login throttle であり、同じ API limiter と混同しない。必要なら
  `MailerAdminSessionThrottleAuditTests.Login_throttle_survives_process_restart_simulation` も確認する。

実 VPS endpoint で 21 回の invalid request を行う場合は、real key を使わず、staging または明示承認済みの
maintenance window だけで行います。fixed window の間は同一 source IP の認証試行を抑制するため、production
の通常運用をこの確認で妨げないでください。`/setup` の HTTP 形は CSRF と bootstrap workflow session を
必要とするため、VPS では手作りの loop を proof とせず、上記の WebApplicationFactory test を canonical proof とします。

## 8. Secret / log exposure proof

以下を、値を証跡へ転記せずに確認します。

- [ ] **API Key plaintext**: client の stdout/stderr、通常の exception、timeout、401/409/429 表示、process argv に出ない。`--api-key` parameter はない。
- [ ] **Authorization header**: console、Admin UI、container log、CI output に出ない。client は内部 HTTP header にだけ設定する。
- [ ] **bootstrap token**: `setup bootstrap show` の表示を一時的な operator TTY だけで扱い、log / command argument / URL / artifact に保存しない。
- [ ] **provider secret**: `/setup/provider` または approved registration の hidden input / protected file だけで扱い、`.env`、tenant JSON、CLI argument、log に置かない。
- [ ] **recipient / subject / body**: real run では environment / secret-manager injection を優先し、CLI argv と証跡に置かない。Admin の PII view は必要な operator だけが見る。
- [ ] **container log**: run の前後に recent Mailer log を operator が画面上で確認し、secret/header/body がないことを確認する。raw log を Issue、chat、CI artifact、共有ファイルへ貼らない。
- [ ] **process argv**: run 中に `ps` または Windows の process inspection で command line を確認し、API Key、Authorization、bootstrap token、provider secret、real recipient がないことを確認する。environment の secret 値を別コマンドの引数へ展開しない。

自動 no-send proof は canary 値を fixture に送り、client の stdout/stderr に API Key、recipient、subject、
body が現れないこと、v2 field 以外（`tenant_id`、`source_service`、`payload_hash`）を送らないことを確認します。
実行コマンドは次の通りです。

```bash
PYTHONDONTWRITEBYTECODE=1 python3 scripts/smoke/test_send_mail.py
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/smoke/send-mail-self-test.ps1
```

## 9. Local / CI boundary

- [ ] CI の Python / PowerShell self-test は temporary local HTTP fixture のみを使い、ACS、Mailpit、VPS、
      production recipient、real API Key に接続しない。
- [ ] real ACS send の証跡が必要な場合は、この checklist の operator step を別の承認済み実行として記録する。
- [ ] `delivery_unknown` を成功、未送信、安全な retry と扱わない。
- [ ] backup、restore、volume migration、release、tag、Issue close はこの PR の完了条件ではない。

停止時の必須状態は、`live_sending` の意図が明確で、運用者が所有する secrets がログ等へ漏れておらず、
Mailer DB と Caddy volume を削除していないことです。
