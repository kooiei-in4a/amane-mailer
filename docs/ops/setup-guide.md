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

[release-image-smoke](release-image-smoke.md) の既定タグは現時点で公開済み release（例: `v1.0.1`）向けです。そのまま実行しても **v1.1.0 の検証にはなりません**。

### 現時点で完了できない構成（正直な境界）

次は構成の**目標像**として区別するが、現行の正本 deploy テンプレート / CLI だけでは完遂できない。完了可能なモードとして扱わない。

| ギャップ | 現状 | モード完遂可否 | 診断時の扱い |
|----------|------|----------------|--------------|
| production ACS の file secret 登録 | `admin provider register-acs` は確認フレーズとして完全一致の **`Staging` のみ**受理する。production を対象にしていながら `Staging` と入力させる使い方は**禁止**（安全確認を壊す） | mode 4 の live send は **Blocked**（deploy 形の準備までは Available） | live send 完了判定は `[FAIL]` + 正規手順待ちの `[ACTION]` |
| platform-owned sender | 同 CLI が `platform-sender.json` も書くが、現時点では tenant の ACS 送信経路からは使われない | tenant 実送信の完了条件に含めない | tenant 送信完了の根拠にしない |
| production ACS + Queue（mode 5） | [bounce ingestion runbook](bounce-ingestion-runbook.md) が要求する `MAILER_BOUNCE_INGESTION` / Queue 接続 / Queue 名は、現行 [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) の `environment` / volume に**未配線**。host shell にだけ置いてもコンテナへ渡らない | **Target only**（deploy template 対応まで完了不可） | 完了判定は `[FAIL]` + compose 配線待ちの `[ACTION]` |

## モード完遂可否と結果コード（分離）

