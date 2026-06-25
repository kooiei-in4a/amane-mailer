[English](restore-procedure.en.md)

# リストア手順

暗号化バックアップからセルフホスト Amane Mailer の SQLite データベースを復元する runbook です。Mailer を呼び出す可能性のあるアプリケーション DB は対象外で、利用側アプリの運用リポジトリに属します。

実環境のリストアは破壊的操作であり、Mailer を停止したりデータを置き換える前にオペレータの明示的な承認が必要です。

## 前提条件

- 選択したバックアップでリストア検証がすでに成功していること。
- 対応する age identity をオペレータの非公開キー管理から取得済みであること。age 鍵管理は [backup-operations.md](backup-operations.md) を参照。
- 対象 Mailer compose ディレクトリに `compose.yml`、`.env`、`tenants.json` があること。
- 選択した暗号化バックアップ名が Mailer 形式 `mailer-YYYYMMDDTHHmmssZ.db.age` に一致すること。
- 対象 `.env` の image tag と tenant 設定が、復元後サービスに意図した値であること。

## age identity の扱い

age identity はバックアップ復号の唯一の手段です。紛失すると暗号化バックアップは永久に復元不能です。

リストア中は identity を `./keys/backup-age-key.txt` など Git 無視の一時パスへコピーし、権限を制限し、インシデントまたはドリル完了後に一時コピーを削除します。identity をリポジトリやバックアップバケットに置かないでください。

## Mailer のリストア

承認後、Mailer compose ディレクトリから次を実行します。パスとファイル名はオペレータの非公開値に置き換えてください。

まずリストア作業領域を準備します:

```bash
set -euo pipefail
cd /path/to/mailer
docker compose --env-file .env -f compose.yml config --quiet

mkdir -p ./data ./restore ./restore/previous ./keys
chmod 700 ./keys
```

非公開キー管理から `./keys/backup-age-key.txt` をコピーし、続けます:

```bash
set -euo pipefail
chmod 600 ./keys/backup-age-key.txt
MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"

docker compose --env-file .env -f compose.yml stop mailer
cp -a data/mailer.db "restore/previous/mailer.db.before-restore-$(date -u +%Y%m%dT%H%M%SZ)" 2>/dev/null || true
rm -f data/mailer.db data/mailer.db-wal data/mailer.db-shm data/mailer.db.restoring

age --decrypt --identity ./keys/backup-age-key.txt "./restore/$MAILER_BACKUP_FILE" \
  > data/mailer.db.restoring
[ -s data/mailer.db.restoring ] || { echo "decrypt produced empty Mailer DB" >&2; exit 1; }

if command -v sqlite3 >/dev/null 2>&1; then
  integrity_result="$(sqlite3 data/mailer.db.restoring 'PRAGMA integrity_check;')"
  [ "$integrity_result" = "ok" ] || { echo "SQLite integrity_check failed: $integrity_result" >&2; exit 1; }
fi

mv data/mailer.db.restoring data/mailer.db

chmod 600 data/mailer.db
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml up -d mailer
```

ローカルにコピーした暗号化バックアップでも同手順を使い、`rclone copy` だけ省略します。

## 検証

呼び出し元を再有効化する前に復元サービスを確認します:

```bash
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer healthcheck
MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
docker compose --env-file .env -f compose.yml exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
docker compose --env-file .env -f compose.yml exec -T mailer curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer db stats
```

ホストで Admin UI が有効なら、承認済みリバースプロキシ経由でログイン、送信依頼一覧表示、Dead Letters ページ表示も確認します。

## ロールバック

検証が失敗したら呼び出し元は無効のままにします。`restore/previous/` の以前の DB コピーを戻すか、次に信頼できる暗号化バックアップを同手順で復元します。インシデントメモが完了するまで失敗したバックアップファイルとコンテナログを保持します。

インシデントまたはドリル後、DB ボリュームに触れず一時的なリストア資料を削除します:

```bash
MAILER_BACKUP_FILE=mailer-YYYYMMDDTHHmmssZ.db.age
rm -f ./keys/backup-age-key.txt ./restore/"$MAILER_BACKUP_FILE" ./data/mailer.db.restoring
```
