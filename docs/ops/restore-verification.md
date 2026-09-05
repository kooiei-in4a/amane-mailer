[English](restore-verification.en.md)

# リストア検証

初回の offsite backup 後、backup script の変更後、migration の大きな変更後、および
operator が決めた周期で、使い捨て環境に full instance archive を復元して検証します。
このドリルは実 ACS 送信、実 recipient、実 provider secret を使いません。検証用の
fake SQLite／fake secret／fake committed spool を使う自動 fixture は次で実行できます:

~~~bash
bash /path/to/amane-mailer/scripts/backup-instance-state-self-test.sh
~~~

この fixture は age/rclone/docker の test double を使い、archive の encrypt/decrypt
経路、内容一致、除外境界、欠落 state の RED、非空 target 拒否を確認します。test double
は本番暗号化の代替ではありません。

## ドリルの安全境界

- 本番の MAILER_DATA_PATH、Compose project、Caddy named volume を使わない。
- docker compose down -v、volume prune、既存 data directory の削除や overwrite を
  行わない。
- restore helper は fresh または空の target だけを受け付ける。sentinel を置いた非空
  directory への実行が失敗し、sentinel が残ることを確認する。
- age identity は一時 path に置き、owner-only (600) にする。実 secret、recipient、
  bearer token、recipient address をログや issue に出さない。
- bounce ingestion を有効にせず、実 ACS endpoint へ接続しない。readiness の確認は
  health endpoint と DB／filesystem state に限定する。
- Caddy の caddy_data / caddy_config は復元せず、Mailer state と混ぜない。

## 使い捨て Compose の準備

以下では checkout を /path/to/amane-mailer、host Compose directory を
/path/to/mailer とします。VPS overlay を使わない場合は compose.vps-dogfood.yml を
各コマンドから外します:

~~~bash
set -Eeuo pipefail
export COMPOSE_PROJECT_NAME=amane-mailer-restore-check
cd /path/to/mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet

mkdir -p ./restore ./keys ./secrets/acs
chmod 700 ./restore ./keys ./secrets ./secrets/acs
chmod 600 ./keys/backup-age-key.txt
~~~

encrypted full archive を private remote から取得する場合:

~~~bash
MAILER_BACKUP_FILE=mailer-state-YYYYMMDDTHHmmssZ.tar.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
~~~

すでに承認済み archive を ./restore にコピー済みなら rclone は省略します。

## Full archive の復元と内容確認

Mailer runtime と migration/admin mutator が動いていないことを確認し、fresh target を
作成して helper を実行します。runtime UID/GID は image/runtime から確認した値に置き換え、
説明用の 1654 を盲目的に使わないでください:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps

RESTORE_TARGET="$(mktemp -d "$PWD/restore-mailer-data.XXXXXX")"
MAILER_RUNTIME_UID=1654
MAILER_RUNTIME_GID=1654

bash /path/to/amane-mailer/infra/deploy/restore-instance-state.sh \
  --archive "$PWD/restore/$MAILER_BACKUP_FILE" \
  --identity "$PWD/keys/backup-age-key.txt" \
  --target "$RESTORE_TARGET" \
  --runtime-uid "$MAILER_RUNTIME_UID" \
  --runtime-gid "$MAILER_RUNTIME_GID"
~~~

成功した target について、次を確認します:

- mailer.db が存在し、helper が provider secret と同じ archive から復元した。
- secrets/acs/acs_connection_string が存在し、owner-only directory と file mode 600
  である。
- attachment-spool/committed/ とその opaque request/spool files が存在し、元の fixture
  または backup 時点の private inventory と byte-for-byte に一致する。
- attachment-spool/staging、bootstrap、logs、data/backups が作成されていない。
- age identity と archive の平文 tar が target や data volume に作成されていない。
- 非空 target の拒否テストが sentinel を保持している。

sqlite3 が利用可能なら DB integrity を補助的に確認します:

~~~bash
sqlite3 "$RESTORE_TARGET/mailer.db" 'PRAGMA integrity_check;'
~~~

期待値は ok です。これは migration の代わりではありません。

## Migration、起動、readiness

target を使う検証 project にだけ data path を向け、元の ./data は変更しません。
VPS overlay の external ACS mount は read-only compatibility mount として用意し、managed
v2 の provider authority をそこへ複製しません:

~~~bash
export MAILER_DATA_PATH="$RESTORE_TARGET"
export MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d mailer

curl -fsS https://mailer.example.invalid/healthz
curl -fsS https://mailer.example.invalid/readyz
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec -T mailer /app/Amane.Mailer db stats
~~~

確認結果には migration の終了 status、health/readiness の HTTP status、DB stats、
復元した committed spool の件数、所要時間、使用 archive、runtime image tag、
ownership/mode を記録します。実 provider send の証拠はこのドリルの目的ではありません。

initialized DB の provider secret を検証用 target で一時的に欠落または破損させて再起動
した場合、次を期待します:

- /readyz は HTTP 503 で JSON reason が provider_secret_missing。
- /setup は HTTP 404。
- bare ACS_CONNECTION_STRING を追加しても fallback しない。
- setup token で再初期化できない。

negative test 後は target を破棄し、元の archive と本番 data を変更しません。

## 完了と cleanup

Mailer を停止して verification project を削除し、証跡を非公開運用メモへ保存します。
監査・インシデント記録が済んだ後で、使い捨て target、downloaded archive、一時 identity
だけを明示的に削除します。Caddy named volume と key vault recovery copy は削除しません:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer
rm -f -- "./restore/$MAILER_BACKUP_FILE" ./keys/backup-age-key.txt
rm -rf -- "$RESTORE_TARGET"
~~~
