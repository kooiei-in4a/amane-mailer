# ACS Delivery Report Queue arrival E2E CLI runbook

> 対象: `setup verify-delivery-report`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)
> 正本: [ADR 0020](../adr/0020-bounce-ingestion-and-suppression.md)、[セットアップ入口](setup-guide.md)、[#426 test-acs-send](test-acs-send-cli-runbook.md)、[#427 check-event-grid](event-grid-config-check-runbook.md)

## 1. 目的

Staging で正常なテストメールを ACS から送信し、その message ID に対応する `Microsoft.Communication.EmailDeliveryReportReceived` が Event Grid 経由で Storage Queue へ到着することを、**read-only peek** で確認する。

- **Staging 限定**。Production での実送信・Queue 確認は対象外。
- 実バウンス、無効宛先、suppression 登録・解除は要求しない。
- Mailer の `provider_event_inbox` 取り込み確認は対象外。

## 2. 安全境界

- 実行前に exact `Staging` と固定 phrase `MAILER-VERIFY-DELIVERY-REPORT` を要求する。
- ACS / Queue の connection string、sender、recipient を command-line argument で受け取らない。
- Queue に対して **Peek / GetProperties のみ**。Receive・Delete・visibility 変更はしない。
- message ID、recipient、sender、subject、body、raw event JSON、provider raw error、connection string を標準出力・ログへ出さない。
- Azure resource / Event Grid subscription / Queue / RBAC を作成・変更・削除しない。

## 3. 前提

1. [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) `setup check-event-grid ... --environment staging` が exit `0`（WARN/ACTION は許容、FAIL なし）。
2. Staging ACS の承認済み送信元と、専用のテスト用送信先。
3. 専用または滞留の少ない Staging Queue。Mailer bounce poller が同一 Queue を消費している場合は、競合を避けるため poller を一時停止するか専用 Queue を使う。
4. ACS secret file（`ACS_CONNECTION_STRING_FILE` または `MAILER_ACS_SECRET_DIRECTORY/acs_connection_string`）を推奨。
5. Queue connection string file（`MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE`）と Queue 名（`MAILER_BOUNCE_QUEUE_NAME`）を推奨。

## 4. 実行

実 TTY が必要。redirected stdin / compose `-T` は拒否される。

```bash
export ACS_CONNECTION_STRING_FILE=/path/to/acs_connection_string
export MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE=/path/to/queue_connection_string
export MAILER_BOUNCE_QUEUE_NAME=staging-acs-delivery-reports
# optional (defaults: timeout 180s, poll 5s; caps: timeout 30-600, poll 1-30)
export MAILER_VERIFY_DELIVERY_REPORT_TIMEOUT_SECONDS=180
export MAILER_VERIFY_DELIVERY_REPORT_POLL_INTERVAL_SECONDS=5

dotnet Amane.Mailer.dll setup verify-delivery-report
```

対話入力:

1. Environment: `Staging`（完全一致。Ctrl+C -> exit `2`）
2. Intent: `MAILER-VERIFY-DELIVERY-REPORT`
3. ACS connection string（secret file が無い場合のみ、非表示・二重入力）
4. Sender / Recipient（非表示）
5. Queue connection string（file/env が無い場合のみ、非表示・二重入力）
6. Queue 名（env が無い場合のみ、表示入力）。`prod` / `production` を含む明らかな production 名は拒否

送信内容は #426 と同じ固定 synthetic subject / text body。ACS 送信は `IAcsTestSendClient` を再利用する。

## 5. 結果の読み方

値そのものは表示されない。

```text
[PASS] ACS authentication
[PASS] Send request accepted
[PASS] ACS send operation completed
[PASS] Delivery Report observed in Storage Queue
[PASS] Event correlated to the test send
[PASS|FAIL|WARN] Delivery status classified
[ACTION] Confirm receipt in the test mailbox
success: operation=verify_delivery_report result=SUCCESS
```

| 判定 | 意味 |
|------|------|
| ACS send operation | ACS が送信操作を完了したか（#426 相当） |
| Delivery Report observed | Queue に Delivery Report が見えたか |
| Event correlated | 送信した message ID と一致したか（正規化なし） |
| Delivery status classified | `Delivered` -> PASS。`Failed` / `Bounced` -> FAIL。その他 -> WARN。配線 PASS と独立 |
| mailbox ACTION | 受信箱到着は人手確認 |

配線が成立していれば、配送 status が `Failed` でも exit `0`（配線 PASS・配送 FAIL として報告）。

timeout 時は ACS PASS と Event Grid 未確認を分離する。

Queue backlog が peek 上限（32）を超え、対象を確認できない場合は WARN / ACTION とし、誤って PASS にしない。

## 6. 終了コード

| Code | 意味 |
|------|------|
| `0` | ACS 送信完了かつ Delivery Report 相関成功（配線確認） |
| `1` | ACS 失敗、Queue 認証失敗、timeout、backlog により確認不能など |
| `2` | Staging / phrase / 入力拒否、prompt 中の Ctrl+C |
| `130` | ACS / Queue I/O 中の協調 cancel |

## 7. 非目標

- 実バウンス発生
- suppression 登録・解除
- Mailer inbox 取り込み
- Production 実行
- Event Grid / Queue の作成・修正
- Queue message の削除
- raw event evidence の保存

## 8. 関連

- [English](verify-delivery-report-runbook.en.md)
- [test-acs-send-cli-runbook.md](test-acs-send-cli-runbook.md)
- [event-grid-config-check-runbook.md](event-grid-config-check-runbook.md)
- [bounce-ingestion-runbook.md](bounce-ingestion-runbook.md)
