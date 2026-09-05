[English](backup-operations.en.md)

# バックアップ運用

セルフホスト Amane Mailer インスタンスのバックアップ運用 runbook です。
Mailer が所有するデータと移植可能な example に限定しています。ホストへのパッケージ導入、実 rclone remote、資格情報、age identity、cron 所有者、プロバイダ固有のバケットポリシーは、オペレータの非公開インフラメモに属します。

バックアップ経路は二つあります。既存の `backup-mailer.sh` は稼働中 SQLite の
DB 単体スナップショットです。障害復旧用の v2 managed instance 全体には、Mailer
を停止した状態で `backup-instance-state.sh` を使います。DB 単体成果物を
インスタンス全体のバックアップとして扱ってはいけません。

## スコープ境界

Amane Mailer が文書化するもの:

- バックアップ対象の Mailer ファイル
- Mailer CLI によるオンライン SQLite バックアップの作成方法
- `backup-mailer.sh` による暗号化と任意のアップロード
- `backup-instance-state.sh` による停止確認付きの暗号化インスタンス状態バックアップ
- `restore-instance-state.sh` による空ディレクトリ限定リストア
- バックアップが復元可能であることの検証方法
- オペレータが適用できる rclone とスケジューラの example 形

Amane Mailer が所有しないもの:

- 特定 deploy host やベースイメージへの rclone 導入
- 実 rclone remote 名、エンドポイント、アクセスキー、バケット名
- 実 age identity やキー保管場所
- 特定組織の本番保持ポリシー
- ホストレベルの cron や systemd timer の所有者
- Caddy の証明書・設定・データ volume のバックアップ所有者

ホスト固有の判断はリポジトリ外に置いてください。issue でホスト固有作業を追跡する場合は本 runbook へリンクし、secret やプロバイダ詳細を issue に貼らないでください。

## バックアップ対象

Mailer が所有する次の項目をバックアップします:

| 項目 | 既定の場所 | 備考 |
| --- | --- | --- |
| SQLite データベース | `/app/data/mailer.db` に mount される `./data/mailer.db` | DB 単体経路では `backup-mailer.sh` の対象。`Amane.Mailer db backup` を使い、稼働中の WAL DB ファイルを直接コピーしない。管理操作監査ログ（`admin_audit_events`）も同一 DB に含まれ、バックアップ・リストアで一緒に保全される |
| managed provider secret | `MAILER_DATA_PATH/secrets/acs/acs_connection_string`（コンテナ内 `/app/data/secrets/acs/acs_connection_string`） | initialized v2 の DB が参照する保護済みファイル。full instance backup では DB と同じ archive に含める。`MAILER_ACS_SECRET_HOST_PATH` の `/run/secrets/acs` mount は read-only の互換／手動登録経路であり、二つ目の authority ではない |
| committed attachment spool | `MAILER_DATA_PATH/attachment-spool/committed`（コンテナ内 `/app/data/attachment-spool/committed`） | accepted request の未完了送信に必要な durable spool。full instance backup で含める。request-id と spool-key の opaque なパスだけを扱う |
| transient attachment staging | `MAILER_DATA_PATH/attachment-spool/staging` | full archive から除外。起動時に orphan staging が cleanup されるため、復元対象の durable state ではない |
| bootstrap token / logs / backup staging | `MAILER_DATA_PATH/bootstrap`、`logs`、`backups` | full archive から除外。bootstrap token は initialized state の authority ではなく、logs と既存 backup 成果物は復元入力にしない |
| tenant 設定 | `./tenants.json` | オペレータによる手動バックアップ。ルーティングと token env 名を含む。運用 metadata を含む場合があり、復元前に確認する |
| compose env | `./.env` | オペレータによる手動バックアップ。secret または secret 参照を含む。Git ではなく非公開 secret manager やホストバックアップにのみ保存 |
| deploy テンプレート | `compose.yml` と `.env` の image tag | ホストローカル状態の手動バックアップ。チェックイン済みテンプレートは再利用可能。有効 image tag はホスト状態 |
| DB 単体の暗号化成果物 | `./data/backups/mailer-*.db.age` | `backup-mailer.sh` が作成。full instance archive ではない |
| full instance の暗号化成果物 | `./data/backups/mailer-state-*.tar.age` | `backup-instance-state.sh` が作成。平文 tar は data volume に置かず、age 後に削除する |
| Caddy state | Compose named volume `caddy_data`（`/data`）と `caddy_config`（`/config`） | Mailer archive に混ぜない。証明書・Caddy 設定を保持するか、復旧時に再発行するかを edge 運用者が別途決める |

