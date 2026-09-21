#!/usr/bin/env bash
# Fixture rehearsal for the DB-only encrypted backup receipt contract.
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "$BASH_SOURCE")/.." && pwd -P)"
BACKUP_SCRIPT="$REPO_ROOT/infra/deploy/backup-mailer.sh"
AGE_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-age.sh"
RCLONE_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-rclone.sh"
DOCKER_DOUBLE="$REPO_ROOT/scripts/test-fixtures/backup-test-docker.sh"

tmp_root="$(mktemp -d "/tmp/amane-mailer-db-backup-test.XXXXXX")"
cleanup() {
  local status=$?
  set +e
  rm -rf -- "$tmp_root"
  exit "$status"
}
trap cleanup EXIT

compose_dir="$tmp_root/compose"
data_dir="$compose_dir/data"
mkdir -p -- "$data_dir"
touch -- "$compose_dir/compose.yml"
printf '%s\n' \
  'MAILER_DATA_PATH=./data' \
  'MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY=fixture-recipient-not-real' \
  'MAILER_BACKUP_RCLONE_REMOTE=fixture:remote-name-not-for-receipts' \
  'MAILER_BACKUP_REQUIRE_OFFSITE=true' \
  > "$compose_dir/.env"

fake_bin="$tmp_root/bin"
mkdir -p -- "$fake_bin"
ln -s -- "$DOCKER_DOUBLE" "$fake_bin/docker"
ln -s -- "$AGE_DOUBLE" "$fake_bin/age"
ln -s -- "$RCLONE_DOUBLE" "$fake_bin/rclone"
export PATH="$fake_bin:$PATH"

run_backup() {
  MAILER_COMPOSE_DIR="$compose_dir" \
    BACKUP_TEST_DATA_DIR="$data_dir" \
    bash "$BACKUP_SCRIPT" > "$tmp_root/backup.out" 2> "$tmp_root/backup.err"
}

expect_backup_failure() {
  if env "$@" MAILER_COMPOSE_DIR="$compose_dir" \
    BACKUP_TEST_DATA_DIR="$data_dir" \
    bash "$BACKUP_SCRIPT" > "$tmp_root/failure.out" 2> "$tmp_root/failure.err"; then
    echo "expected DB-only backup fixture to fail" >&2
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
attempt_path = status_dir / "db-only.attempt.json"
success_path = status_dir / "db-only.success.json"
offsite_path = status_dir / "db-only.offsite-success.json"
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
assert attempt["backupType"] == success["backupType"] == offsite["backupType"] == "database-only"
assert attempt["recordType"] == "attempt" and attempt["status"] == "succeeded"
assert success["recordType"] == "success" and success["status"] == "succeeded"
assert offsite["recordType"] == "offsite-success" and offsite["status"] == "succeeded"
assert stat.S_IMODE(status_dir.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(attempt_path.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(success_path.stat().st_mode) & 0o022 == 0
assert stat.S_IMODE(offsite_path.stat().st_mode) & 0o022 == 0
assert attempt["offsiteStatus"] == success["offsiteStatus"] == offsite["offsiteStatus"] == "succeeded"
assert re.fullmatch(r"mailer-\d{8}T\d{6}Z\.db\.age", success["artifactName"])
assert (backup_dir / success["artifactName"]).is_file()
assert not list(backup_dir.glob("mailer-*.db"))
receipt_text = attempt_path.read_text() + success_path.read_text() + offsite_path.read_text()
assert "fixture-recipient-not-real" not in receipt_text
assert "remote-name-not-for-receipts" not in receipt_text
assert "rclone.conf" not in receipt_text
PY

sleep 1
expect_backup_failure BACKUP_TEST_DOCKER_FAIL_BACKUP=true
python3 - "$status_dir" <<'PY'
import json
import pathlib
import sys

status_dir = pathlib.Path(sys.argv[1])
attempt = json.loads((status_dir / "db-only.attempt.json").read_text())
assert attempt["status"] == "failed"
assert attempt["stage"] == "database"
assert attempt["offsiteStatus"] == "not-attempted"
PY

sleep 1
expect_backup_failure BACKUP_TEST_RCLONE_FAIL=true
python3 - "$status_dir" <<'PY'
import json
import pathlib
import sys

status_dir = pathlib.Path(sys.argv[1])
attempt = json.loads((status_dir / "db-only.attempt.json").read_text())
success = json.loads((status_dir / "db-only.success.json").read_text())
offsite = json.loads((status_dir / "db-only.offsite-success.json").read_text())
assert attempt["status"] == "failed"
assert attempt["stage"] == "upload"
assert attempt["offsiteStatus"] == "failed"
assert success["status"] == "succeeded"
assert offsite["status"] == "succeeded"
PY

echo "db-only-backup-self-test: ok"
