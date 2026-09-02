[English](setup-guide.en.md)

# Amane Mailer セットアップ入口

初めて Amane Mailer を構築するときの**単一の入口**です。Easy Setup（推奨）、Manual Deployment、Hardened Deployment のどれかを選び、必要な情報を揃え、リンク先 runbook の順に進めます。この文書は**判断・順序・安全境界**の正本です。詳細手順の全文複製はせず、候補固有の SHA / digest / checksum **値**も埋め込みません。

Parent tracking: [#445](https://github.com/kooiei-in4a/amane-mailer/issues/445) · 本 Issue: [#457](https://github.com/kooiei-in4a/amane-mailer/issues/457) · Design authority: [ADR 0021](../adr/0021-easy-setup-boundaries.md) ([#446](https://github.com/kooiei-in4a/amane-mailer/issues/446))

例示は `replace-with-*`、`example.invalid`、synthetic UUID / path のみ。実在する secret、token、接続文字列、送信元・送信先、PII、host 固有 private path を docs・Issue・ログ・チャットへ貼らないでください。

## 文書の役割

| 文書 | 役割 |
|------|------|
| [README](../../README.md) / [README.en](../../README.en.md) | リポジトリの最小入口 → 本ガイド |
| **本 setup-guide** | 判断・経路選択・順序・安全境界（正本） |
| `docs/ops/` 配下の runbook | 詳細手順（リンクのみ。全文複製しない） |
| [ADR 0021](../adr/0021-easy-setup-boundaries.md) | Easy Setup の Design authority |
| [setup-release-bundle](setup-release-bundle.md) | maintainer 向け packaging / candidate handoff |
| [implementation-status](../implementation-status.json) | 機能実装状況（Easy Setup は v1.2.0 で導入され、現在も `implemented`） |
| [v1.3.6 release record](../releases/v1.3.6.md) | 現行公開 release の identities / digest / platform / smoke 証跡 |
| 候補 `README-SETUP.md` | 展開後の最小入口。候補の `sourceCommitSha` で本ガイドへリンク |

## 経路の選び方

| 経路 | 選ぶとき | 注意 |
|------|----------|------|
| **Easy Setup（推奨）** | Windows Docker Desktop または Linux Docker Engine / VPS。mode 1–4 | host の `setup assistant` / 任意の non-interactive Main apply。mode 5 は Manual |
| **Manual Deployment** | Managed bundle なしで既存 runbook / CLI を使う | mode 1–5 を維持。現行公開イメージは **v1.3.6**（過去 release の記録は残置） |
| **Hardened Deployment** | file secret / owner-only / Managed metadata なしを厳密に | Easy Setup assistant は**使わない**。Manual 契約が土台 |

---

## Easy Setup（推奨）

Easy Setup は既存の `.env` / `tenants.json` / file secret / deploy compose 契約を、host 上の local Web または terminal assistant で包みます（[ADR 0021](../adr/0021-easy-setup-boundaries.md)）。**v1.2.0 で導入され、現在も `implemented`**（[#445](https://github.com/kooiei-in4a/amane-mailer/issues/445) / [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458)）。これは機能の導入履歴であり、現行公開 release の version 表示ではありません。Managed 活性化なしで進めたい場合は Manual 経路を使ってください。

### プラットフォーム別の開始

| 環境 | 最初に実行するコマンド（展開した host bundle または同等レイアウトから） |
|------|------------------------------------------------------------------------|
| Windows Docker Desktop | `Amane.Mailer.exe setup assistant` |
| Linux GUI + Docker Engine | `./Amane.Mailer setup assistant` |
| headless Linux / VPS | `./Amane.Mailer setup assistant --no-browser` または `./Amane.Mailer setup assistant --terminal` |
| VPS への SSH | `--terminal` を推奨。または assistant の loopback port へ SSH tunnel してローカルブラウザを使う。**VPS 上のブラウザーだけでは完結しない** |
| Offline / GitHub 不可 | `Amane.Mailer setup assistant --help` のあと `--terminal`（Windows は `Amane.Mailer.exe`） |
| non-interactive（Main のみ） | `Amane.Mailer setup apply --config <absolute-path> --non-interactive` |

使用する CLI は次のみ（別名を発明しない）:

```text
Amane.Mailer setup assistant [--port <n>] [--no-browser] [--terminal]
Amane.Mailer setup apply --config <absolute-path> --non-interactive
```

`--port` は localhost Web の listen port。`--no-browser` はブラウザ起動を抑止。`--terminal` は対話式 terminal UI です。

### 候補の消費（検証方法）

Easy Setup **release-candidate** host bundle（公開 GitHub Release ではない）を使う場合:

#### リリース候補の資格確認（#456）

検証**方法**のみを示します。本ガイドに固定 digest を権威として埋め込みません:

- 外側の `CANDIDATE-SHA256SUMS` は展開前／展開時の**アーカイブ自体**を検証する
- 内側の `FILES-SHA256SUMS` は**展開後のファイル**を検証する
- `release-bundle-manifest.json` で `sourceCommitSha`、image digest、schema 範囲を読む
- manifest の `payloadTreeSha256` は staged payload の tree digest であり、**アーカイブ checksum ではない**
- handoff 資料が食い違う場合は**停止**する。handoff は qualification 専用（maintainer #456）であり、利用者環境の「本番検証済み」印ではない

packaging maintainer 手順: [setup-release-bundle](setup-release-bundle.md)。オペレーター判断の正本は本ガイドです。

#### 公開リリース利用者

公開済み **v1.3.6** は GitHub Release の checksum / [release record](../releases/v1.3.6.md) / 公開 image digest を使います（<https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.6>）。candidate handoff と公開リリース検証を混同しないでください。公開 runtime image の platform は release record の記載（現在は `linux/amd64`）に従います。公開 asset の有無や host platform を、記載のない形で補わないでください。

### Managed 境界

- 設定 bundle は immutable。**活性化の唯一権威は `ACTIVE`**（`bundleId` + 単調増加の `activationGeneration`）
- **configuration fingerprint** は non-secret 設定の同一性のみ。**bundle integrity** は sealed な secret-valued env と file secret を含む。fingerprint 一致を secret 込みの全体一致とみなさない
- **recorded** metadata と **effective** runtime 検査は別。metadata は送信正本にしない
- 同一 root で Managed Setup と Manual Deployment を混在させない（`ACTIVE`／metadata と ad-hoc Manual `.env` を二重権威にしない）
- seal と secret を同時に書き換えられる特権 host 管理者は Easy Setup の保護対象外

#### Managed backup 境界

| 対象 | 扱い |
|------|------|
| SQLite DB | [バックアップ運用](backup-operations.md) 等で**別途**取得。config rollback では戻さない |
| Managed root | 同一世代として `bundles` / `state`（`ACTIVE`）/ `verification` / `sealing` を保全する |
| external / manual-only | data path、backup 設定、rclone 設定などは **Managed 切替の外**で別管理 |
| 文書・ログ | secret 値や private host path を載せない |

詳細手順は複製しない: [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md)。

#### Managed failure / recovery

| 状況 | 扱い |
|------|------|
| previous `ACTIVE` がある | atomic に previous へ切替 → コンテナ recreate → fingerprint / integrity / verification 再確認が成功して初めて rollback 成功 |
| previous `ACTIVE` がない | **FreshFailed**。成功した rollback として提示しない |
| lock / `TX.stamp` / 不完全な `ACTIVE` / FINALIZED 不一致 | 成功扱いにしない。recovery または手動介入 |
| migration・Admin SQLite・mail data・provider 副作用 | **config rollback の範囲外** |
| `docker compose down -v` / DB migration rollback | **案内しない** |

#### Secret 検知の範囲と限界

| 契約 | 意味 |
|------|------|
| non-secret fingerprint | secret **値**を含めない |
| finalized Managed bundle の secret | integrity seal 対象（値は公開面に出さない） |
| 誤 mount / 差し替え | runtime の mount attestation などで検知し得る |
| fingerprint 一致のみ | secret 込みの bundle 全体一致を意味**しない** |
| 特権 host が seal + secret を同時改ざん | Easy Setup の保護対象外 |

### Deployment 状態

| 状態 | 意味 |
|------|------|
| **configuration applied** | Managed bundle が `ACTIVE` 経由で commit された |
| **send-ready** | 適用 bundle が send-ready 条件（effective / doctor / readiness / fingerprint / integrity / verification record 整合）を満たす |
| **deployment operational verification** | 利用者が通常 Mailer 経路で実送信を確認した状態。**Easy Setup では記録しない** — 必要なら Manual verification |
| **Release Production operational verification** | maintainer #456 の製品 qualification。**利用者環境の状態ではない** |

Staging 試験と Production は ACS / Queue / token を環境分離する。Staging drill を Production 証拠にしない。

### Admin（任意・既定 disabled）

- Admin 有効化は**任意**で**既定 disabled**。主セットアップ成功後の**独立した任意 transaction**
- bootstrap は対話式 Web または terminal のみ。**non-interactive では行わない**
- non-interactive の Main apply は Admin disabled のまま。入力で Admin 有効化が指定されたら黙って無視せず **FAIL** し、対話式 Assistant へ案内する
- 平文 password を file / redirected stdin / CLI 引数から受け取らない。password hash file input は現行 setup contract の対象外
- 対象 DB 状態は **fresh** と **managed same-user** 再適用のみ。既存 Manual / unsupported は Manual 経路
- config bundle rollback と SQLite Admin 状態（`admin_config` / `admin_users` / session）の rollback は同一視しない
- bootstrap 成功には login と `/admin/setup-status` 表示まで含む。Admin setup status に doctor / テスト送信 / Docker 操作はない

#### Admin access profile

| Profile | 条件（要約） |
|---------|--------------|
| **Local Development** | `ASPNETCORE_ENVIRONMENT=Development`、loopback のみ host 公開、`AMANE_ADMIN_ALLOW_HTTP=true`、localhost アクセス、`Connection.LocalIpAddress` が `AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` と一致 |
| **Production HTTPS** | 承認済み HTTPS reverse proxy が**既に存在**、`AMANE_ADMIN_ALLOW_HTTP=false`、Secure / `__Host-` cookie、server-side local address が allowed local address と一致、Admin の Internet 直公開なし |

reverse proxy が TLS を終端し Mailer へ平文 HTTP で転送する場合は、compose / `external.env` で `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` を設定する。`X-Forwarded-Proto` により Admin antiforgery が HTTPS 扱いになり、Secure cookie が成立する。信頼できる proxy 境界の背後でのみ有効化する。

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` はクライアント送信元 IP ではなく、**`Connection.LocalIpAddress`**（server-side）を照合します。

Easy Setup は reverse proxy・証明書・DNS を**自動構築しない**。Production HTTPS 経路がなければ **Admin を disabled のまま**にする。Main セットアップは成功可能です。

### mode・サポート行列・setup ≠ upgrade

| mode 1–4 | Easy Setup 正式対象 |
|----------|---------------------|
| mode 5（production ACS + Event Grid / Storage Queue） | **Manual / Easy Setup 対象外** |
| Windows Docker Desktop / Linux Docker Engine / VPS | 正式 |
| NAS | best-effort |
| remote Docker / Kubernetes / Podman / macOS 正式配布 | 対象外 |
| Consumer bounced Webhook [#307](https://github.com/kooiei-in4a/amane-mailer/issues/307) | 現行 v1.3.6 に含まれない。将来候補であり、release promise ではない |

**setup と upgrade は別操作です。** Easy Setup は初回／managed のセットアップ向けです。既存 Manual / Hardened 配備の製品 upgrade は、公開イメージの pull と通常の SQLite migration 適用で行います（Admin の silent re-bootstrap ではありません）。

**Historical note — v1.1.0 → v1.2.0 の DB migration（INCLUDE）:** backup のうえ、当時のランタイムが次を適用しました（`none` / 省略は不可）。これは過去 release の記録であり、現行 v1.3.6 の setup prerequisite ではありません。

- `012_provider_event_inbox_details.sql`
- `013_provider_queue_dead_letters.sql`

過去 release の identities: [docs/releases/v1.2.0.md](../releases/v1.2.0.md)。
現行 release は [docs/releases/v1.3.6.md](../releases/v1.3.6.md) と [`release/current-public.json`](../../release/current-public.json) を参照してください。

### backup / rollback / recovery（概要）

- 上の Managed backup / failure・recovery / secret 検知表を優先する
- DB と運用者が所有する secret / config の文書化された backup を優先。runbook が除外するものは除外する
- 安易な rollback に `docker compose down -v` を使わない（volume 破壊）
- DB migration rollback を Easy Setup のサポート済み recovery としない
- 詳細: [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md)

### Easy Setup トラブルシュート指針

- assistant が起動・bind しない: host binary、Docker Desktop/Engine の local context、loopback 前提を確認
- VPS で「ブラウザーだけ」: `--terminal` または SSH tunnel へ切替
- non-interactive で Admin 有効化要求: 期待どおり **FAIL** — 対話式 Assistant を使う
- fingerprint 一致でも secret 誤り／誤 mount: integrity / mount attestation はなお FAIL し得る。fingerprint だけでは不十分
- Manual 寄りの失敗参照: 下の [トラブルシューティング](#トラブルシューティング)

### 文書不備の戻り先（#456 → #457）

qualification（#456）で本ガイドまたは候補 `README-SETUP.md` の文書不備が見つかった場合:

1. [#457](https://github.com/kooiei-in4a/amane-mailer/issues/457)（本ドキュメント / packaging 生成）で修正する
2. **新しい merge SHA** から candidate を再生成する
3. 影響する qualification シナリオを再実行する
4. #456 の Hard gate 表を本ガイドへ**貼らない**（正本は #456）

---

## Manual Deployment

Manual Deployment は第一級の経路のままです。以下は mode 1–5 の runbook 順と完遂可否の意味を維持します。**現行の推奨公開イメージは v1.3.6** です。bounce Queue 採用など v1.1.0 由来の機能境界の説明は歴史的事実として残します。

コンテナ one-shot の effective inspection（`Amane.Mailer setup inspect-effective --format json`、[#447](https://github.com/kooiei-in4a/amane-mailer/issues/447)）は Managed host 向けに実装済みです。stdout は JSON のみ。recorded／effective／mountAttestation は分離し、one-shot 単独では最終 `bundleIntegrity=matched` を主張しません。host assistant／ACTIVE 適用は、この Manual 手順を削除しません。

### 既存 Manual 文書の役割（複製しない）

| 文書 | 役割 | Manual 入口との関係 |
|------|------|------------------|
| [Zero-Admin 初回メール quickstart](first-mail-quickstart.md) | **local Mailpit** の最短手順 | mode 1 の詳細正本 |
| [local Docker runbook](local-mailer-docker-runbook.md)（[bash](local-mailer-docker-runbook-bash.md)） | local の追加 smoke（冪等・Admin など） | mode 1 の拡張 |
| [local deploy rehearsal](local-deploy-rehearsal-runbook.md) | deploy 形スタックの再現 | mode 2 の詳細正本 |
| [register-acs CLI](register-acs-cli-runbook.md) | ACS file secret 登録（確認は exact `Staging` または `Production`） | mode 3 は `Staging`、mode 4 は `Production`。確認フレーズを取り違えない |
| [test-acs-send CLI](test-acs-send-cli-runbook.md) | Staging 限定の ACS 単体実送信確認 | mode 3 の検証正本 |
| [bounce ingestion](bounce-ingestion-runbook.md) | Queue Pull の runtime 設定・運用 | mode 5 の設定キー正本。deploy compose 経由で渡す |
| [event-grid config check](event-grid-config-check-runbook.md) | Event Grid / Queue の read-only 構成確認 | environment 別。到着は保証しない |
| [verify-delivery-report](verify-delivery-report-runbook.md) | Delivery Report の Queue 到着 E2E | **Staging 限定**。production 証拠にしない |
| [設定 README](../../config/mailer/README.md) | tenant / env / preflight | 全モードの設定 shape 正本 |
| [release-image-smoke](release-image-smoke.md) | 公開イメージ smoke | 公開済みタグ向け。現行例は `v1.3.6` |

### 読む前に（安全）

- secret、接続文字列、実テナント token、送信元・送信先、PII、provider raw error を docs・Issue・ログ・チャットへ貼らない。
- placeholder（`replace-with-*`、`local-mail-service-token`）だけを例に使う。
- Event Grid **Push** webhook（[#304](https://github.com/kooiei-in4a/amane-mailer/issues/304)）は v1.1.0 の採用方式ではない。案内しない。
- v1.1.0 の bounce transport は **Storage Queue Pull のみ**（`MAILER_BOUNCE_INGESTION=queue`）。
- **実バウンスの発生確認は、通常セットアップの完了条件にしない。**

### 公開イメージについて（現行 v1.3.6）

**現行推奨:** 公開 GitHub release / GHCR タグ `v1.3.6`。Easy Setup と Manual の両方でこのタグを正とします。current public version / tag / platform の source of truth は [`release/current-public.json`](../../release/current-public.json) です。
証跡は [docs/releases/v1.3.6.md](../releases/v1.3.6.md)（release-image smoke 含む）および
<https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.3.6> を参照してください。現行 runtime image は `linux/amd64` のみです。

既存配備の upgrade は setup とは別操作です。イメージ pull や migration の前に DB backup を取り、対象 release の release record と通常の migration 手順を確認してください。過去の `v1.1.0 → v1.2.0` migration の詳細は上の historical note と [v1.2.0 release record](../releases/v1.2.0.md) に残します。

**前の公開 release:** `v1.3.5` の証跡は [docs/releases/v1.3.5.md](../releases/v1.3.5.md) に残します。さらに古い release の migration / feature boundary も各 release record に残します。
local build や develop 由来の成果物で手順を追う場合はその旨を運用記録に残してください。

[release-image-smoke](release-image-smoke.md) の現行例は公開済み release（`v1.3.6`）向けです。検証対象 tag は必ず明示してください。

### 現時点で完了できない構成（正直な境界）

次は構成の説明上残るが、**tenant 実送信の完了条件には含めない**項目である。

| ギャップ | 現状 | モード完遂可否 | 診断時の扱い |
|----------|------|----------------|--------------|
| platform-owned sender | `register-acs` が `platform-sender.json` も書くが、現時点では tenant の ACS 送信経路からは使われない | tenant 実送信の完了条件に含めない | tenant 送信完了の根拠にしない |

production ACS（mode 4）の file-secret 登録は `admin provider register-acs` の exact **`Production`** 確認で Available。production 作業で `Staging` と入力させる使い方は**禁止**（CLI は staging 登録として受理するため production 証跡にならない。`setup doctor --mode production-acs` は不一致を `[FAIL]` する）。

production ACS + Queue（mode 5）は [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) 経由で `MAILER_BOUNCE_INGESTION` / Queue 名 / Queue 接続（file）をコンテナへ渡せるため **Available**。host shell にだけ変数を置いてもコンテナへは入らない。

### モード完遂可否と結果コード（分離）

構成が今完了できるかどうか（モード表の列）と、診断 CLI の結果コードは別レイヤとする。setup doctor / 確認 CLI（[#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)）は下の結果コード意味に合わせる。既存 smoke script は主に `[PASS]` / `[FAIL]` を出す。

#### モード完遂可否（構成の提供状況）

| 値 | 意味 |
|----|------|
| **Available** | 現行の正本 runbook / CLI / deploy テンプレートだけで完遂できる |
| **Blocked** | 目標モードだが、必須経路が欠けており今は完遂できない |
| **Target only** | 目標像の説明のみ。現行テンプレートでは完了扱いにしない |

#### 結果コード（診断出力）

| コード | 意味 | 次にすること |
|--------|------|----------------|
| **PASS** | 機械的に確認済み。その確認項目は意図どおり満たされている | 次の確認または次モードへ進む |
| **FAIL** | セットアップを進められない不整合、または必須前提不足 | 停止する。完了不能な必須ギャップも含む（「注意すれば使える」ではない） |
| **WARN** | **動作可能**だが、人間の確認やリスク判断が必要 | 記録し、人手で確認する。完了不能を WARN にしない |
| **ACTION** | 次に行う安全な操作（ツールは自動修正しない） | 表示された手順を人が実行する。手順が存在しない項目は推測で埋めない |

代表例:

| 状態 | モード完遂可否 | 診断 |
|------|----------------|------|
| production ACS secret 未登録（確認フレーズ取り違え含む） | Available（手順はある） | `[FAIL]` または `[ACTION]`（`Production` 確認の register-acs） |
| bounce mode / Queue secret / Queue 名の不足（mode 5） | Available（手順はある） | `[FAIL]` または `[ACTION]`（compose 経由の設定） |
| Queue poller は動くが Event Grid 到着未確認 | （モードによる） | `[WARN]` または `[ACTION]` |
| 公開 v1.3.6 イメージ未検証 | （モードによる） | [v1.3.6 release record](../releases/v1.3.6.md) を参照。未追従ホストは `[WARN]` / `[ACTION]` |

secret 値・宛先平文・接続文字列・raw provider error を結果に含めない。不足は「どの設定キー / どの権限能力が欠けているか」だけを示す。

`mail_provider_queue_poll_failed_total` が増えないことだけでは、Event Grid → Queue 配線の成功判定にしない（poller が動いてもイベント未到着があり得る → `[WARN]` / `[ACTION]`）。

### 構成モードを選ぶ

次の質問で **1 つだけ**選ぶ。

1. 実メールを送らず、Docker 上で 1 通届くところまで確認したい → **local Mailpit**
2. deploy 形のスタックを組み、ACS 実送信はまだしない → **staging ACS no-send**
3. staging で ACS 接続・sender を、明示した短時間だけ検証する → **staging ACS verification**
4. 承認済み sender で本番配送する（bounce 取り込みはまだ不要） → **production ACS**
5. 本番配送に加え、Delivery Report を Queue 経由で取り込む → **production ACS + Event Grid / Storage Queue**

| モード | 想定用途 | provider | `live_sending` | bounce mode | 完遂可否（現行正本） | 主に使う正本 |
|--------|----------|----------|----------------|-------------|----------------------|--------------|
| local Mailpit | 初回到達確認、開発 smoke | `mailpit` | `false` | `off`（既定） | **Available** | [Zero-Admin 初回メール quickstart](first-mail-quickstart.md)、[local Docker runbook](local-mailer-docker-runbook.md) |
| staging ACS no-send | deploy 形の起動・token / migrate 確認。実送信なし | `acs`（または JSON どおり） | `false` | 通常 `off` | **Available**（実送信なし） | [local deploy rehearsal](local-deploy-rehearsal-runbook.md)、[設定 README](../../config/mailer/README.md) |
| staging ACS verification | ACS 接続と承認 sender の**明示**検証 | `acs` | 検証中のみ `true`（専用 tenant / 宛先） | 通常 `off` | **Available**（Staging） | [register-acs CLI](register-acs-cli-runbook.md)（確認 **`Staging`**）、[test-acs-send CLI](test-acs-send-cli-runbook.md)、[設定 README](../../config/mailer/README.md) |
| production ACS | 本番配送 | `acs` | `true`（承認済みのみ） | `off` 可 | **Available** | [register-acs CLI](register-acs-cli-runbook.md)（確認 **`Production`**）、[deploy `.env.example`](../../infra/deploy/.env.example)、[compose.yml](../../infra/deploy/compose.yml)、[設定 README](../../config/mailer/README.md) |
| production ACS + Queue | 本番配送 + ハードバウンス抑制 | `acs` | `true` | **`queue` のみ** | **Available** | [bounce ingestion runbook](bounce-ingestion-runbook.md)、[deploy `.env.example`](../../infra/deploy/.env.example)、[compose.yml](../../infra/deploy/compose.yml)、[register-acs CLI](register-acs-cli-runbook.md)（確認 **`Production`**） |

### provider / `live_sending` / bounce mode

| 組合せ | 実メール | 受理・永続化 | 備考 |
|--------|----------|--------------|------|
| `mailpit` + `live_sending=false` | なし（Mailpit へ） | する | local の既定。安全な初回確認向き |
| `acs` + `live_sending=false` | **送らない** | する（実送信ゲートで止まる） | staging no-send。`LIVE_SENDING_DISABLED` になり得る |
| `acs` + `live_sending=true` | **送る** | する | 承認済み sender + 登録済み ACS secret が必須 |
| bounce `off` | — | — | v1.0 互換の既定。取り込みしない |
| bounce `queue` | — | — | v1.1.0 採用。Storage Queue Pull のみ。deploy compose 経由で設定を渡す |
| bounce `webhook` | — | — | **未実装（#304）。設定すると起動失敗。採用しない** |

`MAILER_PROVIDER` / `Mailer__Provider` は全 tenant の provider を上書きする。意図しない上書きに注意（[設定 README](../../config/mailer/README.md)）。

#### ACS secret と platform-owned sender の境界

| 対象 | 何をするか | いま使える場面 |
|------|------------|----------------|
| tenant ACS 配送用 connection string（file） | Staging/Production deploy の `ACS_CONNECTION_STRING_FILE` が参照する file secret | [register-acs CLI](register-acs-cli-runbook.md) で exact **`Staging`** または **`Production`** を確認して登録する |
| `platform-sender.json` | System Admin 向け platform-owned sender 情報 | 同 CLI が書くが、**現行 runtime の tenant 送信経路では未使用**。tenant 実送信完了の根拠にしない |

production オペレーターに、production 作業なのに確認欄へ `Staging` と書かせる案内はしない。

### 責任境界

| コンポーネント | 責任 | 非責任 |
|----------------|------|--------|
| **ACS Email** | メール送信の引き受け、Delivery Report の発行 | Mailer DB の抑制リスト管理 |
| **Event Grid** | ACS Delivery Report を購読し、**Storage Queue** へ配送 | Mailer への HTTPS Push（v1.1.0 では使わない） |
| **Storage Queue** | イベントの一時保管（at-least-once） | 相関・抑制・PII マスク |
| **Mailer** | 送信依頼の受理、Worker 配送、Queue Pull、相関、`mail_suppressions`、Admin / metrics 可視化 | Azure リソースの自動作成、実バウンスの強制発生、host shell だけの env をコンテナ設定とみなすこと |

環境（dev / staging / production）ごとに **ACS と Queue を分離**する。混線すると `provider_message_id` 誤相関の原因になる（[bounce runbook](bounce-ingestion-runbook.md)）。

### local / staging / production の安全境界

| | local | staging | production |
|--|-------|---------|------------|
| 実送信 | しない（Mailpit） | 既定しない。verification のみ明示 | 承認済みのみ。`register-acs` で exact `Production` 確認 |
| token / `tenant_id` | example / local 専用 | non-production 専用 | production 専用。staging と共有しない |
| ACS secret | local drill は bare env 可（runbook 参照） | file secret（`register-acs`、確認は `Staging`） | file secret（`register-acs`、確認は **`Production`**。`Staging` 流用禁止） |
| Admin | 任意・内部 NW | 任意・到達制限必須 | 任意・到達制限必須（公開 Internet 直出し禁止） |
| bounce Queue | 通常不要 | [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) の `setup check-event-grid` で environment 別の read-only 構成確認。[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) は Staging E2E のみ | Available。compose 経由で `queue` + Queue 名 + file secret |
| 完了の定義 | health + 1 通 Mailpit 到着など | 起動・preflight・（任意）明示 verification | deploy 形 + production 確認付き secret 登録 + 承認済み live send。実バウンスは不要 |

### 共通チェックリスト（必要情報・権限・secret・network）

値そのものは書かず、「用意できているか」だけ確認する。

#### 情報

- [ ] 使う構成モード（上表の 1 つ）。mode 4 / 5 は production 固有の安全境界（専用 token / ACS・Queue 分離、Push 非採用）を理解したうえでの選択。公開イメージは `v1.3.6` を正とする（[release record](../releases/v1.3.6.md)）。version / tag の更新時は [`release/current-public.json`](../../release/current-public.json) を確認する
- [ ] tenant JSON の置き場所（example をコピーした **未コミット** ファイル）
- [ ] 各 tenant の `token_env` 名と、対応する環境変数を設定する場所
- [ ] 実効 provider（tenant JSON または `MAILER_PROVIDER`）
- [ ] `live_sending` の意図（false / 明示 true）
- [ ] bounce mode（`off` または `queue`）
- [ ] Admin / metrics / backup を有効にするか（既定オフまたは runbook のとおり）

#### Azure 側で必要な能力（mode 2 以降。具体ロール名は組織の IAM に従う）

- [ ] ACS Email リソースを参照し、承認済み sender / domain を確認できる
- [ ]（mode 3）deploy host で `admin provider register-acs` を実行できる（対話 TTY、secret ディレクトリ権限、確認フレーズ **`Staging`**）
- [ ]（mode 4）deploy host で同 CLI を実行できる（確認フレーズ **`Production`**。production 作業で `Staging` と入力しない）
- [ ]（mode 5）Delivery Report を Event Grid で購読し、エンドポイントを **Storage Queue** にできる
- [ ]（mode 5）対象 Queue の接続情報を、**compose 経由で** Mailer コンテナへ渡せる（`.env` + secret file mount。host shell だけでは不十分）

#### secret（置き場所だけ。値は記録しない）

- [ ] tenant Bearer token（環境変数。JSON 平文禁止）
- [ ]（Staging ACS live）`register-acs`（確認 `Staging`）が書く `ACS_CONNECTION_STRING_FILE` 経路の file secret
- [ ]（production ACS）`register-acs`（確認 **`Production`**）が書く同経路の file secret
- [ ]（mode 5）Queue 接続文字列を `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` に置く（値は記録しない。compose が file としてマウント）
- [ ]（mode 5）`.env` で `MAILER_BOUNCE_INGESTION=queue` と `MAILER_BOUNCE_QUEUE_NAME` を設定する
- [ ]（metrics 有効時）scrape bearer
- [ ]（Admin 有効時）password hash など Admin 秘密

#### network / runtime

- [ ] Docker（local / rehearsal）または deploy host の compose ネットワーク
- [ ] Mailer HTTP（health / ready）と、local なら Mailpit UI/API
- [ ] production では reverse proxy / firewall 等の到達境界（Admin 直公開なし）
- [ ]（mode 5）Mailer から Storage Queue への**外向き**到達（公開 HTTPS 受信口は不要）

### setup doctor（read-only 診断）

セットアップ前または起動失敗時に、ローカル設定と host 前提を **read-only** で診断する CLI です。設定ファイル、DB、container、Azure リソースは変更しません。

```bash
dotnet Amane.Mailer.dll setup doctor --mode <mode> [--compose-file <path>]
```

| `--mode` | 用途 |
|----------|------|
| `local-mailpit` | local Mailpit 初回到達 |
| `staging-no-send` | deploy 形・no-send |
| `staging-verification` | Staging ACS 明示検証 |
| `production-acs` | production deploy 形（register-acs は **`Production`** 確認） |
| `production-queue` | production + Queue（compose 経由の `queue` 設定） |

結果コードは上表（PASS / FAIL / WARN / ACTION）に従います。末尾に `Summary: PASS=… FAIL=… WARN=… ACTION=…` を表示します。`FAIL` が 1 件でもあれば exit code `1` です。

- secret 値・token・接続文字列・宛先平文・raw provider error は出力しません
- DB migration 実行、container 起動、ACS 実送信は行いません。Azure Event Grid / Queue の構成確認は別コマンド `setup check-event-grid`（[#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) / [event-grid-config-check-runbook.md](event-grid-config-check-runbook.md)）
- ACS secret ディレクトリの書き込み確認は `admin provider check-acs-preflight` を使用（doctor は read-only の安全チェックのみ）
- compose 検証は `docker compose config --quiet` を **ACTION** として案内（host 上で人が実行）

deploy host では、Docker CLI と公開 host port の意味が正確になるよう、**host 上**で同コマンドを実行することを推奨します（コンテナが使う env / compose と同じ前提で）。Mailer コンテナ内で実行する場合、Docker 利用可否と loopback port 確認はコンテナ namespaceしか見えないため WARN / ACTION になります。

### 実行順序（全モード共通）

1. **Preflight** — モード選択、チェックリスト、**setup doctor**（上）、tenant / env の shape 確認（[設定 README Preflight](../../config/mailer/README.md#preflight)）
2. **Setup** — 該当モードの正本 runbook に従い起動・登録（ギャップがあるモードは無理に完了させない）
3. **Verification** — health / ready、受理、（モードに応じた）配送または no-send 確認。結果コードは上表
4. **Troubleshooting** — FAIL / WARN 時は [トラブルシューティング](#トラブルシューティング) へ。自動修正はしない（ACTION）

### モード別の一本道

#### 1. local Mailpit

**順序**

1. Preflight: Docker 起動、port 空き（quickstart の前提）
2. Setup / Verification: [Zero-Admin 初回メール quickstart](first-mail-quickstart.md)（自動 smoke: `scripts/local-first-mail-smoke.ps1` / `.sh`）
3. 追加 smoke（冪等・conflict・Admin など）: [local Mailer Docker runbook](local-mailer-docker-runbook.md) / [bash 版](local-mailer-docker-runbook-bash.md)

**完了の目安:** `[PASS]` で health / ready / 1 通 Mailpit 到着。ACS・bounce・実バウンスは不要。

#### 2. staging ACS no-send

**順序**

1. Preflight: [設定 README](../../config/mailer/README.md) と `tenants.shared.example.json` 系。`live_sending=false` を維持
2. Setup: [local deploy rehearsal](local-deploy-rehearsal-runbook.md)（`infra/deploy` の `.env` / `tenants.json` はコミットしない）
3. Verification: compose health、migrate、`/healthz` `/readyz`。実送信しない（no-send smoke は rehearsal の案内に従う）
4. ACS secret 登録はまだ必須ではない。接続検証は mode 3

**完了の目安:** スタックが healthy / ready。実メールを送っていないこと。

#### 3. staging ACS verification

**前提:** mode 2 相当の deploy 形が動いている。検証は**明示実行**のみ。対象は **Staging**。

**順序**

1. Preflight: 専用 tenant / 宛先 / 承認済み sender。`live_sending=true` は短時間・限定範囲
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.md)（対話のみ。CLI 引数に secret を渡さない。mode 3 の確認フレーズは **`Staging` のみ**）
3. Setup doctor（再実行）: `setup doctor --mode staging-verification`。`[PASS] platform_sender_environment`（expected `staging`）を確認。不一致なら `[FAIL]` — live send に進まない
4. Verification: [ACS 単体実送信確認 CLI](test-acs-send-cli-runbook.md)（`admin provider test-acs-send`。Staging + `MAILER-ACS-TEST-SEND`。Mailer API / Worker は通さない）。組織 drill が必要な場合の補助: [mail-05a drill guide](drills/mail-05a-drill-guide.html)
5. 検証後は staging 既定どおり `live_sending=false` に戻すかを判断（WARN になり得る状態を残さない）

**完了の目安:** 明示した検証メールが ACS 経由で期待どおり処理されること。**実バウンスは不要。** platform-owned sender ファイルの存在は tenant 送信完了の根拠にしない。

#### 4. production ACS

**範囲:** deploy テンプレートと設定に加え、`admin provider register-acs` の exact **`Production`** 確認で file secret を登録できる。production 作業で `Staging` と入力する回避策は案内しない（`Staging` は staging 登録として受理されるため production 証跡にならず、`setup doctor --mode production-acs` は `environment` 不一致を `[FAIL]` する）。

**順序**

1. Preflight: production 専用 token / tenant。承認済み sender。metrics bearer 等（[deploy `.env.example`](../../infra/deploy/.env.example)）
2. Setup doctor（登録前）: `setup doctor --mode production-acs`（[#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)）。production 登録は `[ACTION] production_register_acs`（この時点では `platform-sender` 未作成のため環境一致は未判定）
3. Setup（スタック）: deploy compose（[infra/deploy/compose.yml](../../infra/deploy/compose.yml)）の形で host を用意し、tenant JSON / token / metrics / Admin を [設定 README](../../config/mailer/README.md) に沿って揃える
4. Setup（backup・任意）: [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md)
5. Setup（ACS secret）: [register-acs CLI runbook](register-acs-cli-runbook.md)（確認フレーズ **`Production`**。CLI 引数に secret を渡さない）
6. Setup doctor（再実行）: `setup doctor --mode production-acs`。`[PASS] platform_sender_environment`（expected `production`）を確認してから live send へ進む。`Staging` 確認で登録した場合はここで `[FAIL]`
7. Verification: `/healthz` `/readyz`、承認済み sender での明示 live send。公開 release イメージ smoke は [release-image-smoke](release-image-smoke.md)（現行例 `v1.3.6`。証跡は [v1.3.6 release record](../releases/v1.3.6.md)）
8. bounce 取り込みが必要なら mode 5 へ進む（不要ならここで完了してよい）

**完了の目安:** deploy 形・tenant / env preflight・`Production` 確認付き secret 登録・doctor 再実行での `platform_sender_environment` PASS・health/ready・承認済み live send を `[PASS]` にし得る。公開イメージは `v1.3.6`（[release record](../releases/v1.3.6.md)）。

#### 5. production ACS + Event Grid / Storage Queue

**範囲:** mode 4 に加え、[`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) 経由で bounce Queue 設定を Mailer コンテナへ渡せる。host shell にだけ変数を置いてもコンテナへは入らない。Push webhook（#304）は作らない。Easy Setup では本 mode は **Manual**（assistant 自動化対象外）。

**順序**

1. Preflight: mode 4 と同じ production 専用 token / tenant / 承認済み sender。加えて production 専用 ACS / Event Grid / Storage Queue を分離する
2. Setup doctor（登録前）: `setup doctor --mode production-queue`（[#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)）
3. Setup（スタック + ACS）: mode 4 の手順（deploy compose・`Production` 確認の register-acs・doctor 再実行）
4. Setup（bounce）: [bounce ingestion runbook](bounce-ingestion-runbook.md) に従い、`.env` で `MAILER_BOUNCE_INGESTION=queue` と `MAILER_BOUNCE_QUEUE_NAME` を設定し、Queue 接続文字列を `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` に置く（CLI 引数に secret を渡さない）
5. Setup（Azure）: Delivery Report → Event Grid → **Storage Queue**（Push ではない）。`setup check-event-grid`（[#427](https://github.com/kooiei-in4a/amane-mailer/issues/427)）で read-only 構成確認
6. Setup doctor（再実行）: `setup doctor --mode production-queue`。`[PASS] compose_bounce_wiring` / `mode_bounce_queue` / `bounce_queue` を確認
7. Verification: `/healthz` `/readyz`、承認済み live send。Staging での Delivery Report 到着確認は `setup verify-delivery-report`（[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)）— production 実行済みの証拠にはしない。公開イメージは `v1.3.6`（[release record](../releases/v1.3.6.md)）

**結果の付け方**

- poll 失敗メトリクスが静かなことだけで Event Grid 配線成功としない（到着未確認は `[WARN]` / `[ACTION]`）
- [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) は**選択した environment**（dev / staging / production を含む）に対する read-only 構成確認。Staging 限定ではない
- [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) は **Staging 限定**。#428 の結果を production 実行済みの証拠として扱わない
- **実バウンスは完了条件にしない**

**完了の目安:** mode 4 の完了条件に加え、compose 経由の `queue` 設定・Queue file secret・Queue 名・Event Grid → Queue の構成確認を `[PASS]` / 人手確認できること。公開イメージは `v1.3.6`（[release record](../releases/v1.3.6.md)）。

### Manual 確認機能の提供状況

| Issue | 機能 | 境界 |
|-------|------|------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor | **提供済み**（上「setup doctor」） |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS 単体の実送信確認 CLI | **提供済み** — [test-acs-send-cli-runbook.md](test-acs-send-cli-runbook.md)（Staging 限定） |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | Event Grid / Storage Queue の read-only 構成確認（`setup check-event-grid`） | **提供済み** — [event-grid-config-check-runbook.md](event-grid-config-check-runbook.md)（選択 environment 向け。到着は保証しない） |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report の Queue 到着 E2E（message ID 相関。実バウンス必須にしない） | **提供済み** — [verify-delivery-report-runbook.md](verify-delivery-report-runbook.md)（**Staging 限定**。production Queue / production テスト送信は非目標） |

Manual セットアップとしては上記 CLI と既存 preflight / smoke / runbook 手動確認で進める。

---

## Hardened Deployment

Easy Setup assistant を使わず、厳格な host 制御が必要なときに Hardened Deployment を選びます。

- **Manual** 契約（mode・runbook・file secret・compose）を土台にする
- Managed root / `ACTIVE` / Easy Setup metadata を**作らない**
- file secret と owner-only 権限を優先し、`.env` / tenants / secrets / DB / backup を方針に応じて**分離した**置き場所に保つ
- remote Docker、Mailer コンテナへの Docker socket 委譲、文書化された deploy テンプレート外の任意 Compose は使わない
- Production Admin は HTTPS のみ。`AMANE_ADMIN_ALLOW_HTTP=false`
- TLS 終端 reverse proxy → Mailer HTTP upstream の場合は `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`（compose 契約。信頼できる proxy 境界のみ）

CLI 例（exact）:

```text
Amane.Mailer setup doctor --mode <mode>
Amane.Mailer admin provider register-acs
Amane.Mailer admin hash-password
Amane.Mailer admin user create --username <name> --password-hash <pbkdf2> --tenant-id <uuid>
Amane.Mailer db backup <absolute-path>
Amane.Mailer db checkpoint
```

`password-hash` は機密です。docs・ログ・Issue へ貼らないでください。shell history やプロセス一覧に残る可能性があります。Admin の詳細は既存 Admin / local Docker runbook へリンクし、break-glass を既定経路として提示しません。

---

## トラブルシューティング

| 症状の例 | 参照 |
|----------|------|
| Easy Setup 起動 / VPS / non-interactive Admin FAIL | [Easy Setup トラブルシュート指針](#easy-setup-トラブルシュート指針) |
| tenant / token / `LIVE_SENDING_DISABLED` / provider 不足 | [設定 README troubleshooting](../../config/mailer/README.md#tenant--env-troubleshooting)、Manual の setup doctor |
| local 起動・Admin・Mailpit | [local Docker runbook](local-mailer-docker-runbook.md) |
| deploy 形の compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.md) |
| Staging / Production ACS secret 登録失敗 | [register-acs CLI](register-acs-cli-runbook.md)（確認フレーズを環境に合わせる） |
| Staging ACS 単体送信の切り分け | [test-acs-send CLI](test-acs-send-cli-runbook.md)（Staging 限定） |
| Event Grid / Queue 構成の不一致 | [event-grid config check](event-grid-config-check-runbook.md)（read-only） |
| Staging で Delivery Report が Queue に来ない | [verify-delivery-report](verify-delivery-report-runbook.md)（Staging 限定。実バウンス不要） |
| bounce / unmatched / Queue poll（runtime 説明） | [bounce ingestion](bounce-ingestion-runbook.md)、[metrics-and-alerts](metrics-and-alerts.md) |
| backup / restore | [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md) |
| 公開イメージ smoke（公開済みタグ） | [release-image-smoke](release-image-smoke.md) |
| candidate packaging / handoff | [setup-release-bundle](setup-release-bundle.md) |

## この入口の非目標

- 本ドキュメント Issue での runtime 実装変更
- marketing site
- NAS 製品別手順の網羅
- credential / password rotation ガイド
- reverse proxy / 証明書 / DNS の自動構築
- non-interactive Admin bootstrap
- Admin bootstrap の password hash file 方式
- Easy Setup 内での deployment operational verification 記録
- external secret manager 製品別ガイドの網羅
- Azure リソース自動作成
- 既存 runbook 全文のこのファイルへの複製
- 現行 v1.3.6 に含まれない Consumer bounce API / webhook 契約（#307 は将来候補。release promise ではない）
- Event Grid Push（#304）の採用手順
- production 作業で `Staging` 確認フレーズを入力させる回避策の案内
- 実在 credential / tenant / private path の掲載
- #456 Hard gate 表や候補固有 digest 値の埋め込み
