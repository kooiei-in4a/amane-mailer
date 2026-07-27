# Event Grid / Storage Queue 構成確認 CLI runbook

> 対象: `setup check-event-grid`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427)
> 正本: [ADR 0020](../adr/0020-bounce-ingestion-and-suppression.md)、[bounce ingestion runbook](bounce-ingestion-runbook.md)、[セットアップ入口](setup-guide.md)

## 1. 目的

ACS Email Delivery Report 用の Event Grid subscription と Storage Queue が、v1.1.0 の Pull 型 bounce ingestion 前提に沿っているかを、**Azure リソースを変更せず**に確認する。

このコマンドは構成の照会のみを行う。Delivery Report の実到着や Queue message 本文の確認は対象外（到着確認は [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) / [verify-delivery-report-runbook.md](verify-delivery-report-runbook.md)）。

## 2. 採用方式

Mailer 本体に Azure Resource Manager SDK は追加しない。オペレーターの **Azure CLI ログイン済み session** に対し、allowlist された read-only 照会だけを実行する。

| 候補 | 判定 |
|------|------|
| Azure SDK (ARM) 追加 | 却下。Native AOT / trim、配布サイズ、credential chain、保守負担が増える |
| Azure CLI 呼び出し（採用） | 新規 NuGet なし。credential は `az login` 側。fixture / fake runner で CI 可能 |

Bash / PowerShell への中核ロジック二重実装はしない（判定は C# CLI 内）。

## 3. 事前準備

1. Azure CLI を PATH に入れる
2. `az login` 済みで、対象 subscription の ACS / Event Grid / Storage を**読める**権限がある
3. 秘密情報ではなく、リソースを一意に特定する名前 / ID を用意する

入力してはいけないもの:

- ACS connection string / access key
- Storage account key / connection string
- access token / Bearer
- 送信元・送信先 email

## 4. 実行

```bash
dotnet Amane.Mailer.dll setup check-event-grid \
  --subscription <id-or-name> \
  --resource-group <rg> \
  --acs-name <acs-name> \
  --event-subscription <subscription-name> \
  --storage-account <storage-account> \
  --queue-name <queue-name> \
  --environment <dev|staging|production>
```

`--acs-name` の代わりに `--acs-resource-id` を使える（どちらか一方）。

`--subscription` は各照会に付与するだけであり、`az account set` による既定 subscription の変更は行わない。

## 5. 結果の読み方

`setup doctor` と同じ語彙を使う。

| コード | 意味 |
|--------|------|
| `PASS` | 構成を照会して一致を確認した |
| `FAIL` | 対象不存在または明確な不一致 |
| `WARN` | RBAC / network / 到着など、機械では完全判定できない |
| `ACTION` | Portal / CLI で人手確認すべき項目 |

末尾に `Summary: PASS=… FAIL=… WARN=… ACTION=…` を出す。`FAIL` が 1 件でもあれば exit code `1`。usage 不正は `2`。

典型的な FAIL:

- Event subscription 不存在
- source ACS 不一致
- destination が Storage Queue ではない
- destination Queue / Storage 不一致
- `Microsoft.Communication.EmailDeliveryReportReceived` 不足（`All` のみは不可。明示必須）
- Push webhook destination（#304 は v1.1.0 の正常構成として扱わない）
- Cloud Events delivery schema
- environment 名の混線ヒューリスティック

典型的な WARN / ACTION（成功扱いにしない）:

- Event Grid → Queue の RBAC / managed identity
- Storage firewall / private endpoint
- Delivery Report の実到着（本コマンドの非対象）

## 6. 安全境界 / read-only 保証

- Azure リソースの create / update / delete は行わない
- role assignment、Event Grid subscription、Queue、network、firewall の自動修正は行わない
- Azure CLI 呼び出しは allowlist 済み read query のみ（`show` / `exists` / `version` / `account show`）
- raw JSON、raw error、access token、key、email は stdout / stderr に出さない
- resource ID の subscription GUID は `***` にマスクする

## 7. テストと実 Azure

- CI は fake Azure CLI runner / fixture JSON で自動テストする（実 Azure 認証は必須にしない）
- 実 Azure manual smoke は maintainer 管理。未実施なら PR に未実施理由と残存リスクを書く

## 8. 関連

- [bounce-ingestion-runbook.md](bounce-ingestion-runbook.md)
- [setup-guide.md](setup-guide.md)
- ADR 0020