# ACS 単体実送信確認 CLI runbook

> 対象: `admin provider test-acs-send`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426)
> 関連: [register-acs CLI](register-acs-cli-runbook.md)、[セットアップ入口](setup-guide.md) mode 3

## 1. 目的

Mailer API / Worker / Event Grid / Storage Queue / bounce 処理を起動せず、ACS connection string・送信元・テスト用送信先だけを使って、ACS がテストメールを受理し send operation を完了できるかを one-shot で確認する。

- 初期 scope は **Staging 限定**。Production 実送信は対象外。
- 固定の synthetic subject / text body のみを送る（任意本文・添付・bulk は持たない）。
- ACS operation 成功と、受信 mailbox への到着は別判定（到着は人手 ACTION）。

## 2. 安全境界

- 実行前に環境確認（完全一致 `Staging`）と固定 phrase `MAILER-ACS-TEST-SEND` が必須。
- connection string / access key / 送信元 / 送信先を command-line argument で受け取らない。
- secret は既存 secret file を優先し、無い場合のみ実 TTY の非表示二重入力。
- secret、送信元、送信先、件名、本文、message ID、provider raw error を stdout / stderr / log へ出さない。
- DB、tenant JSON、`platform-sender.json`、既存 ACS secret は変更しない（secret は読取のみ）。
- provider 例外は分類・sanitize し、canonical result code だけを返す。

## 3. 事前準備

1. Staging ACS のテスト用 sender（承認済み Email Domain）とテスト用受信先を用意する。
2. 可能なら `admin provider register-acs` 済みの `acs_connection_string`、または deploy の `ACS_CONNECTION_STRING_FILE` を用意する。
3. message ID 引き渡し用の絶対パス（例: 専用ディレクトリ配下のファイル）を決める。環境変数 `MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE` に設定してもよい。
4. Production 資格情報・Production 宛先では実行しない。

## 4. 実行

実 TTY が必要。stdin redirect / `-T` 付き compose は拒否される。

```bash
# secret file を使う例（ACS_CONNECTION_STRING_FILE または MAILER_ACS_SECRET_DIRECTORY）
export ACS_CONNECTION_STRING_FILE=/path/to/acs_connection_string
export MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE=/path/to/acs-test-send-message-id.txt

dotnet Amane.Mailer.dll admin provider test-acs-send
```

対話手順:

1. 環境: `Staging`（完全一致）
2. 意図: `MAILER-ACS-TEST-SEND`
3. secret file が無い場合のみ ACS connection string を非表示で 2 回
4. sender email（bare）
5. recipient email（bare）
6. sender display name（任意・空でスキップ。wire 上は sender email を使用）
7. message ID handoff 絶対パス（env 未設定時）

## 5. 結果の読み方

成功例（値そのものは表示されない）:

```text
[PASS] ACS authentication
[PASS] Send request accepted
[PASS] ACS send operation completed
[PASS] Message ID handoff file written
[ACTION] Confirm receipt in the test mailbox
success: operation=test_acs_send result=SUCCESS
```

| 表示 | 意味 |
|------|------|
| `[PASS] ACS authentication` | ACS 資格情報で送信要求を開始できた |
| `[PASS] Send request accepted` | ACS が send request を受理した |
| `[PASS] ACS send operation completed` | ACS long-running operation が Succeeded |
| `[PASS] Message ID handoff file written` | provider message ID を handoff ファイルへ UUID のみ書き込み（内容は表示しない） |
| `[ACTION] Confirm receipt...` | mailbox 到着は人手確認 |

失敗時は `failed: operation=test_acs_send result=<CODE>` または `rejected: ...`。代表コード:

| Code | 意味 |
|------|------|
| `REJECTED_ENVIRONMENT_MISMATCH` | Staging 以外 |
| `REJECTED_INTENT_MISMATCH` | phrase 不一致 |
| `REJECTED_INPUT_REDIRECTED` | 非対話 stdin |
| `REJECTED_SECRET_MISMATCH` / `REJECTED_INVALID_CONNECTION_STRING` | secret 入力不備 |
| `FAILED_ACS_AUTHENTICATION` | 認証・資格情報失敗 |
| `FAILED_ACS_SENDER_REJECTED` | sender / domain 等による拒否 |
| `FAILED_ACS_SEND_REQUEST` | 送信要求失敗 |
| `FAILED_ACS_OPERATION` | LRO 完了だが Succeeded 以外 |
| `FAILED_ACS_TIMEOUT` | timeout / 一時的障害 |

終了コード: `0` 成功、`1` ACS 側失敗、`2` 入力・前提拒否、`130` Ctrl+C 協調 cancel。

## 6. message ID の引き渡し（#428 向け）

成功時、handoff ファイルへ **UUID 1 行だけ**を書き込む（email / subject / body / secret は含めない）。

- 後続の Delivery Report E2E（#428）は、このファイルまたは同一 `IAcsTestSendClient` 経路を再利用する想定。
- handoff ファイルを evidence や Issue / PR に貼らない。

## 7. 実PTY smoke（開発・CI向け）

実 ACS には接続しない。Linux 上で:

```bash
dotnet build src/Amane.Mailer/Amane.Mailer.csproj
python3 scripts/pty-smoke-test-acs-send.py
```

環境確認拒否、intent 拒否、TTY secret mismatch、redirected stdin 拒否、および出力への secret / email 非漏洩を確認する。

## 8. 記録してよいもの / 記録しないもの

記録してよい: command 名、Staging、canonical result code、PASS/FAIL/ACTION の別。

記録しない: connection string、access key、sender、recipient、subject、body、message ID、provider raw error、stack trace。

## 9. 非目標

- Mailer API / Worker 経由の送信確認
- Production 送信
- 任意 subject / body / attachment / bulk
- Event Grid / Queue / bounce 確認
- ACS リソースや Email Domain の作成・変更
- credential の保存・rotation
