[English](restore-procedure.en.md)

# リストア手順

この runbook は、v2 managed self-hosted Amane Mailer の coordinated/cold
instance-state archive を、使い捨てまたは新規の空 data directory に復元する手順です。
DB 単体の mailer-*.db.age は SQLite の緊急／オンラインスナップショットであり、
provider secret や committed attachment spool を含まないため、この full restore の代替では
ありません。

実環境の切り替えは破壊的になり得ます。既存の稼働 data directory に上書き復元せず、
まず検証用の新しい target で起動・readiness を確認し、切り替えの判断を operator が行います。
この手順と restore-instance-state.sh に --force や無言の overwrite はありません。

## 前提条件

- restore-verification.md で選択した
  mailer-state-YYYYMMDDTHHmmssZ.tar.age の検証が成功していること。
- 対応する age identity を非公開の key manager から取得済みで、リポジトリと backup
  remote の外に置いていること。identity のモードは owner-only (600) にする。
- 対象 checkout に compose.yml、VPS managed-v2 なら compose.vps-dogfood.yml、
  .env があること。
- MAILER_DATA_PATH の既存 directory を restore target に指定しないこと。target は
  fresh または空の絶対パスにする。
- コンテナの実行 UID/GID を対象 image または private deployment metadata から確認済み
  であること。Dockerfile の数字を前提にせず、docker image inspect の Config.User
  と実際の runtime identity を照合して --runtime-uid / --runtime-gid に渡す。
- Mailer と migration/admin mutator が停止していること。script は stop/start を行わない。
- Caddy の caddy_data / caddy_config は Mailer archive と別に扱うこと。

## 復元される authority

full archive の復元単位は次の固定された state です:

- mailer.db（managed provider、sender、admin credential epoch、request/evidence を含む）
- secrets/acs/acs_connection_string（コンテナ内 /app/data/secrets/acs/acs_connection_string）
- attachment-spool/committed/（未完了 accepted request が必要とする opaque files）

attachment-spool/staging、bootstrap token、logs、data/backups、tenant JSON、.env、
platform-sender.json、age private key、外部 bounce queue secret、Caddy volume は
archive に入りません。外部 secret が有効な構成は、同じ参照先を restore 前に別途用意します。

initialized DB の provider secret は SQLite の provider_secret_ref が authority です。
canonical secret が欠落または壊れている場合、Mailer は /readyz を
503 provider_secret_missing にし、/setup を 404 のままにします。bare
ACS_CONNECTION_STRING 環境変数を fallback にしたり、setup を再開したりしないでください。

## 空 target への復元

以下の例では、/path/to/amane-mailer はチェックアウト、/path/to/mailer は
実際の Compose ディレクトリです。値は private な運用値に置き換え、secret 自体は
コマンドやログに貼りません。

1. Compose の構成を検査し、Mailer の mutator を停止します:

~~~bash
set -Eeuo pipefail
cd /path/to/mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer mailer-migrate mailer-acs-admin 2>/dev/null || true

mkdir -p ./restore ./keys ./secrets/acs
chmod 700 ./restore ./keys ./secrets/acs
chmod 700 ./secrets
~~~

stop の後に次を実行し、mailer、mailer-migrate、mailer-acs-admin が running
でないことを確認します。caddy は edge state の所有者なので、Mailer archive の cold
point を妨げない限り別管理です:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps
~~~

2. 非公開 key manager から identity を一時コピーし、権限を固定します。暗号化 archive
   が remote にしかない場合だけ次の rclone copy を使い、すでに ./restore/ にある
   場合は省略します:

~~~bash
chmod 600 ./keys/backup-age-key.txt
MAILER_BACKUP_FILE=mailer-state-YYYYMMDDTHHmmssZ.tar.age
MAILER_BACKUP_RCLONE_REMOTE=remote:bucket-or-prefix/mailer/
rclone copy "$MAILER_BACKUP_RCLONE_REMOTE" ./restore --include "$MAILER_BACKUP_FILE"
~~~

3. 新しい空 target を作成し、runtime UID/GID を指定して helper を実行します。既存の
   ./data を target に指定すると拒否されます:

~~~bash
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

上の 1654 は説明用の placeholder です。実行時は必ず image/runtime から確認した
UID/GID に置き換えてください。helper は age で一時領域へ復号し、archive entry を
固定 boundary と照合してから抽出し、DB と provider secret を 600、secret/spool
directory を owner-only にします。migration、サービス起動、Caddy 操作は行いません。

## 復元データの migration と readiness

検証用 Compose は、MAILER_DATA_PATH を新しい target に一時的に向けます。元の
.env と元の ./data は変更せず、shell の環境変数で target を override します。
VPS overlay の /run/secrets/acs は read-only の互換 mount です。managed v2 の
authority は restore された /app/data/secrets/acs/acs_connection_string であり、
別の secret をそこへ登録して二重管理しません。

~~~bash
export MAILER_DATA_PATH="$RESTORE_TARGET"
export MAILER_COMPOSE_FILE=compose.yml:compose.vps-dogfood.yml

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d mailer
~~~

migration が失敗したら Mailer を起動せず、target を破棄して別の検証済み archive を
使います。起動後、呼び出し元を戻す前に /healthz、/readyz、DB stats を確認します:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood exec -T mailer \
  /app/Amane.Mailer healthcheck
curl -fsS https://mailer.example.invalid/healthz
curl -fsS https://mailer.example.invalid/readyz
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood exec -T mailer \
  /app/Amane.Mailer db stats
~~~

/setup が 404 であることも確認します。initialized DB で provider secret を
意図的に欠落／破損させた検証では、/readyz が 503 かつ JSON reason
provider_secret_missing、/setup が 404 になることを確認します。その状態で
setup token を使った再初期化や bare environment fallback を試してはいけません。
これは障害時にも同じ fail-safe 契約です。

security-sensitive な時点へ戻した場合、DB には当時の API-key hash、credential epoch、
revocation/session 状態が含まれます。restore 後に管理者 credential、API key、不要な
session/revocation state、外部 secret を review し、必要なら operator の承認済み手順で
rotate/revoke します。

## 切り替え・ロールバック・cleanup

検証が通るまで元の MAILER_DATA_PATH と edge を変更しません。切り替え時は対象の
maintenance 手順で target を正式な data path として設定し、Compose config、ownership、
readiness を再確認します。失敗時は Mailer を止め、MAILER_DATA_PATH を元へ戻して
元の state を維持します。helper は既存 data を変更しないため、DB 単体の
restore/previous コピーを上書きするロールバックは不要です。

ドリル完了後、監査・インシデント記録が済んでから、明示した disposable target、
downloaded .tar.age、一時 identity だけを削除します。元の data volume、Caddy named
volume、key vault の recovery copy は削除しません:

~~~bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood stop mailer
rm -f -- "./restore/$MAILER_BACKUP_FILE" ./keys/backup-age-key.txt
rm -rf -- "$RESTORE_TARGET"
~~~
