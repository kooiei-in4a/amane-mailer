[English](backup-operations.en.md)

# バックアップ運用

セルフホスト Amane Mailer インスタンスのバックアップ運用 runbook です。
Mailer が所有するデータと移植可能な example に限定しています。ホストへのパッケージ導入、実 rclone remote、資格情報、age identity、cron 所有者、プロバイダ固有のバケットポリシーは、オペレータの非公開インフラメモに属します。

## スコープ境界

Amane Mailer が文書化するもの:

- バックアップ対象の Mailer ファイル
- Mailer CLI によるオンライン SQLite バックアップの作成方法
- `backup-mailer.sh` による暗号化と任意のアップロード
- バックアップが復元可能であることの検証方法
- オペレータが適用できる rclone とスケジューラの example 形

Amane Mailer が所有しないもの:

- 特定 deploy host やベースイメージへの rclone 導入
- 実 rclone remote 名、エンドポイント、アクセスキー、バケット名
- 実 age identity やキー保管場所
- 特定組織の本番保持ポリシー
- ホストレベルの cron や systemd timer の所有者

ホスト固有の判断はリポジトリ外に置いてください。issue でホスト固有作業を追跡する場合は本 runbook へリンクし、secret やプロバイダ詳細を issue に貼らないでください。

## バックアップ対象

Mailer が所有する次の項目をバックアップします:

| 項目 | 既定の場所 | 備考 |
| --- | --- | --- |
| SQLite データベース | `/app/data/mailer.db` に mount される `./data/mailer.db` | `backup-mailer.sh` の対象。`Amane.Mailer db backup` を使い、稼働中の WAL DB ファイルを直接コピーしない。管理操作監査ログ（`admin_audit_events`）も同一 DB に含まれ、バックアップ・リストアで一緒に保全される |
| tenant 設定 | `./tenants.json` | オペレータによる手動バックアップ。ルーティングと token env 名を含む。運用 metadata を含む場合があり、復元前に確認する |
| compose env | `./.env` | オペレータによる手動バックアップ。secret または secret 参照を含む。Git ではなく非公開 secret manager やホストバックアップにのみ保存 |
| deploy テンプレート | `compose.yml` と `.env` の image tag | ホストローカル状態の手動バックアップ。チェックイン済みテンプレートは再利用可能。有効 image tag はホスト状態 |
| 暗号化バックアップ成果物 | `./data/backups/mailer-*.db.age` | 暗号化とアクセスポリシー確認後のみアップロード安全 |

`ACS_CONNECTION_STRING`、tenant bearer token、管理画面パスワード hash、rclone 資格情報、age identity、実 backup remote 詳細をリポジトリ、公開ログ、PR 説明、GitHub issue に保存しないでください。

## 安全原則

- Mailer DB バックアップは、稼働中サービスコンテナ内から SQLite オンラインバックアップ API を使う `./Amane.Mailer db backup` で取得する。
- 平文 `.db` バックアップはオフサイト転送前に必ず暗号化する。
- 暗号化後は平文 `.db` バックアップファイルを直ちに削除する。
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
  .env
  tenants.json
  backup-mailer.sh
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
7. 手動バックアップを実行する。
8. `data/backups/` に平文 `.db` が残っていないことを確認する。
9. 暗号化 `.db.age` がローカルとオフサイト先の両方に存在することを確認する。
10. スケジュール運用前にリストア検証を実行する。

オフサイト先、資格情報、rclone 設定が整うまで、実ホストを `MAILER_BACKUP_REQUIRE_OFFSITE=true` に切り替えないでください。失敗モードは fail-secure ですが、設定完了までスケジュールバックアップは失敗します。

## 手動バックアップ

`infra/deploy/backup-mailer.sh` を Mailer compose ディレクトリへコピーし、そのディレクトリから実行します（`MAILER_COMPOSE_DIR` を設定するか、ディレクトリで直接実行）。

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
- `data/backups/` の想定外平文 `.db` ファイル

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