`ACS_CONNECTION_STRING`、tenant bearer token、管理画面パスワード hash、rclone 資格情報、age identity、実 backup remote 詳細をリポジトリ、公開ログ、PR 説明、GitHub issue に保存しないでください。

## Full instance state の境界

`backup-instance-state.sh` は generic backup framework ではありません。v2 managed
instance の固定された最小復元単位だけを、次の archive entry として扱います:

- `mailer.db`
- `secrets/acs/acs_connection_string`
- `attachment-spool/committed/` と、その下の Mailer が生成した opaque spool files

実際の入力は、停止したサービス・migration container が共有する
`MAILER_DATA_PATH` です。DB の `provider_secret_ref` がこの data root 外を指す古い／手動構成は、
そのまま full backup しません。まず secret の authority と mount を運用メモで reconcile
し、initialized DB が参照する secret を保護済み data-root 配下にそろえてから取得します。
script は data-root 配下の canonical ACS secret、owner-only permission、committed spool
の形を preflight します。

full backup の cold 条件は次のとおりです。script 自体はサービスを停止・起動しません。
operator が先に `mailer`、`mailer-migrate`、`mailer-acs-admin` を停止し、script が
Compose の running service 一覧を再確認します。SQLite の `-wal`、`-shm`、`-journal`
sidecar が残っている場合も失敗させます。これにより、DB と secret と committed spool
が同じ停止点の状態になります。

`attachment-spool/staging`、bootstrap token、logs、`data/backups`、tenant JSON、
`.env`、`platform-sender.json`、bounce queue の外部 secret はこの archive に混ぜません。
bounce queue を有効にした構成は、その外部 secret を別の operator-owned secret backup
として扱い、復元前に同じ参照を用意します。Caddy の `caddy_data` と `caddy_config` も
Mailer state とは別の backup unit です。

## 安全原則

- Mailer DB バックアップは、稼働中サービスコンテナ内から SQLite オンラインバックアップ API を使う `./Amane.Mailer db backup` で取得する。
- full instance backup は停止確認後にだけ取得し、DB・canonical provider secret・committed spool を同じ cold point から固定する。
- full instance backup は明示した state path だけを tar に入れる。generic な全 volume 探索や `data/` 全体の再帰コピーは行わない。
- 平文 `.db` バックアップはオフサイト転送前に必ず暗号化する。
- full instance の平文 `.tar` も、data volume や backup remote に残さず age の一時入力としてだけ扱う。
- 暗号化後は平文 `.db` と `.tar` バックアップファイルを直ちに削除する。
- age identity（private key）は archive、リポジトリ、ログに入れない。公開鍵だけを `MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY` で指定する。
- インシデント中にオペレータが意図的にローカル暗号化バックアップを受け入れない限り、実運用では `MAILER_BACKUP_REQUIRE_OFFSITE=true` を維持する。
- `./data/backups/` はステージング用であり、永続バックアップ保管先ではない。
- 初回オフサイトバックアップ後、バックアップスクリプト変更後、大きな migration 後、オペレータが選んだ周期でリストア検証を実行する。
- オフサイト障害中に一時的に `MAILER_BACKUP_REQUIRE_OFFSITE=false` にした場合は、理由・時刻・オペレータ・フォローアップを非公開運用メモに記録し、オフサイト先が正常化したら fail-secure 設定へ戻す。

## age 鍵管理

承認済みオペレータ端末または対象ホストで age identity を生成します:

```bash
mkdir -p ./keys
chmod 700 ./keys
age-keygen -o ./keys/backup-age-key.txt
chmod 600 ./keys/backup-age-key.txt
age-keygen -y ./keys/backup-age-key.txt
```

ホスト `.env` の `MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY` に `age-keygen -y` の recipient を設定します。identity ファイルはオペレータのパスワードマネージャまたはキー vault に保管し、リポジトリ外かつバックアップバケット外に少なくとも 1 つの別復旧コピーを保持します。

鍵ローテーション時は新 identity を生成し、`MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY` を更新し、新しいオフサイトバックアップを取得し、新 identity でリストア検証を完了します。当該 identity で暗号化されたバックアップがすべて失効または意図的に破棄されるまで旧 identity を保持します。

## rclone の example

`backup-mailer.sh` は rclone で暗号化 `.db.age` をアップロードできますが、本リポジトリは統合ポイントのみ提供します。rclone をシステム全体、deploy ユーザー配下、別ホスト管理レイヤーで提供するかはオペレータが決めます。

ホスト状態の example:

