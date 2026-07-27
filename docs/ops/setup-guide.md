[English](setup-guide.en.md)

# Amane Mailer セットアップ入口

初めて Amane Mailer を構築するときの**単一の入口**です。構成を 1 つ選び、必要な情報を揃え、既存 runbook の順に進めます。

この文書は判断・順序・安全境界・用語の正本です。詳細な操作手順は複製せず、各 runbook / 設定 README へリンクします。

Parent tracking: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) · 本 Issue: [#424](https://github.com/kooiei-in4a/amane-mailer/issues/424)

## 読む前に（安全）

- secret、接続文字列、実テナント token、送信元・送信先、PII、provider raw error を docs・Issue・ログ・チャットへ貼らない。
- placeholder（`replace-with-*`、`local-mail-service-token`）だけを例に使う。
- Event Grid **Push** webhook（[#304](https://github.com/kooiei-in4a/amane-mailer/issues/304)）は v1.1.0 の採用方式ではない。案内しない。
- v1.1.0 の bounce transport は **Storage Queue Pull のみ**（`MAILER_BOUNCE_INGESTION=queue`）。
- **実バウンスの発生確認は、通常セットアップの完了条件にしない。**

### v1.1.0 公開イメージについて

bounce ingestion（migration `011` 含む）はソース上実装済みでも、**公開 GitHub release / GHCR タグ `v1.1.0` が無い間は、公開イメージを基準にした最終検証は未実施**として扱う。local build や develop 由来の成果物で手順を追う場合はその旨を運用記録に残す。`v1.1.0` の release / publish / post-promote sync 完了後に、公開イメージで再確認する。

## 結果コードの意味（PASS / FAIL / WARN / ACTION）

後続の setup doctor / 確認 CLI（[#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)）でも同じ意味で使う。現状の smoke script は主に `[PASS]` / `[FAIL]` を出す。

| コード | 意味 | 次にすること |
|--------|------|----------------|
| **PASS** | その確認項目は意図どおり満たされている | 次の確認または次モードへ進む |
| **FAIL** | 必須前提が満たされていない。このまま先へ進むと誤構成や危険な実送信になり得る | 停止し、失敗した項目の正本 runbook / 設定を直す |
| **WARN** | 必須ではないが、運用上のリスクや未推奨状態がある | 記録し、本番投入前に解消するかを判断する |
| **ACTION** | ツールは自動修正しない。人が明示操作する必要がある | 表示された手順（runbook リンク）を人が実行する |

secret 値・宛先平文・接続文字列・raw provider error を結果に含めない。不足は「どの設定キー / どの権限能力が欠けているか」だけを示す。

## 構成モードを選ぶ

次の質問で **1 つだけ**選ぶ。

1. 実メールを送らず、Docker 上で 1 通届くところまで確認したい → **local Mailpit**
2. deploy 形のスタックを組み、ACS 実送信はまだしない → **staging ACS no-send**
3. staging で ACS 接続・sender を、明示した短時間だけ検証する → **staging ACS verification**
4. 承認済み sender で本番配送する（bounce 取り込みはまだ不要） → **production ACS**
5. 本番配送に加え、Delivery Report を Queue 経由で取り込む → **production ACS + Event Grid / Storage Queue**

| モード | 想定用途 | provider | `live_sending` | bounce mode | 主に使う正本 |
|--------|----------|----------|----------------|-------------|--------------|
| local Mailpit | 初回到達確認、開発 smoke | `mailpit` | `false` | `off`（既定） | [Zero-Admin 初回メール quickstart](first-mail-quickstart.md)、[local Docker runbook](local-mailer-docker-runbook.md) |
| staging ACS no-send | deploy 形の起動・token / migrate 確認。実送信なし | `acs`（または JSON どおり） | `false` | 通常 `off` | [local deploy rehearsal](local-deploy-rehearsal-runbook.md)、[設定 README](../../config/mailer/README.md) |
| staging ACS verification | ACS 接続と承認 sender の**明示**検証 | `acs` | 検証中のみ `true`（専用 tenant / 宛先） | 通常 `off` | [register-acs CLI](register-acs-cli-runbook.md)、[設定 README](../../config/mailer/README.md)、drill guide |
| production ACS | 本番配送 | `acs` | `true`（承認済みのみ） | `off` 可 | [deploy `.env.example`](../../infra/deploy/.env.example)、[register-acs CLI](register-acs-cli-runbook.md) |
| production ACS + Queue | 本番配送 + ハードバウンス抑制 | `acs` | `true` | **`queue` のみ** | 上に加え [bounce ingestion runbook](bounce-ingestion-runbook.md) |

## provider / `live_sending` / bounce mode

| 組合せ | 実メール | 受理・永続化 | 備考 |
|--------|----------|--------------|------|
| `mailpit` + `live_sending=false` | なし（Mailpit へ） | する | local の既定。安全な初回確認向き |
| `acs` + `live_sending=false` | **送らない** | する（実送信ゲートで止まる） | staging no-send。`LIVE_SENDING_DISABLED` になり得る |
| `acs` + `live_sending=true` | **送る** | する | 承認済み sender + 登録済み ACS secret が必須 |
| bounce `off` | — | — | v1.0 互換の既定。取り込みしない |
| bounce `queue` | — | — | v1.1.0 採用。Storage Queue Pull のみ |
| bounce `webhook` | — | — | **未実装（#304）。設定すると起動失敗。採用しない** |

`MAILER_PROVIDER` / `Mailer__Provider` は全 tenant の provider を上書きする。意図しない上書きに注意（[設定 README](../../config/mailer/README.md)）。

## 責任境界

| コンポーネント | 責任 | 非責任 |
|----------------|------|--------|
| **ACS Email** | メール送信の引き受け、Delivery Report の発行 | Mailer DB の抑制リスト管理 |
| **Event Grid** | ACS Delivery Report を購読し、**Storage Queue** へ配送 | Mailer への HTTPS Push（v1.1.0 では使わない） |
| **Storage Queue** | イベントの一時保管（at-least-once） | 相関・抑制・PII マスク |
| **Mailer** | 送信依頼の受理、Worker 配送、Queue Pull、相関、`mail_suppressions`、Admin / metrics 可視化 | Azure リソースの自動作成、実バウンスの強制発生 |

環境（dev / staging / production）ごとに **ACS と Queue を分離**する。混線すると `provider_message_id` 誤相関の原因になる（[bounce runbook](bounce-ingestion-runbook.md)）。

## local / staging / production の安全境界

| | local | staging | production |
|--|-------|---------|------------|
| 実送信 | しない（Mailpit） | 既定しない。verification のみ明示 | 承認済みのみ |
| token / `tenant_id` | example / local 専用 | non-production 専用 | production 専用。staging と共有しない |
| ACS secret | local drill は bare env 可（runbook 参照） | file secret（`register-acs`） | file secret のみ |
| Admin | 任意・内部 NW | 任意・到達制限必須 | 任意・到達制限必須（公開 Internet 直出し禁止） |
| bounce Queue | 通常不要 | 通常不要 | mode 5 のみ。環境分離必須 |
| 完了の定義 | health + 1 通 Mailpit 到着など | 起動・preflight・（任意）明示 verification | 配送確認。**実バウンスは不要** |

## 共通チェックリスト（必要情報・権限・secret・network）

値そのものは書かず、「用意できているか」だけ確認する。

### 情報

- [ ] 使う構成モード（上表の 1 つ）
- [ ] tenant JSON の置き場所（example をコピーした **未コミット** ファイル）
- [ ] 各 tenant の `token_env` 名と、対応する環境変数を設定する場所
- [ ] 実効 provider（tenant JSON または `MAILER_PROVIDER`）
- [ ] `live_sending` の意図（false / 明示 true）
- [ ] bounce mode（`off` または `queue`）
- [ ] Admin / metrics / backup を有効にするか（既定オフまたは runbook のとおり）

### Azure 側で必要な能力（mode 2 以降。具体ロール名は組織の IAM に従う）

- [ ] ACS Email リソースを参照し、承認済み sender / domain を確認できる
- [ ]（mode 5）Delivery Report を Event Grid で購読し、エンドポイントを **Storage Queue** にできる
- [ ]（mode 5）対象 Queue の接続情報を、Mailer が読める形（接続文字列または file）で渡せる
- [ ]（mode 3–5）deploy host で `admin provider register-acs` を実行できる（対話 TTY、secret ディレクトリ権限）

### secret（置き場所だけ。値は記録しない）

- [ ] tenant Bearer token（環境変数。JSON 平文禁止）
- [ ]（ACS live）`ACS_CONNECTION_STRING_FILE` 経路の file secret、または local drill 用の一時 env（runbook の境界を守る）
- [ ]（mode 5）`MAILER_BOUNCE_QUEUE_CONNECTION_STRING` または `*_FILE`（ログに出さない）
- [ ]（metrics 有効時）scrape bearer
- [ ]（Admin 有効時）password hash など Admin 秘密

### network / runtime

- [ ] Docker（local / rehearsal）または deploy host の compose ネットワーク
- [ ] Mailer HTTP（health / ready）と、local なら Mailpit UI/API
- [ ] production では reverse proxy / firewall 等の到達境界（Admin 直公開なし）
- [ ]（mode 5）Mailer から Storage Queue への**外向き**到達（公開 HTTPS 受信口は不要）

## 実行順序（全モード共通）

1. **Preflight** — モード選択、チェックリスト、tenant / env の shape 確認（[設定 README Preflight](../../config/mailer/README.md#preflight)）
2. **Setup** — 該当モードの正本 runbook に従い起動・登録
3. **Verification** — health / ready、受理、（モードに応じた）配送または no-send 確認。結果コードは上表
4. **Troubleshooting** — FAIL / WARN 時は下の「失敗時の参照先」へ。自動修正はしない（ACTION）

## モード別の一本道

### 1. local Mailpit

**順序**

1. Preflight: Docker 起動、port 空き（quickstart の前提）
2. Setup / Verification: [Zero-Admin 初回メール quickstart](first-mail-quickstart.md)（自動 smoke: `scripts/local-first-mail-smoke.ps1` / `.sh`）
3. 追加 smoke（冪等・conflict・Admin など）: [local Mailer Docker runbook](local-mailer-docker-runbook.md) / [bash 版](local-mailer-docker-runbook-bash.md)

**完了の目安:** `[PASS]` で health / ready / 1 通 Mailpit 到着。ACS・bounce・実バウンスは不要。

### 2. staging ACS no-send

**順序**

1. Preflight: [設定 README](../../config/mailer/README.md) と `tenants.shared.example.json` 系。`live_sending=false` を維持
2. Setup: [local deploy rehearsal](local-deploy-rehearsal-runbook.md)（`infra/deploy` の `.env` / `tenants.json` はコミットしない）
3. Verification: compose health、migrate、`/healthz` `/readyz`。実送信しない（no-send smoke は rehearsal の案内に従う）
4. ACS secret の本番登録はまだ必須ではない。接続検証は mode 3

**完了の目安:** スタックが healthy / ready。Mailpit 以外への実メールが飛んでいないこと。

### 3. staging ACS verification

**前提:** mode 2 相当の deploy 形が動いている。検証は**明示実行**のみ。

**順序**

1. Preflight: 専用 tenant / 宛先 / 承認済み sender。`live_sending=true` は短時間・限定範囲
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.md)（対話のみ。CLI 引数に secret を渡さない）
3. Verification: 組織で承認された drill / 手順（例: [mail-05a drill guide](drills/mail-05a-drill-guide.html)）。ACS 単体確認 CLI は [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) で今後提供予定
4. 検証後は staging 既定どおり `live_sending=false` に戻すかを判断（WARN になり得る状態を残さない）

**完了の目安:** 明示した検証メールが ACS 経由で期待どおり処理されること。**実バウンスは不要。**

### 4. production ACS

**順序**

1. Preflight: production 専用 token / tenant。承認済み sender。metrics bearer 等（[deploy `.env.example`](../../infra/deploy/.env.example)）
2. Setup: deploy compose（[infra/deploy/compose.yml](../../infra/deploy/compose.yml)）、[register-acs](register-acs-cli-runbook.md)、必要なら backup 設定（バックアップ runbook）
3. Verification: `/healthz` `/readyz`、承認済み経路での受理・配送確認。公開 release イメージ smoke は [release-image-smoke](release-image-smoke.md)（タグは当時の公開 release に合わせる）
4. bounce が不要なら mode はここで完了（bounce `off`）

**完了の目安:** 本番配送が期待どおり。Queue 未設定でもよい。

### 5. production ACS + Event Grid / Storage Queue

**前提:** mode 4 完了。**v1.1.0 系イメージ**（migration `011` 含む）を使う。公開 `v1.1.0` が未完了の間は、公開イメージ最終検証は保留。

**順序**

1. Preflight: ACS / Event Grid / Queue が **production 専用**に分離されていること。Push webhook を作らない
2. Setup（Azure）: Delivery Report → Event Grid → **Storage Queue**（手順の詳細はクラウド側の正本運用。Mailer は Queue を Pull するだけ）
3. Setup（Mailer）: [bounce ingestion runbook](bounce-ingestion-runbook.md) の `MAILER_BOUNCE_INGESTION=queue` と Queue 接続設定。deploy テンプレートへ未配線の項目は、正本 runbook の環境変数名に従い host 側で渡す（本 Issue では compose を変更しない）
4. Verification: Mailer 起動、Queue ポーリング失敗メトリクスが継続増加していないこと、Admin / metrics の案内に従う。Event Grid / Queue の read-only 確認と Delivery Report E2E は [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) / [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) で今後提供予定
5. **実バウンスを起こしての確認は完了条件にしない。** 正常 Delivery Report の到着確認で足りる（#428 予定）

**完了の目安:** bounce mode `queue` で Mailer が起動し、環境分離された Queue を Poll できること。抑制リストへの実バウンス登録は任意の運用確認であり必須ではない。

## 失敗時の参照先

| 症状の例 | 参照 |
|----------|------|
| tenant / token / `LIVE_SENDING_DISABLED` / provider 不足 | [設定 README troubleshooting](../../config/mailer/README.md#tenant--env-troubleshooting) |
| local 起動・Admin・Mailpit | [local Docker runbook](local-mailer-docker-runbook.md) |
| deploy 形の compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.md) |
| ACS secret 登録失敗 | [register-acs CLI](register-acs-cli-runbook.md) |
| bounce / unmatched / Queue poll | [bounce ingestion](bounce-ingestion-runbook.md)、[metrics-and-alerts](metrics-and-alerts.md) |
| 公開イメージ smoke | [release-image-smoke](release-image-smoke.md) |

## 今後提供予定の確認機能（完成済み扱いしない）

| Issue | 予定 |
|-------|------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS 単体の実送信確認 CLI |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | Event Grid / Storage Queue の read-only 構成確認 |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report の Queue 到着 E2E（message ID 相関。実バウンス必須にしない） |

現時点では既存の preflight script・smoke・runbook 手動確認で進める。

## この入口の非目標

- setup CLI / doctor / Azure リソース自動作成の実装
- 既存 runbook 全文のこのファイルへの複製
- v1.2.0 の Consumer bounce API / webhook 契約の説明
- Event Grid Push（#304）の採用手順
- 実在 credential / tenant / private path の掲載
