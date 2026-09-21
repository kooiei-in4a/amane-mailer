#!/usr/bin/env bash
# Fixture rehearsal for the cold instance-state backup and fresh-target restore path.
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "$BASH_SOURCE")/.." && pwd -P)"
BACKUP_SCRIPT="$REPO_ROOT/infra/deploy/backup-instance-state.sh"
RESTORE_SCRIPT="$REPO_ROOT/infra/deploy/restore-instance-state.sh"
AGE_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-age.sh"
RCLONE_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-rclone.sh"
DOCKER_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-docker.sh"

tmp_root="$(mktemp -d "/tmp/amane-mailer-instance-state-test.XXXXXX")"
cleanup() {
  local status=$?
  set +e
  rm -rf -- "$tmp_root"
  exit "$status"
}
trap cleanup EXIT

compose_dir="$tmp_root/compose"
data_dir="$compose_dir/data"
secret_path="$data_dir/secrets/acs/acs_connection_string"
committed_dir="$data_dir/attachment-spool/committed"
request_id="00000000-0000-0000-0000-000000000001"
spool_key="00000000-0000-0000-0000-000000000002"
mkdir -p -- \
  "$data_dir/secrets/acs" \
  "$committed_dir/$request_id" \
  "$data_dir/attachment-spool/staging/should-not-be-backed-up" \
  "$data_dir/logs" \
  "$data_dir/bootstrap"
touch -- "$compose_dir/compose.yml"

printf '%s\n' \
  'MAILER_DATA_PATH=./data' \
  'MAILER_COMPOSE_FILE=compose.yml' \
  'MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY=fixture-age-recipient' \
  'MAILER_BACKUP_RCLONE_REMOTE=fixture:encrypted-only' \
  'MAILER_BACKUP_REQUIRE_OFFSITE=true' \
  > "$compose_dir/.env"

python3 -c 'import sqlite3, sys; db = sqlite3.connect(sys.argv[1]); db.execute("CREATE TABLE fixture_state (id INTEGER PRIMARY KEY, value TEXT NOT NULL)"); db.execute("INSERT INTO fixture_state(value) VALUES (?)", ("cold-backup-fixture",)); db.commit(); db.close()' "$data_dir/mailer.db"
printf '%s\n' 'Endpoint=https://fixture.invalid/;AccessKey=fixture-only-not-real' > "$secret_path"
chmod 600 -- "$secret_path"
chmod 700 -- "$data_dir/secrets" "$data_dir/secrets/acs" "$data_dir/attachment-spool" "$committed_dir"
printf '%s\n' 'committed attachment bytes' > "$committed_dir/$request_id/$spool_key.bin"
chmod 600 -- "$committed_dir/$request_id/$spool_key.bin"
printf '%s\n' 'transient staging bytes' > "$data_dir/attachment-spool/staging/should-not-be-backed-up/transient.bin"
printf '%s\n' 'bootstrap token is transient' > "$data_dir/bootstrap/setup_token"
printf '%s\n' 'log data is not Mailer instance state' > "$data_dir/logs/runtime.log"

fake_bin="$tmp_root/bin"
mkdir -p -- "$fake_bin"
ln -s -- "$DOCKER_DOUBLE" "$fake_bin/docker"
ln -s -- "$AGE_DOUBLE" "$fake_bin/age"
ln -s -- "$RCLONE_DOUBLE" "$fake_bin/rclone"
export PATH="$fake_bin:$PATH"

run_backup() {
  MAILER_COMPOSE_DIR="$compose_dir" bash "$BACKUP_SCRIPT" > "$tmp_root/backup.out" 2> "$tmp_root/backup.err"
}

expect_backup_failure() {
  if run_backup; then
    echo "expected backup preflight to fail" >&2
    exit 1
  fi
}

run_backup
status_dir="$data_dir/.mailer-backup-status"
python3 - "$status_dir" "$data_dir/backups" <<'PY'
import json
import pathlib
import re
import stat
import sys