構成が今完了できるかどうか（モード表の列）と、診断 CLI の結果コードは別レイヤとする。後続の setup doctor / 確認 CLI（[#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)）は下の結果コード意味に合わせる。現状の smoke script は主に `[PASS]` / `[FAIL]` を出す。

### モード完遂可否（構成の提供状況）

| 値 | 意味 |
|----|------|
| **Available** | 現行の正本 runbook / CLI / deploy テンプレートだけで完遂できる |
| **Blocked** | 目標モードだが、必須経路が欠けており今は完遂できない |
| **Target only** | 目標像の説明のみ。現行テンプレートでは完了扱いにしない |

### 結果コード（診断出力）

| コード | 意味 | 次にすること |
|--------|------|----------------|
| **PASS** | 機械的に確認済み。その確認項目は意図どおり満たされている | 次の確認または次モードへ進む |
| **FAIL** | セットアップを進められない不整合、または必須前提不足 | 停止する。完了不能な必須ギャップも含む（「注意すれば使える」ではない） |
| **WARN** | **動作可能**だが、人間の確認やリスク判断が必要 | 記録し、人手で確認する。完了不能を WARN にしない |
| **ACTION** | 次に行う安全な操作（ツールは自動修正しない） | 表示された手順を人が実行する。手順が存在しない項目は推測で埋めない |

代表例:

| 状態 | モード完遂可否 | 診断 |
|------|----------------|------|
| production-safe な secret 登録経路なし | Blocked（live send） | `[FAIL]` + `[ACTION]` |
| bounce env / Queue secret の compose 未配線 | Target only | `[FAIL]` + `[ACTION]` |
| Queue poller は動くが Event Grid 到着未確認 | （モードによる） | `[WARN]` または `[ACTION]` |
| 公開 v1.1.0 イメージ未検証 | （モードによる） | `[WARN]` または `[ACTION]` |

secret 値・宛先平文・接続文字列・raw provider error を結果に含めない。不足は「どの設定キー / どの権限能力が欠けているか」だけを示す。

`mail_provider_queue_poll_failed_total` が増えないことだけでは、Event Grid → Queue 配線の成功判定にしない（poller が動いてもイベント未到着があり得る → `[WARN]` / `[ACTION]`）。

## 構成モードを選ぶ

次の質問で **1 つだけ**選ぶ。

1. 実メールを送らず、Docker 上で 1 通届くところまで確認したい → **local Mailpit**
2. deploy 形のスタックを組み、ACS 実送信はまだしない → **staging ACS no-send**
3. staging で ACS 接続・sender を、明示した短時間だけ検証する → **staging ACS verification**
4. 承認済み sender で本番配送する（bounce 取り込みはまだ不要） → **production ACS**（現行 CLI では secret 登録まで完遂不可。下表）
5. 本番配送に加え、Delivery Report を Queue 経由で取り込む → **production ACS + Event Grid / Storage Queue**（**目標構成**。現行 deploy template では未対応）

| モード | 想定用途 | provider | `live_sending` | bounce mode | 完遂可否（現行正本） | 主に使う正本 |
|--------|----------|----------|----------------|-------------|----------------------|--------------|
| local Mailpit | 初回到達確認、開発 smoke | `mailpit` | `false` | `off`（既定） | **Available** | [Zero-Admin 初回メール quickstart](first-mail-quickstart.md)、[local Docker runbook](local-mailer-docker-runbook.md) |
| staging ACS no-send | deploy 形の起動・token / migrate 確認。実送信なし | `acs`（または JSON どおり） | `false` | 通常 `off` | **Available**（実送信なし） | [local deploy rehearsal](local-deploy-rehearsal-runbook.md)、[設定 README](../../config/mailer/README.md) |
| staging ACS verification | ACS 接続と承認 sender の**明示**検証 | `acs` | 検証中のみ `true`（専用 tenant / 宛先） | 通常 `off` | **Available**（Staging） | [register-acs CLI](register-acs-cli-runbook.md)（**Staging 限定**）、[設定 README](../../config/mailer/README.md)、drill guide |
| production ACS | 本番配送の目標 | `acs` | `true`（承認済みのみ） | `off` 可 | deploy 形・設定は **Available**。live send は **Blocked**（production 正規 secret 登録なし） | [deploy `.env.example`](../../infra/deploy/.env.example)、[compose.yml](../../infra/deploy/compose.yml)、[設定 README](../../config/mailer/README.md) |
| production ACS + Queue | 本番配送 + ハードバウンス抑制の目標 | `acs` | `true` | **`queue` のみ** | **Target only** | 目標の設定キーは [bounce ingestion runbook](bounce-ingestion-runbook.md)。compose 配線は別途対応が必要 |

## provider / `live_sending` / bounce mode

| 組合せ | 実メール | 受理・永続化 | 備考 |
|--------|----------|--------------|------|
| `mailpit` + `live_sending=false` | なし（Mailpit へ） | する | local の既定。安全な初回確認向き |
| `acs` + `live_sending=false` | **送らない** | する（実送信ゲートで止まる） | staging no-send。`LIVE_SENDING_DISABLED` になり得る |
| `acs` + `live_sending=true` | **送る** | する | 承認済み sender + 登録済み ACS secret が必須 |
| bounce `off` | — | — | v1.0 互換の既定。取り込みしない |
| bounce `queue` | — | — | v1.1.0 採用。Storage Queue Pull のみ。**runtime は対応、deploy compose は未配線** |
| bounce `webhook` | — | — | **未実装（#304）。設定すると起動失敗。採用しない** |

`MAILER_PROVIDER` / `Mailer__Provider` は全 tenant の provider を上書きする。意図しない上書きに注意（[設定 README](../../config/mailer/README.md)）。

### ACS secret と platform-owned sender の境界

| 対象 | 何をするか | いま使える場面 |
|------|------------|----------------|
| tenant ACS 配送用 connection string（file） | Staging/Production deploy の `ACS_CONNECTION_STRING_FILE` が参照する file secret | **Staging** では [register-acs CLI](register-acs-cli-runbook.md) が登録できる。**Production 確認フレーズは未対応** |
| `platform-sender.json` | System Admin 向け platform-owned sender 情報 | 同 CLI が書くが、**現行 runtime の tenant 送信経路では未使用**。tenant 実送信完了の根拠にしない |

production オペレーターに、production 作業なのに確認欄へ `Staging` と書かせる案内はしない。

## 責任境界

| コンポーネント | 責任 | 非責任 |
|----------------|------|--------|
| **ACS Email** | メール送信の引き受け、Delivery Report の発行 | Mailer DB の抑制リスト管理 |
| **Event Grid** | ACS Delivery Report を購読し、**Storage Queue** へ配送 | Mailer への HTTPS Push（v1.1.0 では使わない） |
| **Storage Queue** | イベントの一時保管（at-least-once） | 相関・抑制・PII マスク |
| **Mailer** | 送信依頼の受理、Worker 配送、Queue Pull、相関、`mail_suppressions`、Admin / metrics 可視化 | Azure リソースの自動作成、実バウンスの強制発生、deploy template に無い env の暗黙注入 |

環境（dev / staging / production）ごとに **ACS と Queue を分離**する。混線すると `provider_message_id` 誤相関の原因になる（[bounce runbook](bounce-ingestion-runbook.md)）。

## local / staging / production の安全境界

| | local | staging | production |
|--|-------|---------|------------|
| 実送信 | しない（Mailpit） | 既定しない。verification のみ明示 | 承認済みのみ。secret 正規登録手順は現行 CLI 外 |
| token / `tenant_id` | example / local 専用 | non-production 専用 | production 専用。staging と共有しない |
| ACS secret | local drill は bare env 可（runbook 参照） | file secret（`register-acs`、確認は `Staging`） | file secret 必須だが、**現行 register-acs では production 確認不可** |
| Admin | 任意・内部 NW | 任意・到達制限必須 | 任意・到達制限必須（公開 Internet 直出し禁止） |
| bounce Queue | 通常不要 | [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) で environment 別の read-only 構成確認（予定）。[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) は Staging E2E のみ | Target only。現行 compose 未配線 |
| 完了の定義 | health + 1 通 Mailpit 到着など | 起動・preflight・（任意）明示 verification | deploy 形の準備は可。**live 配送の正規完了は secret 登録ギャップ解消後**。実バウンスは不要 |

## 共通チェックリスト（必要情報・権限・secret・network）

値そのものは書かず、「用意できているか」だけ確認する。

### 情報

- [ ] 使う構成モード（上表の 1 つ）。mode 4 / 5 は上記ギャップを理解したうえでの選択
- [ ] tenant JSON の置き場所（example をコピーした **未コミット** ファイル）
- [ ] 各 tenant の `token_env` 名と、対応する環境変数を設定する場所
- [ ] 実効 provider（tenant JSON または `MAILER_PROVIDER`）
- [ ] `live_sending` の意図（false / 明示 true）
- [ ] bounce mode（`off` または目標としての `queue`）
- [ ] Admin / metrics / backup を有効にするか（既定オフまたは runbook のとおり）

### Azure 側で必要な能力（mode 2 以降。具体ロール名は組織の IAM に従う）

- [ ] ACS Email リソースを参照し、承認済み sender / domain を確認できる
- [ ]（mode 3）deploy host で `admin provider register-acs` を実行できる（対話 TTY、secret ディレクトリ権限、確認フレーズ `Staging`）
- [ ]（mode 5・目標）Delivery Report を Event Grid で購読し、エンドポイントを **Storage Queue** にできる
- [ ]（mode 5・Target only）対象 Queue の接続情報を、**compose 経由で** Mailer コンテナへ渡せる手段がある（現状の upstream `compose.yml` だけでは不可 → 完了判定は `[FAIL]` + `[ACTION]`）

### secret（置き場所だけ。値は記録しない）

- [ ] tenant Bearer token（環境変数。JSON 平文禁止）
- [ ]（Staging ACS live）`register-acs` が書く `ACS_CONNECTION_STRING_FILE` 経路の file secret
- [ ]（production ACS）file secret が必要であることは [`.env.example`](../../infra/deploy/.env.example) のとおり。**登録 CLI の production 確認は未対応** → live send 完了は `[FAIL]` + `[ACTION]`（推測手順で埋めない）
- [ ]（mode 5・Target only）Queue 接続文字列または file。compose 未配線ならコンテナからは読めない → 完了判定は `[FAIL]` + `[ACTION]`
- [ ]（metrics 有効時）scrape bearer
- [ ]（Admin 有効時）password hash など Admin 秘密

### network / runtime

- [ ] Docker（local / rehearsal）または deploy host の compose ネットワーク
- [ ] Mailer HTTP（health / ready）と、local なら Mailpit UI/API
- [ ] production では reverse proxy / firewall 等の到達境界（Admin 直公開なし）
- [ ]（mode 5・目標）Mailer から Storage Queue への**外向き**到達（公開 HTTPS 受信口は不要）

## 実行順序（全モード共通）

1. **Preflight** — モード選択、チェックリスト、tenant / env の shape 確認（[設定 README Preflight](../../config/mailer/README.md#preflight)）
2. **Setup** — 該当モードの正本 runbook に従い起動・登録（ギャップがあるモードは無理に完了させない）
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
4. ACS secret 登録はまだ必須ではない。接続検証は mode 3

**完了の目安:** スタックが healthy / ready。実メールを送っていないこと。

### 3. staging ACS verification

**前提:** mode 2 相当の deploy 形が動いている。検証は**明示実行**のみ。対象は **Staging**。

**順序**

1. Preflight: 専用 tenant / 宛先 / 承認済み sender。`live_sending=true` は短時間・限定範囲
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.md)（対話のみ。CLI 引数に secret を渡さない。確認フレーズは runbook どおり **`Staging` のみ**）
3. Verification: 組織で承認された drill / 手順（例: [mail-05a drill guide](drills/mail-05a-drill-guide.html)）。ACS 単体確認 CLI は [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) で今後提供予定
4. 検証後は staging 既定どおり `live_sending=false` に戻すかを判断（WARN になり得る状態を残さない）

**完了の目安:** 明示した検証メールが ACS 経由で期待どおり処理されること。**実バウンスは不要。** platform-owned sender ファイルの存在は tenant 送信完了の根拠にしない。

### 4. production ACS

**現状の正直な範囲:** deploy テンプレートと設定の準備までは案内できる。一方、現行の `register-acs` は **Staging 確認専用**のため、production の正規 file-secret 登録手順としては使えない（`Staging` 入力の回避策も案内しない）。

**順序**

1. Preflight: production 専用 token / tenant。承認済み sender。metrics bearer 等（[deploy `.env.example`](../../infra/deploy/.env.example)）
2. Setup（できること）: deploy compose（[infra/deploy/compose.yml](../../infra/deploy/compose.yml)）の形で host を用意し、tenant JSON / token / metrics / Admin を [設定 README](../../config/mailer/README.md) に沿って揃える
3. Setup（backup・任意）: [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md)
4. Setup（ACS secret）: compose は `ACS_CONNECTION_STRING_FILE` を期待する。**production 向けに確認フレーズ付きで登録する正本 CLI / runbook は、現状この入口からリンクできない** → live send は **Blocked**、診断は `[FAIL]` + `[ACTION]`。register-acs を production 作業に流用しない
5. Verification（secret ギャップ解消前）: `/healthz` `/readyz`、no-send または受理のみなど、**実送信なしで確認できる範囲**に留める。公開 release イメージ smoke は [release-image-smoke](release-image-smoke.md)（**既定タグは公開済み版。v1.1.0 検証には使わない** → 公開 v1.1.0 未検証は `[WARN]` / `[ACTION]`）
6. bounce が不要でも、正規の production live send 完了は secret 登録ギャップ解消後

**完了の目安（現行）:** deploy 形・tenant / env preflight・health/ready までを `[PASS]` にし得る。production live send 完了は **Blocked** のため `[FAIL]` + `[ACTION]`（「使えるが要注意」の WARN にしない）。

### 5. production ACS + Event Grid / Storage Queue（目標構成）

**前提の限界:** mode 4 のギャップに加え、現行 [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) は bounce 用 env・Queue secret mount を渡さない。host に変数を置いただけではコンテナへ入らない。**このモードを現行テンプレートだけで完了させない。**

**目標の理解（実装・別対応待ち）**

1. ACS / Event Grid / Queue を **production 専用**に分離する。Push webhook（#304）は作らない
2. Azure 側: Delivery Report → Event Grid → **Storage Queue**
3. Mailer 側: [bounce ingestion runbook](bounce-ingestion-runbook.md) の `MAILER_BOUNCE_INGESTION=queue`、Queue 接続、Queue 名を、**compose（または承認済み override）経由で**渡す
4. **v1.1.0 系イメージ**（migration `011`）と公開イメージ検証は、release 完了後

**いま付けられる結果**

- deploy template 未配線 → モードは **Target only**、完了判定は `[FAIL]` + `[ACTION]`
- poll 失敗メトリクスが静かなことだけで Event Grid 配線成功としない（到着未確認は `[WARN]` / `[ACTION]`）
- [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) は**選択した environment**（dev / staging / production を含む）に対する read-only 構成確認（予定）。Staging 限定ではない
- [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) は **Staging 限定**の Delivery Report E2E / pre-production 配線確認（予定）。#428 の結果を production 実行済みの証拠として扱わない。production Queue 実行・production テスト送信は #428 の非目標
- **実バウンスは完了条件にしない**

**完了の目安（現行）:** なし（Target only）。#427 の environment 別構成確認と、#428 の Staging E2E 証跡と、production 本番構成の完了は分離する。

## 失敗時の参照先

| 症状の例 | 参照 |
|----------|------|
| tenant / token / `LIVE_SENDING_DISABLED` / provider 不足 | [設定 README troubleshooting](../../config/mailer/README.md#tenant--env-troubleshooting) |
| local 起動・Admin・Mailpit | [local Docker runbook](local-mailer-docker-runbook.md) |
| deploy 形の compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.md) |
| Staging ACS secret 登録失敗 | [register-acs CLI](register-acs-cli-runbook.md)（Staging 限定） |
| bounce / unmatched / Queue poll（runtime 説明） | [bounce ingestion](bounce-ingestion-runbook.md)、[metrics-and-alerts](metrics-and-alerts.md) |
| backup / restore | [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md) |
| 公開イメージ smoke（公開済みタグ） | [release-image-smoke](release-image-smoke.md) |

## 今後提供予定の確認機能（完成済み扱いしない）

| Issue | 予定 | 境界 |
|-------|------|------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor | — |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS 単体の実送信確認 CLI | Staging 前提の計画（Issue 本文に従う） |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | Event Grid / Storage Queue の read-only 構成確認 | **選択 environment 向け**（Staging 限定ではない）。構成確認のみ。イベント到着は保証しない |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report の Queue 到着 E2E（message ID 相関。実バウンス必須にしない） | **Staging 限定**の pre-production 配線確認。production Queue / production テスト送信は非目標 |

現時点では既存の preflight script・smoke・runbook 手動確認で進める。

## この入口の非目標

- setup CLI / doctor / Azure リソース自動作成の実装
- deploy compose への bounce 配線や production 向け register-acs 拡張（別 Issue）
- 既存 runbook 全文のこのファイルへの複製
- v1.2.0 の Consumer bounce API / webhook 契約の説明
- Event Grid Push（#304）の採用手順
- production 作業で `Staging` 確認フレーズを入力させる回避策の案内
- 実在 credential / tenant / private path の掲載
