[English](bounce-ingestion-runbook.en.md)

# バウンス取り込み runbook（Pull / Storage Queue）

> 対象: ACS Email Delivery Report → Event Grid → Storage Queue → Mailer Pull 取り込み（ADR 0020 / #305）
> Admin 可視化: #306。抑制解除 CLI: #400。Push（Event Grid Webhook）は v1.1.0 スコープ外（#304）。

## 1. 目的

ハードバウンス（ACS `status = Bounced`）を取り込み、テナント別 `mail_suppressions` に登録して再送を止める。
可視化は Admin UI と Prometheus メトリクスのみ（Consumer 向け `bounced` 通知は v1.2.0 以降）。

## 2. 採用トランスポート（Pull）

v1.1.0 は **Storage Queue ポーリング**のみ。公開 HTTPS 受信口は追加しない。

| 設定 | 例 |
|------|-----|
| モード | `MAILER_BOUNCE_INGESTION=queue`（または `Mailer:BounceIngestion:Mode=queue`） |
| 接続文字列 | `MAILER_BOUNCE_QUEUE_CONNECTION_STRING` または `MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE` |
| キュー名 | `MAILER_BOUNCE_QUEUE_NAME` |
| ポーリング間隔 | `Mailer:BounceIngestion:Queue:PollIntervalSeconds`（既定 30） |

接続文字列・キュー名をログやメトリクスに出さないこと。

### ACS / Event Grid 構成の要点

1. ACS Email の Delivery Report を Event Grid で購読する。
2. Event Grid のエンドポイントを **Storage Queue** にする（Push Webhook ではない）。
3. 環境（dev / staging / production）ごとに **ACS リソースと Queue を分離**する。混線すると別環境の `provider_message_id` と誤相関し得る。
4. Queue メッセージは生 JSON（Base64 ではない）。

## 3. Admin での確認

| 画面 | 内容 |
|------|------|
| `/admin/mail-requests/{id}` | リクエスト詳細の「バウンス履歴」。`bounce_events` を表示。FK は無いため、空でも正常（未取り込み / purge 済み）。 |
| `/admin/suppressions` | テナント別抑制リスト（閲覧のみ）。宛先は既定マスク。非マスクは `MAILER_ADMIN_PII_LIST_MODE=visible` の明示 opt-in。visible 時はテナント単位で閲覧・監査し、未選択時はテナント選択画面（許可テナントが1件なら自動リダイレクト）。 |

tenant scope を持つ管理者は自テナントのみ。break-glass のみ全テナント閲覧可。

`MAILER_ADMIN_PII_LIST_MODE=visible` のときは、非マスク一覧とその監査イベントはテナント単位です。サイドナビからはテナント選択画面に入ります（許可テナントが1件だけの場合は自動リダイレクト）。

## 4. メトリクスと滞留閾値

詳細とアラート例は [metrics-and-alerts.md](metrics-and-alerts.md)。

| メトリクス | 見るべきこと |
|------------|--------------|
| `mail_bounce_events_total` | 取り込みが進んでいるか |
| `mail_bounce_unmatched_total` | 相関失敗の増加 → 設計前提の崩れ |
| `mail_bounce_recipient_mismatch_total` | 宛先不一致破棄 |
| `mail_suppressed_sends_total` | 送信前ブロック発生 |
| `mail_provider_events_pending` | inbox 滞留（例: 50 超を 15m） |
| `mail_provider_events_dead_lettered` | inbox dead letter（0 超を警告） |
| `mail_provider_queue_poll_failed_total` | Queue ポーリング失敗 |

ラベルに `tenant_id` / 宛先を付けない（ADR 0020 D-10）。

CLI `db stats` の `provider_events_pending` / `provider_events_dead_lettered` も同じ inbox 集計。

## 5. unmatched 多発時の確認手順

1. `increase(mail_bounce_unmatched_total[30m])` が上昇しているか確認。
2. ACS → Event Grid → Queue の購読が正しい環境か確認（環境混線）。
3. 送信側 `mail_attempts.provider_message_id` が ACS `data.messageId` と一致する想定か確認（正規化なし・完全一致）。
4. Queue に古い他環境メッセージが残っていないか確認。
5. `mail_provider_events_dead_lettered` が増えていないか確認。増えていれば inbox 処理失敗。

ログ・Admin・DB にイベント raw JSON や宛先平文を無条件出力しない。

## 6. 抑制解除手順（#400 CLI）

誤検知や一時的な ACS 判断で宛先が止まっている場合、**本番 SQLite への直接 SQL は正規手順にしない**。

### 解除対象の確認

1. Admin `/admin/suppressions` でテナントを選び、解除したい宛先を特定する（既定はマスク表示。非マスクは `MAILER_ADMIN_PII_LIST_MODE=visible`）。
2. 件数だけ確認する場合は `db stats [--tenant-id <uuid>]` の `mail_suppressions_count` を使う（宛先は出さない）。

### 解除コマンド

```bash
Amane.Mailer db suppressions remove \
  --tenant-id <tenant-guid> \
  --recipient <email>
```

終了コード:

| Code | 意味 |
|------|------|
| 0 | 1 件削除した |
| 1 | schema unavailable（`mail_suppressions` 未マイグレーション等） |
| 2 | usage error（必須引数不足・不正 UUID 等） |
| 3 | 指定テナントに該当エントリなし（沈黙の成功にしない） |
| 130 | Ctrl+C による協調 cancel |

注意:

- 宛先正規化は格納（#301）/ 照会（#303）/ 解除（#400）で同一の `RecipientEmailNormalizer`（`Trim` + `ToLowerInvariant`）を使う。
- 別テナントの同一宛先を巻き込まない（`--tenant-id` 必須）。
- 標準出力・標準エラーへ宛先を出さない（ADR 0013）。成功時はテナント ID のみを報告する。
- 成功時は `admin_audit_events` に `mail_suppressions.removed`（`TargetId` = suppression 行 ID）、not-found 時は `mail_suppressions.remove_failed` を同一トランザクションで記録する（actor=`cli`、宛先は監査に載せない。監査失敗時は削除も rollback）。
- Admin UI からの解除は本 Issue スコープ外。

## 7. Push（#304）について

Event Grid Webhook Push は **採用していない**。公開エンドポイント・HTTPS 終端・AzureEventGrid Service Tag 制限は #304 側の設計文書を参照。本 runbook では扱わない。

## 8. 関連

- ADR: [docs/adr/0020-bounce-ingestion-and-suppression.md](../adr/0020-bounce-ingestion-and-suppression.md)
- Admin PII: [docs/adr/0013-admin-threat-model-and-pii-policy.md](../adr/0013-admin-threat-model-and-pii-policy.md)
- メトリクス: [metrics-and-alerts.md](metrics-and-alerts.md)