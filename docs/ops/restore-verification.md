[English](restore-verification.en.md)

# リストア検証

初回オフサイトバックアップ後、バックアップスクリプト変更後、大きな DB migration 後、オペレータが選んだ周期でリストア検証を実行します。使い捨て compose プロジェクトと使い捨てデータディレクトリを使い、本番ボリューム・ポート・リバースプロキシルーティングに影響しないようにします。

リストアドリルでは `docker compose down -v` や Docker volume prune コマンドを使わないでください。

## 使い捨て Mailer ドリル

1. 隔離した checkout またはコピーした Mailer deploy ディレクトリを準備します:

   ```bash
   set -euo pipefail
   export MAILER_CHECKOUT=/path/to/amane-mailer
   export COMPOSE_PROJECT_NAME=amane_mailer_restore_check
   export MAILER_COMPOSE_FILE="$MAILER_CHECKOUT/infra/deploy/compose.yml"
   mkdir -p ./restore-mailer-data ./restore ./keys
   chmod 700 ./keys
   RESTORE_MAILER_DATA="$(pwd)/restore-mailer-data"
   ```

   ドリル用 `.env.mailer` は使い捨て token を使い、`MAILER_DATA_PATH` を `$RESTORE_MAILER_DATA` の絶対パスに、
   `MAILER_TENANTS_HOST_PATH` を安全なドリル tenant JSON に向けます。
   ドリルが provider 接続を明示的に含めない限り `ACS_CONNECTION_STRING` は空のままにします。

2. オペレータの非公開キー管理から age identity を取得します。永続コピーはリポジトリ外に置き、ドリル用一時コピーは次に置きます:

   ```text
   ./keys/backup-age-key.txt
   ```

3. オフサイトストレージから選択した暗号化 Mailer バックアップをコピーします:

   ```bash
   set -euo pipefail
   chmod 600 ./keys/backup-age-key.txt
   MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
   MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
   rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
   ```

   別の承認済み経路で暗号化ファイルをコピーした場合は `./restore/` に置き、`rclone copy` は省略します。

4. 使い捨てデータディレクトリへ SQLite をリストアします:

   ```bash
   set -euo pipefail
   rm -f ./restore-mailer-data/mailer.db ./restore-mailer-data/mailer.db-wal ./restore-mailer-data/mailer.db-shm ./restore-mailer-data/mailer.db.restoring

   age --decrypt --identity ./keys/backup-age-key.txt "./restore/$MAILER_BACKUP_FILE" \
     > ./restore-mailer-data/mailer.db.restoring
   [ -s ./restore-mailer-data/mailer.db.restoring ] || { echo "decrypt produced empty Mailer DB" >&2; exit 1; }

   if command -v sqlite3 >/dev/null 2>&1; then
     integrity_result="$(sqlite3 ./restore-mailer-data/mailer.db.restoring 'PRAGMA integrity_check;')"
     [ "$integrity_result" = "ok" ] || { echo "SQLite integrity_check failed: $integrity_result" >&2; exit 1; }
   fi

   mv ./restore-mailer-data/mailer.db.restoring ./restore-mailer-data/mailer.db

   chmod 600 ./restore-mailer-data/mailer.db
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" --profile ops run --rm mailer-migrate
   ```

5. 使い捨て Mailer サービスを起動します:

   ```bash
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" up -d mailer
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer /app/Amane.Mailer healthcheck
   MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env.mailer | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
   MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" exec -T mailer /app/Amane.Mailer db stats
   ```

6. ドリル `.env` で Admin UI が有効なら、ドリル専用資格情報でログイン、送信依頼一覧表示、Dead Letters ページ表示を確認します。

7. ドリル日付、バックアップファイル名、リストア所要時間、検証結果、是正措置を非公開運用メモに記録します。

8. ドリルコンテナを停止・削除します。クリーンアップ承認まで `restore-mailer-data/` は検査用に保持し、その後削除します:

   ```bash
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" stop mailer
   docker compose --env-file .env.mailer -f "$MAILER_COMPOSE_FILE" rm -f mailer
   rm -f ./keys/backup-age-key.txt ./restore/"$MAILER_BACKUP_FILE"
   rm -rf ./restore-mailer-data
   unset COMPOSE_PROJECT_NAME RESTORE_MAILER_DATA
   ```

## 受け入れチェック

- 暗号化バックアップが保管済み age identity で復号できる。
- 復元ファイルが使い捨てデータディレクトリに `mailer.db` として存在する。
- `mailer-migrate` が成功する。
- `/app/Amane.Mailer healthcheck` が 0 で終了する。
- 使い捨て Mailer 環境で `/healthz` と `/readyz` が 200 を返す。
- `db stats` が成功し、復元データの期待 status 件数を示す。
- ドリルで Admin UI が有効なら、Admin ログイン、Mail Requests、Dead Letters が動作する。
- 次のスケジュールバックアップに依存する前にドリル結果を記録する。