status_dir = pathlib.Path(sys.argv[1])
backup_dir = pathlib.Path(sys.argv[2])
attempt_path = status_dir / "full-instance.attempt.json"
success_path = status_dir / "full-instance.success.json"
offsite_path = status_dir / "full-instance.offsite-success.json"
attempt = json.loads(attempt_path.read_text())
success = json.loads(success_path.read_text())
offsite = json.loads(offsite_path.read_text())
expected_keys = {
    "schemaVersion", "backupType", "recordType", "status", "startedAtUtc",
    "completedAtUtc", "stage", "offsiteStatus", "artifactName",
}
assert set(attempt) == expected_keys
assert set(success) == expected_keys
assert set(offsite) == expected_keys
assert attempt["backupType"] == success["backupType"] == offsite["backupType"] == "full-instance"
assert attempt["recordType"] == "attempt" and attempt["status"] == "succeeded"
assert success["recordType"] == "success" and success["status"] == "succeeded"
assert offsite["recordType"] == "offsite-success" and offsite["status"] == "succeeded"
assert stat.S_IMODE(status_dir.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(attempt_path.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(success_path.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(offsite_path.stat().st_mode) & 0o022 == 0
assert re.fullmatch(r"mailer-state-\d{8}T\d{6}Z\.tar\.age", success["artifactName"])
assert (backup_dir / success["artifactName"]).is_file()
receipt_text = attempt_path.read_text() + success_path.read_text() + offsite_path.read_text()
assert "fixture-age-recipient" not in receipt_text
assert "fixture:encrypted-only" not in receipt_text
assert "google-client-secret-fixture-not-real" not in receipt_text
PY
archive="$(find -P "$data_dir/backups" -maxdepth 1 -type f -name 'mailer-state-*.tar.age' -print -quit)"
[ -n "$archive" ] || { echo "encrypted fixture archive was not created" >&2; exit 1; }
baseline_archive="$tmp_root/mailer-state-baseline.tar.age"
cp -- "$archive" "$baseline_archive"
plaintext_archive="$(find -P "$data_dir/backups" -maxdepth 1 -type f -name 'mailer-state-*.tar' -print -quit)"
[ -z "$plaintext_archive" ] || { echo "plaintext archive was left in the data volume" >&2; exit 1; }

tar_entries="$tmp_root/archive.entries"
tar --list --file "$archive" > "$tar_entries"
grep -Fx 'mailer.db' "$tar_entries" >/dev/null
grep -Fx 'secrets/acs/acs_connection_string' "$tar_entries" >/dev/null
grep -Fx 'attachment-spool/committed/' "$tar_entries" >/dev/null
grep -Fx "attachment-spool/committed/$request_id/" "$tar_entries" >/dev/null
grep -Fx "attachment-spool/committed/$request_id/$spool_key.bin" "$tar_entries" >/dev/null
if grep -Fx 'secrets/admin_google/client_secret' "$tar_entries" >/dev/null; then
  echo "unset Google Client Secret was included in the archive" >&2
  exit 1
fi
if grep -E '(^|/)(staging|bootstrap|logs)(/|$)' "$tar_entries" >/dev/null; then
  echo "transient or log state leaked into the archive" >&2
  exit 1
fi
if grep -F '.mailer-backup-status' "$tar_entries" >/dev/null; then
  echo "backup status receipts were included in the full-instance archive" >&2
  exit 1
fi

# Content-coverage teeth: removing either required state component must make the
# backup preflight fail instead of silently producing an incomplete archive.
rm -f -- "$data_dir/backups"/*.age
mv -- "$secret_path" "$secret_path.missing"
expect_backup_failure
mv -- "$secret_path.missing" "$secret_path"
chmod 600 -- "$secret_path"

rm -f -- "$data_dir/backups"/*.age
mv -- "$committed_dir" "$data_dir/attachment-spool/committed.missing"
expect_backup_failure
mv -- "$data_dir/attachment-spool/committed.missing" "$committed_dir"
chmod 700 -- "$committed_dir"

# Cold-point safety tooth: a running Mailer must make the backup RED before any
# archive is produced.
rm -f -- "$data_dir/backups"/*.age
if BACKUP_TEST_DOCKER_RUNNING=true run_backup; then
  echo "expected backup preflight to reject a running Mailer" >&2
  exit 1
fi
[ -z "$(find -P "$data_dir/backups" -maxdepth 1 -type f -name 'mailer-state-*.age' -print -quit)" ]

restore_target="$tmp_root/restored-data"
identity="$tmp_root/backup-age-key.txt"
printf '%s\n' 'AGE-SECRET-KEY-1-fixture-test-only' > "$identity"
chmod 600 -- "$identity"
bash "$RESTORE_SCRIPT" \
  --archive "$baseline_archive" \
  --identity "$identity" \
  --target "$restore_target" \
  --runtime-uid "$(id -u)" \
  --runtime-gid "$(id -g)" \
  > "$tmp_root/restore.out" 2> "$tmp_root/restore.err"

cmp -- "$data_dir/mailer.db" "$restore_target/mailer.db"
cmp -- "$secret_path" "$restore_target/secrets/acs/acs_connection_string"
cmp -- \
  "$committed_dir/$request_id/$spool_key.bin" \
  "$restore_target/attachment-spool/committed/$request_id/$spool_key.bin"
[ "$(stat -c '%a' "$restore_target/secrets/acs/acs_connection_string")" = 600 ]
[ "$(stat -c '%u:%g' "$restore_target/secrets/acs/acs_connection_string")" = "$(id -u):$(id -g)" ]
[ ! -e "$restore_target/attachment-spool/staging" ]
[ ! -e "$restore_target/bootstrap" ]
[ ! -e "$restore_target/logs" ]
[ ! -e "$restore_target/.mailer-backup-status" ]
[ ! -e "$restore_target/secrets/admin_google/client_secret" ]
python3 -c 'import sqlite3, sys; db = sqlite3.connect(sys.argv[1]); assert db.execute("SELECT value FROM fixture_state").fetchone() == ("cold-backup-fixture",); db.close()' "$restore_target/mailer.db"

# Non-empty-target safety tooth: a restore must be RED and must preserve the
# sentinel when an operator points it at an existing directory.
nonempty_target="$tmp_root/nonempty-target"
mkdir -p -- "$nonempty_target"
printf '%s\n' 'must survive refusal' > "$nonempty_target/sentinel"
if bash "$RESTORE_SCRIPT" \
  --archive "$baseline_archive" \
  --identity "$identity" \
  --target "$nonempty_target" \
  --runtime-uid "$(id -u)" \
  --runtime-gid "$(id -g)" \
  > "$tmp_root/nonempty.out" 2> "$tmp_root/nonempty.err"; then
  echo "restore unexpectedly accepted a non-empty target" >&2
  exit 1
fi
grep -Fx 'must survive refusal' "$nonempty_target/sentinel" >/dev/null

# Optional managed Google Client Secret: present secrets are archived and
# restored with owner-only permissions. Logs must not contain the secret.
google_secret_dir="$data_dir/secrets/admin_google"
google_secret_path="$google_secret_dir/client_secret"
google_secret_value='google-client-secret-fixture-not-real'
mkdir -p -- "$google_secret_dir"
chmod 700 -- "$google_secret_dir"
printf '%s\n' "$google_secret_value" > "$google_secret_path"
chmod 600 -- "$google_secret_path"
rm -f -- "$data_dir/backups"/*.age
run_backup
google_archive="$(find -P "$data_dir/backups" -maxdepth 1 -type f -name 'mailer-state-*.tar.age' -print -quit)"
[ -n "$google_archive" ] || { echo "encrypted fixture archive was not created" >&2; exit 1; }
google_entries="$tmp_root/google-archive.entries"
tar --list --file "$google_archive" > "$google_entries"
grep -Fx 'secrets/admin_google/client_secret' "$google_entries" >/dev/null
grep -Fx 'mailer.db' "$google_entries" >/dev/null
grep -Fx 'secrets/acs/acs_connection_string' "$google_entries" >/dev/null
if grep -E '(^|/)(staging|bootstrap|logs)(/|$)' "$google_entries" >/dev/null; then
  echo "transient or log state leaked into the Google secret archive" >&2
  exit 1
fi
google_restore="$tmp_root/restored-google"
bash "$RESTORE_SCRIPT" \
  --archive "$google_archive" \
  --identity "$identity" \
  --target "$google_restore" \
  --runtime-uid "$(id -u)" \
  --runtime-gid "$(id -g)" \
  > "$tmp_root/restore-google.out" 2> "$tmp_root/restore-google.err"
cmp -- "$google_secret_path" "$google_restore/secrets/admin_google/client_secret"
cmp -- "$data_dir/mailer.db" "$google_restore/mailer.db"
cmp -- "$secret_path" "$google_restore/secrets/acs/acs_connection_string"
[ "$(stat -c '%a' "$google_restore/secrets/admin_google/client_secret")" = 600 ]
[ "$(stat -c '%a' "$google_restore/secrets/admin_google")" = 700 ]
[ "$(stat -c '%u:%g' "$google_restore/secrets/admin_google/client_secret")" = "$(id -u):$(id -g)" ]
if grep -F "$google_secret_value" \
  "$tmp_root/backup.out" "$tmp_root/backup.err" \
  "$tmp_root/restore-google.out" "$tmp_root/restore-google.err" >/dev/null; then
  echo "Google Client Secret leaked into backup or restore logs" >&2
  exit 1
fi

# A non-owner-only Google secret must fail closed and must not be echoed.
rm -f -- "$data_dir/backups"/*.age
chmod 644 -- "$google_secret_path"
if run_backup; then
  echo "expected backup to reject a Google Client Secret that is not owner-only" >&2
  exit 1
fi
if grep -F "$google_secret_value" "$tmp_root/backup.out" "$tmp_root/backup.err" >/dev/null; then
  echo "Google Client Secret leaked into backup failure logs" >&2
  exit 1
fi
[ -z "$(find -P "$data_dir/backups" -maxdepth 1 -type f -name 'mailer-state-*.age' -print -quit)" ]
chmod 600 -- "$google_secret_path"

echo "instance-state-backup-self-test: ok"
