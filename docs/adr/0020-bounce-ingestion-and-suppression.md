# ADR 0020: バウンス取り込みと抑制リストの設計

- **Status:** Accepted
- **Date:** 2026-07-26
- **Amended by:** [ADR 0023](0023-multiple-recipient-contract-and-delivery-semantics.md)（2026-08-05）
- **Related PR:** [#539](https://github.com/kooiei-in4a/amane-mailer/pull/539)
- **Related issues:** [#519](https://github.com/kooiei-in4a/amane-mailer/issues/519)、[#530](https://github.com/kooiei-in4a/amane-mailer/issues/530)
- **Decision owner:** Koo
- **Design approval:** 2026-08-05（production implementationは未承認・未実施）
- **Tracks:** [#300](https://github.com/kooiei-in4a/amane-mailer/issues/300)
- **Implementation follow-up:** [#301](https://github.com/kooiei-in4a/amane-mailer/issues/301)（スキーマ）、[#302](https://github.com/kooiei-in4a/amane-mailer/issues/302)（取り込みコア）、[#303](https://github.com/kooiei-in4a/amane-mailer/issues/303)（送信前チェック）、[#305](https://github.com/kooiei-in4a/amane-mailer/issues/305)（Pull 受信）、[#306](https://github.com/kooiei-in4a/amane-mailer/issues/306)（可視化）、[#400](https://github.com/kooiei-in4a/amane-mailer/issues/400)（解除 CLI）
- **Continues:** [ADR 0012 D-06](0012-mail-via-mailer-microservice.md)（`WaitUntil.Completed` と将来の配信結果精緻化の予約）
- **Preserves:** [ADR 0012 D-07](0012-mail-via-mailer-microservice.md)（Worker 1 レプリカ）、[ADR 0013 D-02](0013-admin-threat-model-and-pii-policy.md)（到達制限）、[ADR 0019](0019-sqlite-single-process-boundaries.md)（SQLite／単一プロセス前提）
- **Spikes:** [#299](https://github.com/kooiei-in4a/amane-mailer/issues/299)（ACS / Event Grid 実機検証）、[#399](https://github.com/kooiei-in4a/amane-mailer/issues/399)（`Azure.Storage.Queues` の Native AOT 互換）

## Context

現在の Mailer は送信の**受理**までしか観測していない。`AcsMailDeliveryProvider` は `WaitUntil.Completed` で ACS の送信操作完了を待ち、成功なら `mail_requests.status = Delivered` を終端として書く。しかしこれは「ACS が配送を引き受けた」ことを意味するに過ぎず、受信箱への到達を保証しない。

その結果、**宛先が存在しないメールも運用者からは「配信成功」に見える**。#299 の実機検証でこれを実測した（後述「実機検証で確定した事実」F-4）。到達しない宛先へ送り続けると、送信ドメインの評価が下がり、正常な宛先への配信にも影響する。

バウンスは ACS の HTTP 応答からは取得できず、Event Grid の `Microsoft.Communication.EmailDeliveryReportReceived` イベントでのみ得られる。本 ADR は、そのイベントを取り込み、ハードバウンスした宛先への送信を止めるまでの設計を固定する。

### 本 ADR で決めないこと

- Consumer 向けの `bounced` webhook 通知（D-11）
- `mail_requests` の状態機械の変更（D-05）
- Push 型（Event Grid Webhook）受信口の実装（D-02）

## 実機検証で確定した事実（#299）

以下は仮説ではなく実測値である。設計判断の根拠として本文に固定する。記録: [#299 のコメント](https://github.com/kooiei-in4a/amane-mailer/issues/299#issuecomment-5081986279)。

### F-1. ID 相関は成立する

`data.messageId` と `mail_attempts.provider_message_id` は 3 件すべてで完全一致した。

書式は **小文字・ハイフンあり・中括弧なしの Guid `D` 形式**で、両者に差異はない。SQLite の TEXT 比較は既定でバイナリ比較だが、**正規化なしでそのまま照合できる**。

さらに、渡した決定的 operationId（`UuidV5(tenant_id, "source_service:mail_request_id")`）と `operation.Id` も一致した。すなわち次の 4 つは同一値である。

```
渡した operationId ═ operation.Id ═ provider_message_id ═ data.messageId
```

帰結として、**`provider_message_id` は送信前に決定的に算出できる**。

### F-2. ステータスのフィールド名は `data.status`

`deliveryStatus` ではない。実ペイロードは `"status": "Bounced"`。フィールド名を誤ると全イベントが分類不能になる。

ハードバウンス時の値が **`"Bounced"`** であることを実測した。正常配信は `"Delivered"`。**その他の値（`Failed` / `Quarantined` / `Suppressed` など）は未観測**である。

### F-3. `data.recipient` に宛先アドレスが平文で入る

イベント本文は PII を含む。生のまま永続化・ログ出力してはならない。

### F-4. `mail_requests.status` はバウンス時も `Delivered` のまま

ACS が `Bounced` を報告した送信でも、Mailer の DB 上の状態は `Delivered`（終端）だった。バウンスは既存の状態機械の外側にある事実である。

### F-5. `deliveryStatusDetails.statusMessage` は生のプロバイダ応答

ハードバウンス時、受信側 MTA の SMTP 応答がそのまま入る。実測では宛先アドレスは含まれなかったが、**プロバイダによっては応答にアドレスを含める**。加えて、実測値の**末尾に `};'` という壊れた文字列が付いていた**。整形済みであることを前提にパースしてはならない。正常配信時は空文字列。

### F-6. Storage Queue のメッセージは生 JSON

Base64 エンコードされていない。

### F-7. 送信からイベント到達まで約 2 分

3 件とも同程度。ポーリング間隔の設計に用いる。

## Decision

### D-01. Inbox パターンでイベントを耐久化し、重複を排除する

受信したイベントは、処理前に `provider_event_inbox` へ挿入する。`UNIQUE (provider, event_id)` により重複を吸収する。

Event Grid は at-least-once 配信であり、同一イベントが複数回届く。受信直後に永続化してから処理することで、取り込み処理の失敗とイベントの喪失を分離する。

inbox の lease／再試行のイディオムは `delivery_events` を複製する。**複製にあたっては [#388](https://github.com/kooiei-in4a/amane-mailer/issues/388) と [#402](https://github.com/kooiei-in4a/amane-mailer/issues/402) の修正を含んだ現行の形を写すこと。** それ以前の形には、最終試行の lease 期限切れが終端へ収束しない欠陥と、シグナル未排出によるアイドル時ホットスピンがある。

### D-02. v1.1.0 のトランスポートは Pull（Storage Queue ポーリング）で確定する

Push 型（公開 Event Grid Webhook 受信口）は **v1.1.0 スコープ外**とする。

| 観点 | Push | **Pull（採用）** |
|------|------|------------------|
| 通信方向 | 内向き（公開 HTTPS 受信口が必要） | 外向きのみ |
| 既存の脅威モデル | **サービス初のインターネット到達点**。ADR 0013 D-02 の到達制限前提が崩れる | 変更なし |
| 追加の運用準備 | HTTPS 終端 / IP 制限 / ボディ上限 / レート制限（現在レート制限機構は無い） | 接続文字列の管理 |
| 新規依存 | なし | `Azure.Storage.Queues` |
| 検知遅延 | 即時 | ポーリング間隔ぶん |

`/internal` プレフィクス、Admin の既定オフ、ADR 0013 D-02 の到達制限、single-node 運用という既存の一貫した方針に対し、Pull は前提を一切変えない。Push の唯一の優位である即時性は、抑制リスト用途では要件にならない（F-7 のとおりイベント自体が約 2 分遅れて届く）。

Pull の唯一のリスクであった Native AOT / trim 互換は #399 で検証済みである。

- `Azure.Storage.Queues` 12.27.1 / linux-x64 で **publish 警告・エラーなし**（`IlcTreatWarningsAsErrors=true`）
- 検証 API: `QueueClient` / `ReceiveMessagesAsync` / `UpdateMessageAsync` / `DeleteMessageAsync`
- 実行スモークで 3 API とも runtime 到達、AOT メタデータ欠落例外なし
- 出力バイナリ 23,262,496 → 23,621,088 bytes（**+358,592**）。trim による消去で警告が出なかった「偽陽性の合格」ではないことを確認済み
- `src/Amane.Mailer.Contracts/packages.lock.json` に内容変化なし（公開パッケージ側への波及なし）

**判定の限界:** 実 Storage への接続は未実施で、ダミー接続の `RequestFailedException` 到達までを確認した。**成功レスポンスの逆シリアライズ経路（`ReceiveMessages` の XML → `QueueMessage[]`）のみ未実行**である。publish 警告がゼロでありリフレクション非依存（`System.Xml.Linq` による手書きパース）であることから危険度は低いが、#305 の実装時に Azurite で 1 度通して確定させる。

win-x64 でのネイティブリンクは未実施だが、CI の AOT publish は linux-x64 のみのため判定上の欠落にはあたらない。

Push 型の設計は #304 に文書として保持する。Pull で運用上の不足（ポーリング遅延が要件に合わない、Storage アカウントを持てない配置が必要）が判明した場合、本 ADR へ判断変更を追記した上で再検討する。

### D-03. 相関キーは `provider_message_id` ↔ `data.messageId` とし、正規化しない

F-1 のとおり両者は書式・casing まで一致する。**正規化関数を挟まない。** 挟むとインデックスが効かなくなるうえ、将来の書式変更を隠してしまう。

相関できなかったイベントは破棄せず、`mail_bounce_unmatched_total` として計上する。相関率の低下は設計前提の崩壊を示す最初の兆候であり、無音で失われてはならない。

`provider_message_id` は送信前に算出できる（F-1）が、**相関は DB 逆引きで行う。** 算出に頼ると、operationId の生成規則を変更したときに過去のイベントが相関できなくなる。

### D-04. 宛先は DB 側の値を正本とし、イベント申告値を信用しない

抑制リストに登録する宛先は、相関で特定した `mail_requests.recipient_email` を使う。`data.recipient`（F-3）は**照合にのみ使い、登録値としては採用しない**。

イベント申告値をそのまま登録すると、イベントが改竄・混線した場合に無関係な宛先を止められる。認証（トランスポート）・相関（messageId）・照合（recipient）の三層で確認する。

`data.recipient` と DB 側が食い違うイベントは登録せず破棄し、`mail_bounce_recipient_mismatch_total` に計上する。

### D-05. `mail_requests` の状態機械を変更しない

`Delivered` は終端のまま維持する。`Bounced` ステータスは追加しない。

バウンスは送信処理の結果ではなく、送信後に非同期で判明する別の事実である（F-4）。状態機械に混ぜると、Worker・Admin・GET status API・webhook payload のすべてに波及し、既存の契約を壊す。

バウンスの事実は `bounce_events` に保持し、リクエストとは `mail_request_id` で関連付ける。**FK は張らない。** 理由は retention 判定の独立性（`mail_requests` の完了判定に依存しない削除経路にできる）と、将来 `bounce_events` の保持方針を変える際に `mail_requests` のスキーマ変更を要さないことにある。**保持期間自体は `mail_requests` と揃え、同一 `DeleteExpiredCompletedAsync` トランザクションで purge する。** 耐久的な送信ブロック状態は `mail_suppressions` が担う（既定で非 purge）。リクエスト詳細画面からの表示は、参照先が存在しない状態を許容する設計にすること（#306。同一トランザクション purge でも読み取り側との競合はあり得る）。

### D-06. 抑制リストはテナント別に分離し、ハードバウンスのみ即時登録する

`mail_suppressions` は `(tenant_id, 正規化宛先)` で UNIQUE とする。テナントをまたいだ抑制は行わない。

**即時登録の対象は `status = "Bounced"` のみ**とする（F-2）。未観測のステータス値（`Failed` / `Quarantined` / `Suppressed` 等）は、**記録はするが抑制登録はしない**。分類が確定していない値で送信を止めると、誤検知に気づく手段がない。対象を広げる場合は、実データで値と意味を確認してから #301 / #302 の判断として追加する。

宛先の正規化方式は、格納（#301）・照会（#303）・解除（#400）の三箇所で**完全に一致させる**。食い違うと、UNIQUE インデックスが効かず全表走査になるか、抑制が効かないまま送信される。

### D-07. 解除経路を v1.1.0 スコープに含める

抑制リスト解除の CLI（#400）を v1.1.0 の必須スコープとする。

自動追加のみで解除経路が無い状態は、誤検知が起きたときに**本番 SQLite への直接 SQL 以外の回復手段が無い**ことを意味する。送信ブロック（#303）を有効にする以上、解除は同一リリースに存在しなければならない。

### D-08. プロバイダ応答はサニタイズしてから永続化する

`deliveryStatusDetails.statusMessage`（F-5）は生のプロバイダ応答であり、宛先アドレスやプロバイダ固有のテキストを含み得る。既存の `ProviderErrorSanitizer` と同じ方針で、**DB 書き込み・ログ出力・Admin 表示のいずれの前にも**サニタイズする（[#26](https://github.com/kooiei-in4a/amane-mailer/issues/26)）。

パーサは整形済み入力を前提にしないこと。実測値には壊れた末尾文字列が含まれていた（F-5）。

イベント本文全体を `provider_event_inbox` に生のまま保持する場合、そのカラムは PII を含む扱いとし、retention・バックアップ・Admin 表示の各方針に反映する。

### D-09. 失敗時は永続リトライを Azure 側に委譲する

取り込みに失敗した場合、**キューメッセージを削除しない**。visibility timeout の経過後に自動で再出現し、重複は D-01 の inbox が吸収する。

アプリ内に独自の in-memory リトライキューを持たない。プロセス再起動で失われる回復手段は、耐久化の意味を損なう。

`SQLITE_BUSY`（busy_timeout 超過）も同様に扱う。

### D-10. メトリクスは `mail_*` 命名に揃え、テナントラベルを付けない

`PrometheusMetricsFormatter` の既存方針に従う。カーディナリティと PII の両面から、`tenant_id` や宛先をラベルにしない。

| 用途 | メトリクス名 | 型 |
|------|-------------|-----|
| 取り込んだバウンス件数 | `mail_bounce_events_total` | counter |
| 相関できなかった件数 | `mail_bounce_unmatched_total` | counter |
| 宛先不一致で破棄した件数 | `mail_bounce_recipient_mismatch_total` | counter |
| 送信前ブロック件数 | `mail_suppressed_sends_total` | counter |
| キューポーリング失敗 | `mail_provider_queue_poll_failed_total` | counter |
| Inbox 滞留 | `mail_provider_events_pending` | gauge |
| Inbox dead letter | `mail_provider_events_dead_lettered` | gauge |

### D-11. Consumer 向け `bounced` 通知は v1.1.0 スコープ外とする

`delivery_events` の UNIQUE / CHECK 制約変更と HTTP 契約変更（OpenAPI・Contracts・SDK の同期）を伴うため、v1.2.0 以降へ分離する。

v1.1.0 におけるバウンスの可視化手段は **Admin UI とメトリクスのみ**である（#306）。抑制リスト一覧は宛先アドレスの列挙そのものになるため、ADR 0013 D-05 の既定マスク方針を適用する。

### D-12. ADR 0023 amendment: recipient-level feedback and ACS Suppressed

v1.3.0ではdelivery eventをrecipient単位で関連付け、`mail_request_recipients`をrecipient correlationのcanonical sourceとする。[ADR 0023](0023-multiple-recipient-contract-and-delivery-semantics.md) がrecipient state、request aggregate、BCC privacyの詳細正本であり、本ADRはbounce ingestion、event history、suppression dataの責任を維持する。

provider message IDだけでcross-tenant correlationを行わない。tenant scope、request特定、canonical recipient照合の順に限定する。duplicate eventは冪等に処理し、out-of-order eventはhistoryを保持し、unknown recipient、cross-tenant、cross-source-service eventは他のrecipientへ相関しない。provider eventは外部観測結果であり、canonical recipient rowを置き換えない。

ACS `Suppressed` は `Delivered` として扱わない。recipient単位のSuppressed結果として記録し、suppression対象として保存し、将来requestの事前拒否へ使う。request全体のstatusは他recipient結果とのaggregateで決定する。`Bounced` と `Suppressed` はsuppression対象、`Failed`、`Quarantined`、未確認のunknown statusは記録のみとする。

事前suppressionは既存のall-or-nothing送信境界を維持し、一人でもsuppressedならprovider invocationを行わない。対象recipientはSuppressed、他recipientはNotSent、request aggregateはFailedとする。Issue [#530](https://github.com/kooiei-in4a/amane-mailer/issues/530)がACS Suppressedのproduction実装、分類、回帰テストを独立して所有する。本ADR amendmentとPR #539はIssue #530の実装完了を表さない。

このamendmentはevent／suppressionの設計責任と#530のownershipを記録するだけであり、runtime、migration、test codeはPR #539で変更しない。

## Rejected Alternatives

| 案 | 却下理由 |
|----|----------|
| Push（Event Grid Webhook）を v1.1.0 で提供する | サービス初のインターネット到達点を作る。ADR 0013 D-02 の到達制限前提が崩れ、HTTPS 終端・IP 制限・レート制限の運用準備を伴う。即時性は抑制用途で要件にならない（D-02） |
| Push / Pull の両方を v1.1.0 で提供する | 公開エンドポイント追加と新規 Azure 依存追加を同時に抱える。片方に絞る |
| クエリ文字列に共有シークレットを載せる | リポジトリのハード制約「URL クエリの secret 禁止」に抵触 |
| Microsoft Entra ID 認証を採用する | AOT / 依存負担が過大。共有シークレットで要件を満たせる |
| `mail_requests` に `Bounced` ステータスを追加する | Worker / Admin / GET status / webhook payload への波及が過大。バウンスは状態遷移ではなく別事実（D-05） |
| `data.recipient` をそのまま抑制リストに登録する | イベント申告値の単独信用。混線・改竄時に無関係な宛先を止める（D-04） |
| messageId を正規化してから照合する | F-1 のとおり不要。インデックスを無効化し、将来の書式変更を隠す |
| 未観測のステータス値も抑制対象に含める | 分類が未確定の値で送信を止めると誤検知に気づけない（D-06） |
| Inbox を別 SQLite ファイルに分離する | 初期は不採用。競合が実測で問題化した場合の将来オプション |
| 取り込み失敗時に独自のリトライキューを持つ | プロセス再起動で失われる。Azure 側の再配信に委譲する（D-09） |
| 抑制リストの解除を v1.2.0 へ送る | 誤検知時の回復手段が本番 SQLite への直接 SQL のみになる（D-07） |

## Consequences

- _positive:_ バウンスした宛先への再送が止まり、送信ドメインの評価低下を防げる。
- _positive:_ 相関の成立が実測で確認済みであり、実装前に前提が崩れるリスクを潰してある（F-1）。
- _positive:_ Pull 採用により、既存の到達制限・脅威モデルを一切変更せずにバウンス取り込みを追加できる。
- _positive:_ `mail_requests` の状態機械が不変なので、既存の HTTP 契約・SDK・Admin への波及がない。
- _negative:_ バウンス検知は約 2 分（F-7）＋ポーリング間隔ぶん遅れる。即時性が必要な用途には使えない。
- _negative:_ Storage アカウントと接続文字列という運用対象が 1 つ増える。
- _negative:_ 抑制リストは自動で増え続ける。誤検知の回復は CLI 操作（#400）に依存する。
- _operational:_ `mail_bounce_unmatched_total` の増加は相関設計の破綻を示す。ランブックで監視対象に含めること（#306）。
- _operational:_ 抑制リスト一覧は宛先アドレスの列挙であり、新たな PII 面である。ADR 0013 D-05 の既定マスクを適用する。

## References

- [#300 docs: バウンス取り込みと抑制リストの ADR を追加する](https://github.com/kooiei-in4a/amane-mailer/issues/300)
- [#299 spike: ACS Event Grid 配信レポートを実機検証する](https://github.com/kooiei-in4a/amane-mailer/issues/299) — [検証結果](https://github.com/kooiei-in4a/amane-mailer/issues/299#issuecomment-5081986279)
- [#399 spike: Azure.Storage.Queues の Native AOT / trim 互換](https://github.com/kooiei-in4a/amane-mailer/issues/399) — [判定](https://github.com/kooiei-in4a/amane-mailer/issues/399#issuecomment-5079154007)
- [#26 provider error sanitization](https://github.com/kooiei-in4a/amane-mailer/issues/26)
- [#388 Webhook 最終試行の lease 期限切れ収束](https://github.com/kooiei-in4a/amane-mailer/issues/388)
- [#402 WebhookDeliveryWorker のアイドル時ホットスピン](https://github.com/kooiei-in4a/amane-mailer/issues/402)
- [ADR 0012 D-06 / D-07](0012-mail-via-mailer-microservice.md)
- [ADR 0013 D-02 / D-05](0013-admin-threat-model-and-pii-policy.md)
- [ADR 0019 SQLite／単一プロセス前提](0019-sqlite-single-process-boundaries.md)
- `src/Amane.Mailer/Delivery/AcsMailDeliveryProvider.cs`
- `src/Amane.Mailer/Delivery/AcsOperationIdFactory.cs`
- `src/Amane.Mailer/Delivery/UuidV5.cs`
- `src/Amane.Mailer/Data/Migrations/008_delivery_events.sql`
- `src/Amane.Mailer/Operations/PrometheusMetricsFormatter.cs`
