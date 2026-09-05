[English](upgrade-guide.en.md)

# Upgrade / rollback ガイド

既存の self-hosted Amane Mailer を、現在公開中の release へ更新するための
canonical な運用入口です。初回構築は [セットアップ入口](setup-guide.md) を使ってください。
本ガイドは release identity、互換性、backup、migration、rollout、検証、rollback の順序を
まとめ、詳細操作は各正本 runbook へ委譲します。

この手順は `infra/deploy/compose.yml` を使う Manual / Hardened deployment を基準にします。
Easy Setup の Managed bundle 切替は [ADR 0021](../adr/0021-easy-setup-boundaries.md) の
`ACTIVE` / integrity 契約に従いますが、config bundle rollback は SQLite、Admin 状態、
mail data、provider 副作用を戻しません。image または schema が変わる操作を setup の再実行や
Admin の再 bootstrap で代替しないでください。

## Authority と停止条件

| 判断 | Authority |
|------|-----------|
| 現在公開中の version / tag / platform / release record path | [`release/current-public.json`](../../release/current-public.json) |
| 対象 release の commit、image digest、immutable tag、platform、公開証跡 | `current-public.json` の `releaseRecord` が指す release record |
| release 間の互換性・破壊的変更・migration inventory | 対象区間の [`CHANGELOG.md`](../../CHANGELOG.md) と各 [`docs/releases/`](../releases/) record |
| 公開 artifact の照合方法 | [Release artifact verification](release-artifact-verification.md) |
| migration の挙動 | [service-spec の migration checksum policy](../service-spec.md#マイグレーション-checksum-policy) と対象 image 内の `Data/Migrations/*.sql` |
| backup / restore | [バックアップ運用](backup-operations.md)、[リストア手順](restore-procedure.md)、[リストア検証](restore-verification.md) |
| liveness / readiness | [service-spec](../service-spec.md) と対象 release の runtime |

`docs/releases/` の個別 file、README、setup guide に書かれた version を current release の
別 authority にしません。必ず `release/current-public.json` から始めてください。repository root
では、固定 version を手順へ転記せず次のように確認できます。

```bash
node - <<'NODE'
const current = require('./release/current-public.json');
console.log(`version=${current.version}`);
console.log(`tag=${current.tag}`);
console.log(`platforms=${current.platforms.join(',')}`);
console.log(`releaseRecord=${current.releaseRecord}`);
NODE
```

次のいずれかなら変更を開始せず停止します。

- 稼働中 image の version / immutable identity、対象 release、または対象 platform を確定できない
- 現在の配備から対象までの release record / CHANGELOG に必要な互換性・migration 情報がない
- release record と registry で commit、tag、digest、platform が一致しない
- pre-upgrade backup と、選択した backup を復号できる age identity を確保できない
- rollback 時に使う以前の immutable image、private config / secret、DB restore 経路を確保できない

1.x の SemVer 互換性は public HTTP contract と Contracts package に適用されます。DB schema の
downgrade や任意の release を飛び越す upgrade を保証するものではありません。

## 1. 対象と互換性を確定する

1. 稼働中の immutable image reference、version、compose / config identity、DB data path を
   private な変更記録へ残します。secret 値や private path を公開 issue / log へ貼りません。
2. `release/current-public.json` を読み、そこから示された release record を開きます。
3. 現在の配備より後から対象まで、該当する CHANGELOG section と release record を順に読み、
   public contract、runtime semantics、設定、platform、migration inventory の差分を確認します。
4. release record の commit / digest / immutable `sha-<git-sha>` tag を
   [artifact verification runbook](release-artifact-verification.md) で照合します。deploy では
   mutable な version tag や `latest` より、照合済み digest または immutable SHA tag を使います。
5. migration が含まれる区間では、release record / CHANGELOG に記載された開始 schema と適用順が
   現在の DB に適用できることを確認します。直接の upgrade path が明記されていない場合は推測しません。

`infra/deploy/compose.yml` は `MAILER_IMAGE_REPOSITORY` と `MAILER_IMAGE_TAG` を組み合わせるため、
この template では release record にある immutable `sha-<releaseCommitSha>` tag を
`MAILER_IMAGE_TAG` に設定できます。別の deployment tooling が digest reference を受け取れる場合は、
release record と照合した digest を pin してください。

## 2. Backup と rollback plan を準備する

[バックアップ運用](backup-operations.md) に従い、upgrade 用の最終 backup は呼び出し元を
quiesce した後、旧 Mailer を停止する直前に取得します。`backup-mailer.sh` は稼働中 Mailer の
SQLite online backup API を使います。live WAL DB file を直接 copy しません。managed-v2 の
full recovery point が必要な場合は、graceful shutdown 後に `backup-instance-state.sh` を実行し、
DB、canonical provider secret、committed spool を同じ cold point から保全します。full script は
stop/start を行いません。

事前確認:

- 対応する age identity と復旧コピーが利用できる
- `tenants.json`、`.env`、compose template、file secrets、Managed root など、DB 外の
  operator-owned state も private storage に保全されている。managed-v2 full archive の
  対象外となる Caddy named volume と外部 bounce secret の扱いも決まっている
- 最終 backup の取得後、target migration 前に [リストア検証](restore-verification.md) を実施できる
  隔離環境と時間を確保している
- 以前の immutable image reference と、その release に互換する config を取得できる

rollback の承認者、呼び出し元を停止する方法、判断期限も先に決めます。実環境の DB restore は
破壊的操作なので、[リストア手順](restore-procedure.md) が要求する明示承認なしに実行しません。

backup snapshot の時刻が rollback の recovery point です。snapshot 後の DB 更新は、通常 traffic
をまだ再開していなくても restore 先に含まれません。呼び出し元の quiesce は新規受付を減らしますが、
既にある queue / in-flight work、Worker / Sweep、webhook / Admin / retention の DB 更新や provider
呼び出しを停止するものではありません。snapshot 後から Mailer 停止完了までに起きた provider
side effect は DB restore では戻りません。restore を承認する前に、この期間の request state と
provider outcome を照合し、喪失する DB 更新と残存する外部 side effect を明示的に受け入れる必要が
あります。provider 呼び出し結果が曖昧な request を安全に再送できるとは仮定しません。

## 3. Rollout

以下は deploy host の Mailer compose directory で実行します。実際の path、image identity、
secret は private な値を使います。

1. 呼び出し元からの新規 request を止めます。環境の運用基準に従って queue / in-flight state を
   確認し、どの状態を recovery point にするか記録します。
2. 旧 Mailer を稼働させたまま最終 online DB backup を取得します。新しい暗号化
   `mailer-*.db.age`、必要な offsite upload、平文 `.db` が残っていないことを確認し、snapshot
   時刻を記録します。
3. 直ちに旧 Mailer の graceful shutdown を完了させます。managed-v2 の full recovery point
   を作る場合は、`backup-instance-state.sh` をここで実行し、`mailer-state-*.tar.age` と
   平文 `.tar` の不存在を確認します。snapshot 後に完了した DB 更新や provider operation が
   あれば reconciliation 対象として記録します。

```bash
docker compose --env-file .env -f compose.yml stop mailer
```

4. 保存済みの旧 private config / image identity を保持したまま、`.env` の
   `MAILER_IMAGE_TAG` を検証済みの対象 immutable SHA tag に変更し、target image を pull します。
5. 最終 DB/full backup を対象 image と隔離した disposable environment で
   [リストア検証](restore-verification.md) します。そこでは通常 `mailer-migrate` と health /
   readiness まで確認します。失敗した場合は production DB を変更せず停止し、保存済みの旧
   image / config を明示的に復元して再起動するか、別の検証済み backup を選びます。
6. 対象 image で schema を read-only 分類します。

```bash
docker compose --env-file .env -f compose.yml --profile ops pull mailer-migrate mailer
docker compose --env-file .env -f compose.yml --profile ops run --rm \
  mailer-migrate db migrate --status --format json
```

既存配備では通常、`Current` または `Behind` だけを続行候補にします。`AheadOrUnsupported` または
想定外の `DatabaseAbsent` は停止です。`Unknown` も通常は path / mount / image / schema、SQLite
open / I/O の問題を解消するまで停止します。ただし次の既知 legacy bootstrap だけは例外です。

### Legacy checksum bootstrap（限定例外）

[service-spec](../service-spec.md#マイグレーション-checksum-policy) は、checksum column 導入前の
既存 `schema_migrations` table に対し、最初の checksum 対応 `db migrate` が column を追加し、
同梱中の適用済み migration version へ checksum を backfill する経路を定義しています。read-only
`--status` はこの DB を `Unknown` と分類し得ます。

`Unknown` で通常 migration を実行してよいのは、次をすべて満たす場合だけです。

- 最終 backup の disposable copy を target migration 前に read-only で確認し、稼働元 release と
  DB が既存 `schema_migrations` table を持つ一方で checksum column を持たない documented legacy
  schema であることを operator が確認した
- applied version が対象 image の期待する連続 prefix であり、DB path / mount、SQLite open / I/O、
  権限、未知の applied version や migration gap など、legacy checksum 不在以外の `Unknown` 原因を
  除外した
- 最終 pre-upgrade backup を対象 image で restore verification し、通常 migration を含め成功した。
  historical checksum が存在しないため、対象 image 同梱 SQL を最初の trust anchor にする判断も
  明示承認した

証明できなければ停止します。checksum column や値を手作業で追加・backfill せず、承認した対象
image の通常 `db migrate` にだけ bootstrap を実行させます。

7. `Behind` または承認済み legacy checksum bootstrap なら、対象 image の migration runner を
   **1 つだけ**実行します。`Current` でも同じ command は「up to date」で完了します。複数
   runner を同時に起動しません。
8. migration 後に read-only status を再実行し、`Current` を要求します。
9. migration と status 確認が成功した場合だけ対象 Mailer を起動します。

```bash
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml --profile ops run --rm \
  mailer-migrate db migrate --status --format json
docker compose --env-file .env -f compose.yml up -d --wait mailer
```

SQL migration は番号順の forward-only bundle で、適用済み version と byte-level checksum を
検証します。既存 migration file の欠落や checksum mismatch を手編集、reformat、DB metadata
変更で回避しないでください。正しい image / SQL file、または backup restore を選びます。

## 4. Health / readiness と運用検証

通常 traffic を戻す前に、少なくとも次を確認します。

```bash
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer healthcheck
MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
docker compose --env-file .env -f compose.yml exec -T mailer \
  curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
docker compose --env-file .env -f compose.yml exec -T mailer \
  curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer db stats
```

- `/healthz` は process の liveness だけを示します。
- CLI `healthcheck` と `/readyz` は、対象 binary が要求する migration version / checksum と、
  Worker 有効時の Worker / Sweep 稼働・heartbeat 鮮度を確認します。
- `/readyz` は provider / ACS 設定を再検証せず、provider 到達性、実配送、upgrade 全体の成功を
  単独では証明しません。provider 設定の不正は startup 時に fail-closed になる範囲があります。

release record / CHANGELOG が要求する config・contract 確認と、環境に合う no-send または承認済み
delivery check も実施します。Admin を利用する環境では、承認済み access path から login、
Mail Requests、Dead Letters を確認します。すべて成功してから呼び出し元を段階的に再開し、
readiness、queue / failure metrics、provider 結果を監視します。

## 5. Rollback 判断と実行境界

| 状態 | 処置 |
|------|------|
| migration 前に artifact / config / pull / status 確認が失敗 | 変更を中止。target `.env` へ変更済みなら、保存済みの旧 private config と immutable image identity を明示的に復元し、旧 image を起動して health / readiness を再確認する |
| migration 未適用で新 runtime の起動・検証が失敗 | 呼び出し元を止めたまま以前の immutable image / config へ戻し、health / readiness を再確認する |
| migration 適用後に起動・readiness・運用検証が失敗 | 古い binary を forward-migrated DB に当てない。snapshot 後の DB 更新と provider side effect を照合し、loss window を承認したうえで、以前の image / config と **pre-upgrade DB backup** を組にして [リストア手順](restore-procedure.md) を実行する |
| traffic 再開後に rollback が必要 | まず呼び出し元を止める。snapshot 以降に受理・処理した request / provider outcome を照合する。loss window は traffic 再開時ではなく backup snapshot 時に始まるため、その全期間を明示承認してから incident 手順を決める |

schema downgrade は保証されず、reverse migration は提供されません。restore による DB loss
window は backup snapshot 時点から始まり、provider side effect は restore では取り消されません。
承認者へ reconciliation 結果と範囲を提示したうえで、先に `.env` と compose state を以前の互換
image / config に戻し、選択した pre-upgrade backup を
[restore procedure](restore-procedure.md) に従って復元します。呼び出し元を無効のまま CLI
`healthcheck`、`/healthz`、`/readyz`、`db stats` と環境固有の確認を通してから再開します。

config だけを戻しても SQLite、Admin state、mail data、既に発生した provider side effect は
戻りません。`docker compose down -v`、data directory の削除、migration metadata の手編集を
rollback として使わないでください。

## 変更記録

公開できない値を除き、operator の private change / incident record に次を残します。

- 以前と対象の immutable image identity
- 対象 release record と確認した artifact digest / platform
- backup artifact、restore verification、age identity availability の確認結果
- backup snapshot 時刻、停止完了までの post-snapshot change / provider outcome、
  reconciliation と承認
- migration status と実行結果
- health / readiness / environment-specific verification の結果
- traffic stop / resume、rollback 判断、承認、未解決事項