```text
/path/to/mailer/
  compose.yml
  compose.vps-dogfood.yml       # VPS managed-v2 の場合
  .env
  tenants.json
  backup-mailer.sh
  backup-instance-state.sh
  data/
  rclone/
    rclone.conf        # 非公開。コミットしない
```

`.env` の example 値:

```dotenv
MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY=replace-with-age-recipient-public-key
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
MAILER_BACKUP_RCLONE_CONFIG_PATH=./rclone/rclone.conf
MAILER_BACKUP_REQUIRE_OFFSITE=true
MAILER_BACKUP_PING_URL=
# VPS overlay を使う場合は .env に置くか、実行時に指定する
MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml
```

`MAILER_BACKUP_RCLONE_REMOTE` と `rclone.conf` の内容は非公開インフラ状態の example です。公開ドキュメントや issue ではプレースホルダー名を使います。secret 値を Git 外に置けるなら rclone の環境変数設定も可です。

推奨オブジェクトストレージ制御:

- Mailer バックアップ専用の private バケットまたは prefix
- 公開アクセス無効
- 利用可能ならプロバイダ側暗号化を有効化
- `rclone copy` に必要な最小権限のアップロード資格情報
- ストレージプロバイダによるライフサイクル失効
- 別の復元/read 資格情報または break-glass オペレータアクセス

日次アップロード資格情報に広い削除権限を与える代わりに、バケットライフサイクルでオフサイト保持を管理します。

## プロビジョニング順序

セルフホストでは次の順序を使います:

1. 非公開オフサイト先とライフサイクルポリシーを作成または承認する。
2. `rclone copy` に必要な最小アップロード資格情報を作成する。
3. ホスト上で rclone をどう導入・管理するか決める。
4. 非公開 rclone 設定をホストに置くか、承認済み `RCLONE_CONFIG_*` 環境変数を Git 外に設定する。
5. ホスト `.env` に `MAILER_BACKUP_*` 値を設定する。
6. `docker compose --env-file .env -f compose.yml config --quiet` を実行する。
7. DB 単体が必要な場合はオンライン `backup-mailer.sh` を実行する。
8. 障害復旧用には Mailer を停止し、`backup-instance-state.sh` を実行する。
9. `data/backups/` に平文 `.db` または `.tar` が残っていないことを確認する。
10. 暗号化 `.age` がローカルとオフサイト先の両方に存在することを確認する。
11. スケジュール運用前に full instance のリストア検証を実行する。

オフサイト先、資格情報、rclone 設定が整うまで、実ホストを `MAILER_BACKUP_REQUIRE_OFFSITE=true` に切り替えないでください。失敗モードは fail-secure ですが、設定完了までスケジュールバックアップは失敗します。

## 手動バックアップ

`infra/deploy/backup-mailer.sh` を Mailer compose ディレクトリへコピーし、そのディレクトリから実行します（`MAILER_COMPOSE_DIR` を設定するか、ディレクトリで直接実行）。これは DB 単体のオンライン経路です。

```bash
cd /path/to/mailer
docker compose --env-file .env -f compose.yml config --quiet
bash backup-mailer.sh 2>&1 | tee /tmp/mailer-backup-manual.log
```

期待結果:

- `data/backups/` に `mailer-YYYYMMDDTHHmmssZ.db.age` が書き込まれる
- スクリプト終了後、平文 `mailer-YYYYMMDDTHHmmssZ.db` が残らない
- SQLite バックアップ API によるオンラインバックアップである
- `MAILER_BACKUP_RCLONE_REMOTE` 設定時は `rclone copy` で暗号化ファイルをアップロードする
- `MAILER_BACKUP_REQUIRE_OFFSITE=true` で remote 欠落またはアップロード失敗時は非ゼロ終了する
- ログに secret が出ない

アクティブなバックアップ操作外で平文 `.db` が見つかった場合はホストから削除し、インシデントをオペレータの非公開メモに記録します。

## Full instance backup（PR3）

full instance は、停止点をそろえた coordinated/cold backup です。script は自動で
stop/start しないため、運用者が停止を確認できる maintenance window で実行します。

```bash
cd /path/to/mailer
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer mailer-migrate mailer-acs-admin 2>/dev/null || true

MAILER_COMPOSE_DIR="$PWD" \
MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml \
  bash /path/to/amane-mailer/infra/deploy/backup-instance-state.sh
```

`mailer`、`mailer-migrate`、`mailer-acs-admin` のいずれかが running と見える場合、
または DB sidecar が残っている場合は non-zero で終了します。停止コマンドで存在しない
one-shot service を指定する差異がある環境では、停止後に `docker compose ... ps` で
Mailer runtime が止まっていることを確認してから script を実行してください。停止確認を
省略する環境変数や `--force` はありません。

期待される成果物は `data/backups/mailer-state-YYYYMMDDTHHmmssZ.tar.age` です。archive
には `mailer.db`、`secrets/acs/acs_connection_string`、`attachment-spool/committed`
だけが入り、staging、bootstrap token、logs、既存 backup、age identity は入りません。
ACS secret はログや shell 出力に表示せず、archive の作成後には平文 tar を削除します。
暗号化と offsite upload の失敗時はローカルの未完成成果物も cleanup し、
`MAILER_BACKUP_REQUIRE_OFFSITE=true` では remote 未設定・upload 失敗を成功扱いにしません。

DB 単体の `mailer-*.db.age` と full instance の `mailer-state-*.tar.age` を同じものとして
扱わないでください。full instance をスケジュールする場合も、停止と起動を所有する
外部 maintenance orchestration は本リポジトリの script の外側に置き、無停止の cron
から full script を呼ばないでください。

## Admin UI 経由 backup（任意）

`AMANE_ADMIN_DB_OPS_ENABLED=true`（fallback: `MAILER_ADMIN_DB_OPS_ENABLED`）を **明示した場合のみ**、Admin UI `/admin/ops` から WAL checkpoint と online backup を実行できます。`AMANE_ADMIN_ENABLED=true` だけでは有効になりません。

| 項目 | 内容 |
|------|------|
| 認可 | break-glass 管理者、または全 effective tenant scope を持つ管理者のみ（scoped admin は不可） |
| 出力先 | 固定ディレクトリのみ（UI/API からパス指定不可）。既定は `<db-parent>/backups/`。上書きは `AMANE_ADMIN_DB_BACKUP_DIRECTORY` |
| ファイル名 | `mailer-<UTC-timestamp>.db`（平文） |
| 監査 | `admin_audit_events` に `db_ops.*` を記録。絶対パスは記録しない |
| 同時実行 | checkpoint / backup は排他（実行中は 409 Conflict） |

**運用上の注意**

- backup 出力は Mailer DB と同等以上の **PII を平文で含む**。本番の定期運用は `backup-mailer.sh`（age 暗号化 + offsite）を優先し、Admin backup は緊急スナップショット向けとする。
- 保存先の権限制限・暗号化・転送・削除確認を適用する。詳細は [ADR 0013 D-09](../adr/0013-admin-threat-model-and-pii-policy.md) を参照。
- CLI `db checkpoint` / `db backup` は従来どおり利用可能（Admin ゲートの影響を受けない）。
## スケジュールバックアップ

手動バックアップとリストア検証が通ってからスケジュールを導入します。crontab や systemd timer など、ホストが所有する 1 か所に置きます。

cron の example:

```cron
30 18 * * * cd /path/to/mailer && bash backup-mailer.sh 2>&1 | logger -t amane-mailer-backup
```

systemd timer の example 形:

```ini
# /etc/systemd/system/amane-mailer-backup.service
[Unit]
Description=Amane Mailer encrypted backup

[Service]
Type=oneshot
WorkingDirectory=/path/to/mailer
ExecStart=/usr/bin/bash backup-mailer.sh
```

```ini
# /etc/systemd/system/amane-mailer-backup.timer
[Unit]
Description=Run Amane Mailer encrypted backup

[Timer]
OnCalendar=*-*-* 18:30:00
Persistent=true

[Install]
WantedBy=timers.target
```

unit パス、ユーザー、rclone バイナリパス、ログ先、タイムゾーンはホスト固有の判断です。

## 監視の引き継ぎ

最低限、オペレータは次を監視すべきです:

- バックアップコマンドの終了ステータス
- `MAILER_BACKUP_REQUIRE_OFFSITE=true` 時のオフサイト設定欠落
- 最近の成功バックアップ成果物の欠如
- `MAILER_BACKUP_PING_URL` 設定時の `/fail` または成功 ping 欠如
- `data/backups/` の想定外平文 `.db` / `.tar` ファイル

ping URL、アラートルーティング、ログ先は本リポジトリ外です。

## リストア検証

初回オフサイトバックアップ後、使い捨て環境で
[restore-verification.md](restore-verification.md) を実行し、結果を非公開運用メモに記録します:

- 日付とオペレータ
- 対象環境
- バックアップファイル名
- リストア所要時間
- 検証チェック
- 是正措置（あれば）
